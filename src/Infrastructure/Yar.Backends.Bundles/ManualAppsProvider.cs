using Yar.Core;
using Yar.Metadata;

namespace Yar.Backends.Bundles;

/// <summary>
/// Apps no package manager knows about: launchers the user (or an installer script) created,
/// and AppImages lying around in the usual download folders. Read-only.
/// </summary>
public sealed class ManualAppsProvider : ISoftwareProvider
{
    public const string ProviderId = "manual";
    public const string SourceId = "manual";

    private const int MaxFilesPerDirectory = 2000;

    private readonly List<string> _launcherDirs;
    private readonly IReadOnlyList<string> _appImageDirs;
    private readonly IReadOnlyList<string> _searchPath;

    /// <param name="launcherDirs">Directories whose leftover launchers count as manual, e.g. the user's applications dir.</param>
    /// <param name="appImageDirs">Directories to look in for AppImages. Not searched recursively.</param>
    /// <param name="searchPath">The <c>PATH</c> used to find the program a launcher starts.</param>
    public ManualAppsProvider(
        IReadOnlyList<string> launcherDirs, IReadOnlyList<string> appImageDirs, IReadOnlyList<string> searchPath)
    {
        _launcherDirs = launcherDirs.Select(d => d.TrimEnd('/') + "/").ToList();
        _appImageDirs = appImageDirs;
        _searchPath = searchPath;
    }

    public static ManualAppsProvider ForUser(XdgDirs dirs, string? pathVariable) => new(
        launcherDirs: [dirs.UserApplicationsDir, "/usr/local/share/applications"],
        appImageDirs:
        [
            Path.Combine(dirs.Home, "Applications"),
            Path.Combine(dirs.Home, "AppImages"),
            Path.Combine(dirs.Home, "Downloads"),
            Path.Combine(dirs.Home, ".local", "bin"),
        ],
        searchPath: (pathVariable ?? "/usr/local/bin:/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries));

    public string Id => ProviderId;

    public ProviderFacts Facts => new(ProviderId, HomepageUrl: null,
    [
        new("Your own launchers", _launcherDirs[0].TrimEnd('/')),
        .. _appImageDirs.Where(Directory.Exists).Select(d => new ProviderLocation("A folder I look in for apps", d)),
    ]);

    public Task<ProviderStatus> ProbeAsync(CancellationToken ct) => Task.FromResult(ProviderStatus.Available);

    public Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceInfo>>(
        [
            new SourceInfo(
                ProviderId, SourceId, "Downloaded yourself",
                "Apps you downloaded or set up by hand. No store keeps them up to date.",
                Url: null, Enabled: true, SourceScope.User, TrustLevel.Unknown,
                new Dictionary<string, string>()),
        ]);

    public Task<IReadOnlyList<AppInfo>> ClaimAppsAsync(DesktopEntryIndex unclaimed, CancellationToken ct) =>
        Task.Run<IReadOnlyList<AppInfo>>(() =>
        {
            var apps = new List<AppInfo>();
            var launchedPrograms = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in unclaimed.Entries.Where(IsManualLauncher))
            {
                ct.ThrowIfCancellationRequested();
                var program = DesktopExec.ResolveProgram(entry.Exec, _searchPath);
                if (program is not null)
                    launchedPrograms.Add(program);
                apps.Add(FromLauncher(entry, program));
            }

            foreach (var (path, type) in FindAppImages(ct))
            {
                if (!launchedPrograms.Contains(path))
                    apps.Add(FromAppImage(path, type));
            }

            return apps;
        }, ct);

    private bool IsManualLauncher(DesktopEntry entry) =>
        _launcherDirs.Any(directory => entry.Path.StartsWith(directory, StringComparison.Ordinal));

    private static AppInfo FromLauncher(DesktopEntry entry, string? program)
    {
        var technical = new Dictionary<string, string> { ["launcher"] = entry.Path };
        if (entry.Exec is not null)
            technical["exec"] = entry.Exec;

        long? size = null;
        string? installPath = null;
        if (program is null)
        {
            // Some apps are started by the desktop through a message instead of a command line.
            // Those have no program to look for, and nothing is wrong with them.
            var startedByMessage = string.IsNullOrWhiteSpace(entry.Exec) && entry.GetKey("DBusActivatable") == "true";
            // Otherwise the launcher points at something that is gone. The health check picks this up.
            if (!startedByMessage)
                technical["broken"] = "true";
        }
        else
        {
            technical["program"] = program;
            if (AppImageFile.DetectType(program) is { } type)
            {
                technical["appimage-type"] = type.ToString(System.Globalization.CultureInfo.InvariantCulture);
                size = new FileInfo(program).Length;
                installPath = program;
            }
            else if (IsOwnFolder(program))
            {
                installPath = Path.GetDirectoryName(program);
            }
        }

        var iconIsPath = entry.Icon is not null && Path.IsPathFullyQualified(entry.Icon);
        return new AppInfo(
            ProviderId,
            Id: entry.DesktopId,
            Name: entry.Name,
            Summary: entry.Comment ?? entry.GenericName,
            IconName: iconIsPath ? null : entry.Icon,
            IconPath: iconIsPath ? entry.Icon : null,
            Version: null,
            SourceId: SourceId,
            SizeBytes: size,
            InstalledAt: null,
            LauncherPaths: [entry.Path],
            ComponentId: null,
            Technical: technical)
        {
            InstallPath = installPath,
        };
    }

    private static AppInfo FromAppImage(string path, int type) => new(
        ProviderId,
        Id: path,
        Name: Path.GetFileNameWithoutExtension(path),
        Summary: null,
        IconName: null,
        IconPath: null,
        Version: null,
        SourceId: SourceId,
        SizeBytes: new FileInfo(path).Length,
        InstalledAt: null,
        LauncherPaths: [],
        ComponentId: null,
        Technical: new Dictionary<string, string>
        {
            ["program"] = path,
            ["appimage-type"] = type.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["in-menu"] = "false",
        })
    {
        InstallPath = path,
    };

    // A launcher that starts a shared system tool (steam, wine) has no folder of its own.
    private static bool IsOwnFolder(string program) =>
        !program.StartsWith("/usr/", StringComparison.Ordinal) &&
        !program.StartsWith("/bin/", StringComparison.Ordinal) &&
        !program.StartsWith("/sbin/", StringComparison.Ordinal);

    private IEnumerable<(string Path, int Type)> FindAppImages(CancellationToken ct)
    {
        foreach (var directory in _appImageDirs)
        {
            List<string> files;
            try
            {
                if (!Directory.Exists(directory))
                    continue;
                files = Directory.EnumerateFiles(directory).Take(MaxFilesPerDirectory)
                    .Order(StringComparer.Ordinal).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (AppImageFile.DetectType(file) is { } type)
                    yield return (file, type);
            }
        }
    }
}
