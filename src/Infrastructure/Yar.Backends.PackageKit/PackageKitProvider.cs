using Yar.Core;

namespace Yar.Backends.PackageKit;

/// <summary>
/// The distribution's own packages and repositories, through PackageKit. That one interface
/// covers dnf, apt, zypper and pacman.
/// </summary>
public sealed class PackageKitProvider : ISoftwareProvider, IUpdateProvider, ISourceManager, IOfferSizeProvider
{
    public const string ProviderId = "packagekit";

    private const int MaxParallelSearches = 4;
    private const int RecheckAttempts = 4;

    private static readonly string[] RepoConfigDirs =
        ["/etc/yum.repos.d", "/etc/apt/sources.list.d", "/etc/zypp/repos.d"];

    private readonly IPackageKitClient _client;
    private readonly IReadOnlyList<IRepoDetailReader> _detailReaders;
    private readonly IReadOnlyList<string> _launcherDirs;
    private readonly ICacheStore? _cache;
    private readonly TimeSpan _recheckWait;
    private readonly IReadOnlyList<IRepoListFallback> _repoFallbacks;

    /// <param name="systemApplicationDirs">
    /// Directories whose launchers may belong to a package, e.g. <c>/usr/share/applications</c>.
    /// </param>
    public PackageKitProvider(
        IPackageKitClient client,
        IReadOnlyList<IRepoDetailReader> detailReaders,
        IReadOnlyList<string> systemApplicationDirs,
        string? distroHomepage = null,
        ICacheStore? cache = null,
        IReadOnlyList<IRepoListFallback>? repoFallbacks = null,
        TimeSpan? recheckWait = null)
    {
        _recheckWait = recheckWait ?? TimeSpan.FromSeconds(1.5);
        _repoFallbacks = repoFallbacks ?? [];
        _cache = cache;
        Facts = new(ProviderId, distroHomepage, RepoConfigDirs.Where(Directory.Exists)
            .Select(d => new ProviderLocation("The list of sources", d)).ToList());
        _client = client;
        _detailReaders = detailReaders;
        _launcherDirs = systemApplicationDirs.Select(d => d.TrimEnd('/') + "/").ToList();
    }

    public string Id => ProviderId;

    public ProviderFacts Facts { get; }

    public Task<OfferSize?> GetSizeAsync(StoreOffer offer, CancellationToken ct) =>
        offer.ProviderId == ProviderId && IsPackageName(offer.PackageName)
            ? _client.GetOfferSizeAsync(offer.PackageName!, ct)
            : Task.FromResult<OfferSize?>(null);

    // Package names come from catalog files; only plain names are passed on to the daemon.
    private static bool IsPackageName(string? name) =>
        name is { Length: > 0 and <= 255 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+');

    // Repositories that are already configured can be switched on and off. Adding a new one means
    // writing system files as root, which Yar does not do.
    public bool CanAdd => false;

    public Task AddAsync(SourceRequest request, CancellationToken ct) =>
        throw new NotSupportedException("Package sources cannot be added from here yet.");

    public bool CanRemove(SourceInfo source) => false;

    public Task RemoveAsync(SourceInfo source, CancellationToken ct) =>
        throw new NotSupportedException("Package sources cannot be removed from here yet.");

    public bool CanToggle(SourceInfo source) =>
        source.ProviderId == ProviderId && source.Id.Length > 0 && !source.Technical.ContainsKey("read-only");

    public Task SetEnabledAsync(SourceInfo source, bool enabled, CancellationToken ct) =>
        CanToggle(source)
            ? _client.SetRepoEnabledAsync(source.Id, enabled, ct)
            : throw new InvalidOperationException("This source cannot be switched from here.");

    public async Task<UpdateCheck> GetUpdatesAsync(IReadOnlyList<AppInfo> installed, CancellationToken ct)
    {
        var packages = await _client.GetUpdatesAsync(ct).ConfigureAwait(false);
        var age = await _client.GetTimeSinceRefreshAsync(ct).ConfigureAwait(false);
        var apps = installed.GroupBy(a => a.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var updates = packages.Select(package =>
        {
            var id = $"{package.Name}.{package.Arch}";
            apps.TryGetValue(id, out var app);
            return new UpdateInfo(
                ProviderId, id, app?.Name ?? package.Name, app?.Version, package.Version,
                SeverityOf(package.Info),
                // For an update the data field is the repository it will come from.
                SourceId: package.Data.Length > 0 && !package.Data.Contains(':') ? package.Data : package.OriginRepo,
                AppId: app?.Id);
        }).ToList();

        return new UpdateCheck(updates, age is { } elapsed ? DateTimeOffset.UtcNow - elapsed : null);
    }

    /// <summary>Maps PackageKit's PK_INFO_ENUM_* values (pk-enum.h) to what Yar shows.</summary>
    public static UpdateSeverity SeverityOf(uint info) => info switch
    {
        8 => UpdateSeverity.Security,               // PK_INFO_ENUM_SECURITY
        6 or 7 => UpdateSeverity.BugFix,            // PK_INFO_ENUM_BUGFIX, PK_INFO_ENUM_IMPORTANT
        4 => UpdateSeverity.Enhancement,            // PK_INFO_ENUM_ENHANCEMENT
        _ => UpdateSeverity.Normal,
    };

    public async Task<ProviderStatus> ProbeAsync(CancellationToken ct) =>
        await _client.GetBackendAsync(ct).ConfigureAwait(false) is null
            ? ProviderStatus.NotInstalled
            : ProviderStatus.Available;

    public async Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken ct)
    {
        var backend = await _client.GetBackendAsync(ct).ConfigureAwait(false);
        var repos = await _client.GetRepoListAsync(ct).ConfigureAwait(false);
        var details = _detailReaders.SelectMany(reader => reader.Read())
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

        // Some backends report no repositories at all; then the package manager's own files are read instead.
        var listedElsewhere = false;
        if (repos.Count == 0)
        {
            var found = _repoFallbacks.SelectMany(fallback => fallback.Read()).ToList();
            repos = found.Select(f => f.Repo).ToList();
            foreach (var (repo, detail) in found)
                details.TryAdd(repo.Id, detail with { RepoId = repo.Id });
            listedElsewhere = repos.Count > 0;
        }

        return repos.Select(repo =>
        {
            details.TryGetValue(repo.Id, out var detail);
            var technical = new Dictionary<string, string> { ["repo-id"] = repo.Id };
            // PackageKit does not know these, so it cannot switch them either.
            if (listedElsewhere)
                technical["read-only"] = "true";
            if (backend is not null)
                technical["backend"] = backend.Name;
            if (detail is not null)
                technical["file"] = detail.FilePath;
            if (detail?.SignatureCheck is { } check)
                technical["signature-check"] = check ? "on" : "off";
            if (Flavor(repo.Id) is { } flavor)
                technical["flavor"] = flavor;

            return new SourceInfo(
                ProviderId,
                Id: repo.Id,
                Name: string.IsNullOrWhiteSpace(repo.Description) ? repo.Id : repo.Description,
                Description: null,
                Url: detail?.Url,
                Enabled: repo.Enabled,
                Scope: SourceScope.System,
                Trust: TrustLevel.Unknown,
                Technical: technical);
        }).ToList();
    }

    public async Task<IReadOnlyList<AppInfo>> ClaimAppsAsync(DesktopEntryIndex unclaimed, CancellationToken ct)
    {
        var candidates = unclaimed.Entries
            .Where(entry => _launcherDirs.Any(d => entry.Path.StartsWith(d, StringComparison.Ordinal)))
            .ToList();

        var remembered = OwnerCache.Load(_cache);
        var owners = new (DesktopEntry Entry, PkPackage? Owner)[candidates.Count];
        var stamps = candidates.Select(entry => FileStamp.Of(entry.Path)).ToArray();
        var pending = new List<int>();

        // Launchers whose file has not changed since last time keep their remembered owner.
        // One request then confirms those packages are still installed and gives their current version.
        var recalled = new Dictionary<int, string>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (stamps[i] is { } stamp && remembered.Owners.TryGetValue(candidates[i].Path, out var known) &&
                known.Stamp == stamp)
            {
                if (known.Package is not null)
                    recalled[i] = known.Package;
                // else: confirmed earlier as belonging to no package; nothing to ask.
            }
            else
            {
                pending.Add(i);
            }
        }

        if (recalled.Count > 0)
        {
            var names = recalled.Values.Select(NameOf).Distinct(StringComparer.Ordinal).ToList();
            var installed = (await _client.ResolveInstalledAsync(names, ct).ConfigureAwait(false))
                .GroupBy(KeyOf, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var (i, key) in recalled)
            {
                if (installed.TryGetValue(key, out var package))
                    owners[i] = (candidates[i], package);
                else
                    pending.Add(i); // The package is gone or renamed: ask again.
            }
        }

        // PackageKit does not say which file matched, so each remaining launcher is its own request.
        await Parallel.ForAsync(0, pending.Count,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSearches, CancellationToken = ct },
            async (n, token) =>
            {
                var i = pending[n];
                var packages = await _client.SearchInstalledFileAsync(candidates[i].Path, token).ConfigureAwait(false);
                owners[i] = (candidates[i], packages.Count > 0 ? packages[0] : null);
            }).ConfigureAwait(false);

        // A launcher in a system folder that no package claims is unusual. The likelier story is that
        // something was installed a moment ago by another tool and the daemon has not noticed. Tell it
        // to look again; it takes a few seconds to do so, so ask a few more times before believing "nobody".
        var unowned = pending.Where(i => owners[i].Owner is null).ToList();
        if (unowned.Count > 0)
        {
            await _client.NotifyPackagesChangedAsync(ct).ConfigureAwait(false);
            for (var attempt = 0; attempt < RecheckAttempts && unowned.Count > 0; attempt++)
            {
                await Task.Delay(_recheckWait, ct).ConfigureAwait(false);
                foreach (var i in unowned)
                {
                    var packages = await _client.SearchInstalledFileAsync(candidates[i].Path, ct).ConfigureAwait(false);
                    owners[i] = (candidates[i], packages.Count > 0 ? packages[0] : null);
                }
                unowned = unowned.Where(i => owners[i].Owner is null).ToList();
            }
        }

        // Launchers remembered as belonging to no package were not touched above; give them their entry.
        for (var i = 0; i < candidates.Count; i++)
            owners[i] = (candidates[i], owners[i].Owner);

        var byPackage = owners
            .Where(pair => pair.Owner is not null)
            .GroupBy(pair => pair.Owner!.PackageId, StringComparer.Ordinal)
            .ToList();

        // A given version of a package always has the same size, so sizes are remembered too.
        // The answer may name a package with a different data field than the request did
        // (dnf5 drops the "installed:" prefix), so sizes are matched on name, version and architecture.
        var sizes = byPackage.Select(group => WithoutData(group.Key))
            .Where(remembered.Sizes.ContainsKey)
            .ToDictionary(key => key, key => remembered.Sizes[key], StringComparer.Ordinal);
        var unsized = byPackage.Select(group => group.Key).Where(id => !sizes.ContainsKey(WithoutData(id))).ToList();
        foreach (var (id, size) in await _client.GetSizesAsync(unsized, ct).ConfigureAwait(false))
            sizes.TryAdd(WithoutData(id), size);

        // Only what was seen this time is kept, so entries for removed launchers do not pile up.
        var current = new Dictionary<string, OwnerCache.Owner>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            // "Nobody owns this" is remembered too; by now it has been confirmed after the daemon looked again.
            if (stamps[i] is { } stamp)
                current[candidates[i].Path] = new(stamp, owners[i].Owner is { } owner ? KeyOf(owner) : null);
        }
        OwnerCache.Save(_cache, new(current, sizes));

        return byPackage.Select(group =>
        {
            var package = group.First().Owner!;
            // A package can ship several launchers; lead with the one named after it.
            var launchers = group.Select(pair => pair.Entry)
                .OrderByDescending(entry => entry.DesktopId.Contains(package.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var primary = launchers[0];
            var iconIsPath = primary.Icon is not null && Path.IsPathFullyQualified(primary.Icon);

            return new AppInfo(
                ProviderId,
                Id: $"{package.Name}.{package.Arch}",
                Name: primary.Name,
                Summary: primary.Comment ?? (package.Summary.Length > 0 ? package.Summary : null),
                IconName: iconIsPath ? null : primary.Icon,
                IconPath: iconIsPath ? primary.Icon : null,
                Version: package.Version,
                SourceId: package.OriginRepo,
                SizeBytes: sizes.TryGetValue(WithoutData(package.PackageId), out var size) ? size : null,
                InstalledAt: null,
                LauncherPaths: launchers.Select(l => l.Path).ToList(),
                ComponentId: null,
                Technical: new Dictionary<string, string>
                {
                    ["package"] = package.Name,
                    ["package-id"] = package.PackageId,
                    ["arch"] = package.Arch,
                });
        }).ToList();
    }

    private static string KeyOf(PkPackage package) => $"{package.Name}.{package.Arch}";

    // Architectures contain no dots, package names sometimes do.
    private static string NameOf(string key) => key[..key.LastIndexOf('.')];

    private static string WithoutData(string packageId)
    {
        var lastSeparator = packageId.LastIndexOf(';');
        return lastSeparator < 0 ? packageId : packageId[..lastSeparator];
    }

    // Add-on repositories layered on the distribution's package manager.
    private static string? Flavor(string repoId) =>
        repoId.StartsWith("copr:", StringComparison.Ordinal) ? "copr" : null;
}
