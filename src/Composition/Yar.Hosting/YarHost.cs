using Yar.Backends.Bundles;
using Yar.Backends.Flatpak;
using Yar.Backends.PackageKit;
using Yar.Backends.Snap;
using Yar.Core;
using Yar.Metadata;
using Yar.Online;
using Yar.Persistence;

namespace Yar.Hosting;

/// <summary>Wires the backends together. Shared by the app and the command-line tool.</summary>
public sealed class YarHost : IDisposable
{
    /// <summary>Placeholder until the project has its real reverse-DNS ID.</summary>
    public const string AppId = AppIdentity.Id;

    private readonly SnapProvider _snap;
    private readonly PackageKitClient _packageKit = new();
    private readonly OnlineStoreData _online;
    private readonly GitHubFeed _developer;
    private readonly ISoftwareProvider[] _providers;

    public YarHost()
    {
        Dirs = XdgDirs.FromEnvironment();
        _snap = new SnapProvider(userSnapDir: Path.Combine(Dirs.Home, "snap"));
        Os = OsRelease.Read();

        var path = Environment.GetEnvironmentVariable("PATH");
        Cache = new FileCacheStore(Path.Combine(Dirs.CacheHome, "yar"));
        SearchPath = (path ?? "/usr/local/bin:/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries);

        var flatpakUserDir = Path.Combine(Dirs.DataHome, "flatpak");
        Changes = new ChangeMonitor(
        [
            FileChangeSource.ForLaunchers(Dirs),
            FileChangeSource.ForFlatpak(["/var/lib/flatpak", flatpakUserDir]),
            new PackageKitWatcher(_packageKit),
        ], quietPeriod: TimeSpan.FromSeconds(2));

        var desktops = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);

        // Claim order matters: each provider only sees the launchers the earlier ones left.
        ISoftwareProvider[] providers =
        [
            new FlatpakProvider(
                new ProcessRunner(), flatpakUserDir, appDataDir: Path.Combine(Dirs.Home, ".var", "app")),
            _snap,
            new PackageKitProvider(
                _packageKit, [RepoFileReader.ForSystem()],
                Dirs.DataDirs.Select(d => Path.Combine(d, "applications")).ToList(),
                Os.HomeUrl,
                Cache,
                [new AptListsReader()]),
            ManualAppsProvider.ForUser(Dirs, path),
        ];

        _providers = providers;
        Storage = new StorageAnalyzer(new LsblkProbe(new ProcessRunner()), providers, Dirs.CacheHome, Dirs.Home);
        ThirdParty = new FedoraThirdParty(new ProcessRunner());
        Store = new StoreCatalogReader(Dirs, Cache);
        _online = new OnlineStoreData(
            Cache, Os.Name, imageDirectory: Path.Combine(Dirs.CacheHome, "yar", "images"));
        _developer = new GitHubFeed(Cache);

        Catalog = new AppCatalog(
            providers,
            _ => Task.Run(() => new DesktopEntryScanner(Dirs, currentDesktops: desktops, searchPath: SearchPath).Scan()),
            new CatalogAnnotator(
                KnownSources.LoadBundled(Os.Id), () => ComponentIndex.Load(Dirs, cache: Cache),
                (path ?? "/usr/local/bin:/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries)),
            Cache);
    }

    public XdgDirs Dirs { get; }

    public OsRelease Os { get; }

    public AppCatalog Catalog { get; }

    /// <summary>Works out where disk space goes. Slow enough to run only when asked.</summary>
    public StorageAnalyzer Storage { get; }

    /// <summary>The app catalogs already on this computer. Reading them needs no network.</summary>
    public IStoreCatalogReader Store { get; }

    /// <summary>Ratings, reviews and popularity from the internet. Only call while the user allows online data.</summary>
    public IOnlineStoreData Online => _online;

    /// <summary>The distribution's opt-in for software it does not make itself. Check IsAvailable first.</summary>
    public IThirdPartyPolicy ThirdParty { get; }

    /// <summary>Trending source-code projects. Only call when the user switched the developer section on.</summary>
    public IDeveloperFeed Developer => _developer;

    /// <summary>The folders programs are looked for in.</summary>
    public IReadOnlyList<string> SearchPath { get; }

    /// <summary>An optional ability of a provider, such as telling an app's permissions; null if it has none.</summary>
    public T? Ability<T>(string providerId)
        where T : class => _providers.FirstOrDefault(p => p.Id == providerId) as T;

    /// <summary>The part of a provider that can change its sources, if it has one.</summary>
    public ISourceManager? SourceManagerFor(string providerId) =>
        _providers.FirstOrDefault(p => p.Id == providerId) as ISourceManager;

    /// <summary>Tells when something changed on this computer while Yar is open. Call Start once to begin.</summary>
    public ChangeMonitor Changes { get; }

    /// <summary>Saved results that make the next start faster. Safe to delete at any time.</summary>
    public ICacheStore Cache { get; }

    public void Dispose()
    {
        Changes.Dispose();
        _online.Dispose();
        _developer.Dispose();
        _snap.Dispose();
        _packageKit.Dispose();
    }
}
