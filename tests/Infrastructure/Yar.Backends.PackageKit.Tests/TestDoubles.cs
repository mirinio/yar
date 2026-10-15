using Yar.Core;

namespace Yar.Backends.PackageKit.Tests;

/// <summary>Stands in for the PackageKit daemon: answers from what a test put in, and notes every change asked of it.</summary>
internal sealed class FakeClient : IPackageKitClient
{
    public PkBackend? Backend { get; init; } = new("dnf5", "fedora;44;x86_64");
    public List<PkRepo> Repos { get; } = [];
    public Dictionary<string, string> Owners { get; } = [];
    public Dictionary<string, long> Sizes { get; } = [];
    public List<string> Searched { get; } = [];
    public int Nudges { get; private set; }
    public Dictionary<string, string> OwnersAfterNudge { get; } = [];

    public Task NotifyPackagesChangedAsync(CancellationToken ct)
    {
        Nudges++;
        foreach (var (path, id) in OwnersAfterNudge)
            Owners[path] = id;
        return Task.CompletedTask;
    }
    public Dictionary<string, OfferSize> OfferSizes { get; } = [];
    public List<string> SizeQuestions { get; } = [];

    public Task<OfferSize?> GetOfferSizeAsync(string packageName, CancellationToken ct)
    {
        SizeQuestions.Add(packageName);
        return Task.FromResult(OfferSizes.GetValueOrDefault(packageName));
    }
    public List<(string RepoId, bool Enabled)> Switched { get; } = [];

    public Task SetRepoEnabledAsync(string repoId, bool enabled, CancellationToken ct)
    {
        Switched.Add((repoId, enabled));
        return Task.CompletedTask;
    }
    public List<IReadOnlyList<string>> Resolved { get; } = [];
    public List<IReadOnlyList<string>> Sized { get; } = [];
    public Dictionary<string, string> Installed { get; } = [];

    public Task<IReadOnlyList<PkPackage>> ResolveInstalledAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        Resolved.Add(names);
        return Task.FromResult<IReadOnlyList<PkPackage>>(Installed
            .Where(pair => names.Contains(pair.Key))
            .Select(pair => PkPackage.Parse(pair.Value, "Package summary")!).ToList());
    }
    public List<PkPackage> Updates { get; } = [];
    public TimeSpan? SinceRefresh { get; init; }

    public Task<IReadOnlyList<PkPackage>> GetUpdatesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PkPackage>>(Updates);

    public Task<TimeSpan?> GetTimeSinceRefreshAsync(CancellationToken ct) => Task.FromResult(SinceRefresh);

    public Task<PkBackend?> GetBackendAsync(CancellationToken ct) => Task.FromResult(Backend);

    public Task<IReadOnlyList<PkRepo>> GetRepoListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PkRepo>>(Repos);

    public Task<IReadOnlyList<PkPackage>> SearchInstalledFileAsync(string path, CancellationToken ct)
    {
        lock (Searched) Searched.Add(path);
        return Task.FromResult<IReadOnlyList<PkPackage>>(
            Owners.TryGetValue(path, out var id) ? [PkPackage.Parse(id, "Package summary")!] : []);
    }

    public List<PkPackage> Available { get; } = [];
    public List<PkPackage> Simulated { get; } = [];
    public List<string> Changes { get; } = [];

    public Task<IReadOnlyList<PkPackage>> ResolveAvailableAsync(string name, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PkPackage>>(Available.Where(p => p.Name == name).ToList());

    public Task<IReadOnlyDictionary<string, OfferSize>> GetOfferSizesAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, OfferSize>>(
            ids.Where(id => OfferSizes.ContainsKey(id)).ToDictionary(id => id, id => OfferSizes[id]));

    public Task<IReadOnlyList<PkPackage>> SimulateInstallAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PkPackage>>(Simulated);

    public Task<IReadOnlyList<PkPackage>> SimulateRemoveAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PkPackage>>(Simulated);

    public Task<IReadOnlyList<PkPackage>> SimulateInstallFilesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        SimulateFails is { } reason
            ? Task.FromException<IReadOnlyList<PkPackage>>(new PackageKitException(1, reason))
            : Task.FromResult<IReadOnlyList<PkPackage>>(Simulated);

    public string? SimulateFails { get; set; }

    public Task InstallFilesAsync(IReadOnlyList<string> paths, IProgress<PkProgress> progress, CancellationToken ct)
    {
        Changes.Add("install-files " + string.Join(",", paths));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PkPackage>> SimulateUpdateAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PkPackage>>(Simulated);

    public Task UpdateAsync(IReadOnlyList<string> ids, IProgress<PkProgress> progress, CancellationToken ct)
    {
        Changes.Add("update " + string.Join(",", ids));
        return Task.CompletedTask;
    }

    public Task InstallAsync(IReadOnlyList<string> ids, IProgress<PkProgress> progress, CancellationToken ct)
    {
        progress.Report(new(PkProgress.Download, 40, AllowCancel: true));
        progress.Report(new(PkProgress.Install, null, AllowCancel: false));
        Changes.Add("install " + string.Join(",", ids));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(IReadOnlyList<string> ids, bool allowDependents, IProgress<PkProgress> progress, CancellationToken ct)
    {
        Changes.Add($"remove {string.Join(",", ids)} dependents={allowDependents}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, long>> GetSizesAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        Sized.Add(ids);
        return Task.FromResult<IReadOnlyDictionary<string, long>>(
            Sizes.Where(pair => ids.Any(id => id.StartsWith(pair.Key[..pair.Key.LastIndexOf(';')], StringComparison.Ordinal)))
                .ToDictionary(pair => pair.Key, pair => pair.Value));
    }
}

/// <summary>Stands in for a system tool such as rpm: every command gets the same answer, empty output meaning failure.</summary>
internal sealed class FakeProcessRunner(string output) : IProcessRunner
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add([absolutePath, .. arguments]);
        return Task.FromResult(new ProcessResult(output.Length > 0 ? 0 : 1, output, ""));
    }
}

/// <summary>Keeps every progress report, in order.</summary>
internal sealed class Collect<T>(List<T> into) : IProgress<T>
{
    public void Report(T value) => into.Add(value);
}
