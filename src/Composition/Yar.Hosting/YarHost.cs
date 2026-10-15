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
    /// <summary>The app ID the desktop knows Yar by; see <see cref="AppIdentity"/>.</summary>
    public const string AppId = AppIdentity.Id;

    private readonly SnapProvider _snap;
    private readonly PackageKitClient _packageKit = new();
    private readonly OnlineStoreData _online;
    private readonly GitHubFeed _developer;
    private readonly LinkDownloader _downloader;
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
        Trash = new HomeTrash(Dirs.Home, Dirs.DataHome);
        Changes = new ChangeMonitor(
        [
            FileChangeSource.ForLaunchers(Dirs),
            FileChangeSource.ForFlatpak(["/var/lib/flatpak", flatpakUserDir]),
            new PackageKitWatcher(_packageKit),
        ], quietPeriod: TimeSpan.FromSeconds(2));

        var desktops = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);

        var manifests = new ManifestStore(Path.Combine(Dirs.DataHome, "yar", "apps"));
        var flatpak = new FlatpakProvider(
            new ProcessRunner(), flatpakUserDir, appDataDir: Path.Combine(Dirs.Home, ".var", "app"),
            trash: Trash);
        var packageKit = new PackageKitProvider(
            _packageKit, [RepoFileReader.ForSystem()],
            Dirs.DataDirs.Select(d => Path.Combine(d, "applications")).ToList(),
            Os.HomeUrl,
            Cache,
            [new AptListsReader()],
            runner: new ProcessRunner());

        // Claim order matters: each provider only sees the launchers the earlier ones left.
        ISoftwareProvider[] providers =
        [
            flatpak,
            _snap,
            packageKit,
            ManualAppsProvider.ForUser(Dirs, path, manifests, Trash),
        ];

        _providers = providers;
        Journal = new FileChangeJournal(Path.Combine(Dirs.StateHome, "yar", "history.jsonl"));
        Operations = new OperationRunner(Journal);
        Storage = new StorageAnalyzer(new LsblkProbe(new ProcessRunner()), providers, Dirs.CacheHome, Dirs.Home);
        ThirdParty = new FedoraThirdParty(new ProcessRunner());
        Pinner = new DesktopPinner(new ProcessRunner(), desktops);
        Make = new AppChanges(providers, Operations, Trash, ThirdParty, Pinner);
        DefaultAppsFolder = Path.Combine(Dirs.Home, "Applications");
        _downloader = new LinkDownloader(Path.Combine(Dirs.CacheHome, "yar", "downloads"));
        Intake = new BundleIntake(
            new IntakePlaces(
                Dirs.Home, Path.Combine(Dirs.CacheHome, "yar"), Dirs.UserApplicationsDir,
                Path.Combine(Dirs.DataHome, "yar", "icons")),
            manifests, new ProcessRunner(), Trash, new BundleInspector(new LibraryFinder()),
            new Dictionary<string, IFileIntakeHandler>
            {
                ["rpm"] = packageKit,
                ["deb"] = packageKit,
                ["flatpakref"] = flatpak,
                ["flatpak"] = flatpak,
            });
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

    /// <summary>The record of everything Yar changed on this computer.</summary>
    public IChangeJournal Journal { get; }

    /// <summary>Carries out changes one at a time and reports how far along the current one is.</summary>
    public OperationRunner Operations { get; }

    public ITrash Trash { get; }

    /// <summary>Takes apps the user brings as a file or a link.</summary>
    public IAppIntake Intake { get; }

    /// <summary>Pins added apps to the taskbar where this desktop allows it. Check WhyNot first.</summary>
    public IAppPinner Pinner { get; }

    /// <summary>Fetches a file from a link the user gave. Only ever on the user's say-so.</summary>
    public IAppDownloader Downloader => _downloader;

    /// <summary>Where added apps go unless the user chose another folder: <c>~/Applications</c>.</summary>
    public string DefaultAppsFolder { get; }

    /// <summary>The one way to change the system from Yar: recorded, queued and undoable.</summary>
    public AppChanges Make { get; }

    /// <summary>Tells when something changed on this computer while Yar is open. Call Start once to begin.</summary>
    public ChangeMonitor Changes { get; }

    /// <summary>Saved results that make the next start faster. Safe to delete at any time.</summary>
    public ICacheStore Cache { get; }

    public void Dispose()
    {
        Changes.Dispose();
        Operations.Dispose();
        _online.Dispose();
        _developer.Dispose();
        _downloader.Dispose();
        _snap.Dispose();
        _packageKit.Dispose();
    }
}
