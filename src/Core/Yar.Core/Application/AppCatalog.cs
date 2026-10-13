using System.Diagnostics;

namespace Yar.Core;

public sealed record ProviderReport(string ProviderId, ProviderStatus Status)
{
    /// <summary>Set when looking for updates failed. The apps are still listed.</summary>
    public string? UpdateCheckError { get; init; }

    /// <summary>When the provider's knowledge about updates was last refreshed, if it says.</summary>
    public DateTimeOffset? UpdatesRefreshedAt { get; init; }
}

/// <summary>The same app installed from more than one provider.</summary>
public sealed record DuplicateGroup(string Key, IReadOnlyList<AppInfo> Apps);

public sealed record CatalogSnapshot(
    IReadOnlyList<ProviderReport> Providers,
    IReadOnlyList<SourceInfo> Sources,
    IReadOnlyList<AppInfo> Apps,
    IReadOnlyList<DuplicateGroup> Duplicates,
    IReadOnlyList<DesktopEntry> UnclaimedLaunchers,
    IReadOnlyList<UpdateInfo> Updates,
    IReadOnlyList<ProviderFacts> Facts)
{
    /// <summary>Data left behind by apps that are no longer installed.</summary>
    public IReadOnlyList<LeftoverData> Leftovers { get; init; } = [];

    /// <summary>How long each stage of the load took. For diagnosis; not part of what the user sees.</summary>
    public IReadOnlyList<StageTiming> Timings { get; init; } = [];
}

public sealed record StageTiming(string Stage, double Milliseconds);

/// <summary>
/// Loads sources and installed apps from all providers. Providers claim launchers in the order
/// they were given; each one only sees what the earlier ones left.
/// </summary>
public sealed class AppCatalog
{
    private readonly List<ISoftwareProvider> _providers;
    private readonly Func<CancellationToken, Task<DesktopEntryIndex>> _loadLaunchers;
    private readonly ICatalogAnnotator? _annotator;
    private readonly ICacheStore? _cache;

    public AppCatalog(
        IEnumerable<ISoftwareProvider> providersInClaimOrder,
        Func<CancellationToken, Task<DesktopEntryIndex>> loadLaunchers,
        ICatalogAnnotator? annotator = null,
        ICacheStore? cache = null)
    {
        _annotator = annotator;
        _cache = cache;
        _providers = providersInClaimOrder.ToList();
        _loadLaunchers = loadLaunchers;
    }

    /// <summary>
    /// What the last complete load found, read from the saved index without asking any backend.
    /// Null when nothing usable is saved.
    /// </summary>
    public CatalogSnapshot? LoadCached() => _cache is null ? null : SnapshotCache.Read(_cache);

    /// <summary>Everything: apps and sources first, then updates. The result is saved for the next start.</summary>
    public async Task<CatalogSnapshot> LoadAsync(CancellationToken ct) =>
        await LoadUpdatesAsync(await LoadAppsAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>Phase one: sources and installed apps. Asks no backend about updates.</summary>
    public async Task<CatalogSnapshot> LoadAppsAsync(CancellationToken ct)
    {
        var timings = new Timings();
        var statuses = await timings.Measure("probe", () => Task.WhenAll(_providers.Select(p => Guarded(
            () => p.ProbeAsync(ct), ProviderStatus.Error, ct)))).ConfigureAwait(false);

        var reports = new ProviderReport[_providers.Count];
        for (var i = 0; i < _providers.Count; i++)
            reports[i] = new(_providers[i].Id, statuses[i]);

        var sourceTasks = _providers.Select((p, i) => IsAvailable(reports[i])
            ? timings.Measure($"sources:{p.Id}", () => Guarded<IReadOnlyList<SourceInfo>?>(
                async () => await p.GetSourcesAsync(ct).ConfigureAwait(false),
                reason => { reports[i] = new(p.Id, ProviderStatus.Error(reason)); return null; }, ct))
            : Task.FromResult<IReadOnlyList<SourceInfo>?>(null)).ToList();

        var launchers = await timings.Measure("launchers", () => _loadLaunchers(ct)).ConfigureAwait(false);
        var unclaimed = launchers;
        var apps = new List<AppInfo>();
        for (var i = 0; i < _providers.Count; i++)
        {
            if (!IsAvailable(reports[i]))
                continue;

            var provider = _providers[i];
            var index = i;
            var offered = unclaimed;
            var claimed = await timings.Measure($"claim:{provider.Id}", () => Guarded<IReadOnlyList<AppInfo>?>(
                async () => await provider.ClaimAppsAsync(offered, ct).ConfigureAwait(false),
                reason => { reports[index] = new(provider.Id, ProviderStatus.Error(reason)); return null; },
                ct)).ConfigureAwait(false);
            if (claimed is null)
                continue;

            apps.AddRange(claimed);
            unclaimed = unclaimed.Without(claimed.SelectMany(a => a.LauncherPaths).ToHashSet(StringComparer.Ordinal));
        }

        IReadOnlyList<SourceInfo> sources = (await Task.WhenAll(sourceTasks).ConfigureAwait(false))
            .Where(s => s is not null)
            .SelectMany(s => s!)
            .ToList();

        IReadOnlyList<AppInfo> annotatedApps = apps;
        if (_annotator is not null)
        {
            // Annotation is a nicety: if it fails, show what the providers found as it is.
            var found = sources;
            (sources, annotatedApps) = await timings.Measure("annotate", () => Guarded(
                () => _annotator.AnnotateAsync(found, apps, launchers, ct),
                _ => (found, apps), ct)).ConfigureAwait(false);
        }

        return new(
            reports, sources, annotatedApps, FindDuplicates(annotatedApps), unclaimed.Entries,
            Updates: [], _providers.Select(p => p.Facts).ToList())
        {
            Timings = timings.ToList(),
        };
    }

    /// <summary>
    /// Phase two: adds the waiting updates to the result of <see cref="LoadAppsAsync"/> and saves
    /// the complete snapshot for the next start.
    /// </summary>
    public async Task<CatalogSnapshot> LoadUpdatesAsync(CatalogSnapshot apps, CancellationToken ct)
    {
        var timings = new Timings();
        var reports = apps.Providers.ToArray();
        var updates = await CollectUpdatesAsync(reports, apps.Apps, timings, ct).ConfigureAwait(false);

        var leftovers = await Task.WhenAll(_providers.Select((provider, i) =>
            provider is ILeftoverProvider finder && IsAvailable(reports[i])
                ? timings.Measure($"leftovers:{provider.Id}", () => Guarded<IReadOnlyList<LeftoverData>>(
                    () => finder.FindLeftoversAsync(apps.Apps.Where(a => a.ProviderId == provider.Id).ToList(), ct),
                    // Not being able to look for leftovers is no reason to trouble the user.
                    _ => [], ct))
                : Task.FromResult<IReadOnlyList<LeftoverData>>([]))).ConfigureAwait(false);

        var complete = apps with
        {
            Providers = reports,
            Updates = updates,
            Leftovers = leftovers.SelectMany(found => found).ToList(),
            Timings = [.. apps.Timings, .. timings.ToList()],
        };
        // A load during which a provider failed is shown, with a warning, but not saved: the next
        // start should open on the last complete picture, not on one with apps missing.
        if (_cache is not null && reports.All(r => r.Status.Availability != ProviderAvailability.Error))
            SnapshotCache.Write(_cache, complete);
        return complete;
    }

    // A failed update check is noted on the provider's report; it never hides the apps.
    private async Task<List<UpdateInfo>> CollectUpdatesAsync(
        ProviderReport[] reports, IReadOnlyList<AppInfo> apps, Timings timings, CancellationToken ct)
    {
        var checks = await Task.WhenAll(_providers.Select((provider, i) =>
            provider is IUpdateProvider updater && IsAvailable(reports[i])
                ? timings.Measure($"updates:{provider.Id}", () => Guarded<(UpdateCheck? Check, string? Error)>(
                    async () => (await updater.GetUpdatesAsync(
                        apps.Where(a => a.ProviderId == provider.Id).ToList(), ct).ConfigureAwait(false), null),
                    reason => (null, reason), ct))
                : Task.FromResult<(UpdateCheck? Check, string? Error)>((null, null)))).ConfigureAwait(false);

        var updates = new List<UpdateInfo>();
        for (var i = 0; i < checks.Length; i++)
        {
            var (check, error) = checks[i];
            // Also clears what an earlier check left on a report that is being refreshed.
            reports[i] = reports[i] with { UpdateCheckError = error, UpdatesRefreshedAt = check?.LastRefreshed };
            if (check is null)
                continue;
            updates.AddRange(check.Updates);
        }
        return updates;
    }

    private static bool IsAvailable(ProviderReport report) =>
        report.Status.Availability == ProviderAvailability.Available;

    // One failing provider must not fail the whole load.
    private static async Task<T> Guarded<T>(Func<Task<T>> action, Func<string, T> onError, CancellationToken ct)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            return onError(e.Message);
        }
    }

    /// <summary>Collects stage durations; stages may run at the same time.</summary>
    private sealed class Timings
    {
        private readonly List<StageTiming> _stages = [];

        public async Task<T> Measure<T>(string stage, Func<Task<T>> action)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                lock (_stages)
                    _stages.Add(new(stage, Math.Round(elapsed, 1)));
            }
        }

        public List<StageTiming> ToList()
        {
            lock (_stages)
                return [.. _stages];
        }
    }

    private static List<DuplicateGroup> FindDuplicates(IReadOnlyList<AppInfo> apps) =>
        apps.GroupBy(DuplicateKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(a => a.ProviderId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => new DuplicateGroup(g.Key, g.ToList()))
            .ToList();

    private static string DuplicateKey(AppInfo app)
    {
        var id = app.ComponentId;
        if (id is not null)
            return id.EndsWith(".desktop", StringComparison.Ordinal) ? id[..^".desktop".Length] : id;
        return "name:" + string.Concat(app.Name.Where(char.IsLetterOrDigit));
    }
}
