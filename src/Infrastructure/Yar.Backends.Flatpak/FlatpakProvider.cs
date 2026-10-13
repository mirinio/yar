using Yar.Core;

namespace Yar.Backends.Flatpak;

/// <summary>Flatpak remotes and installed Flatpak apps, read through the <c>flatpak</c> command.</summary>
public sealed class FlatpakProvider
    : ISoftwareProvider, IUpdateProvider, ISourceManager, IPermissionProvider, ILeftoverProvider, ISharedPartsProvider
{
    public const string ProviderId = "flatpak";

    private static readonly string[] DefaultLocations = ["/usr/bin/flatpak", "/bin/flatpak", "/usr/local/bin/flatpak"];
    private static readonly string[] Installations = ["system", "user"];
    private static readonly string[] RefKinds = ["--app", "--runtime"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // A system-wide change waits for the user to type their password.
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5);

    // A source's list of apps can be several megabytes.
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(120);

    // Adding a source downloads its description and key.
    private static readonly TimeSpan ChangeTimeout = TimeSpan.FromSeconds(60);

    // Asking about updates can reach out to the network; do not let a slow connection hold things up for long.
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromSeconds(12);

    private readonly IProcessRunner _runner;
    private readonly string? _flatpak;
    private readonly string _userInstallationDir;
    private readonly string _systemInstallationDir;
    private readonly string? _appDataDir;

    /// <param name="userInstallationDir">Usually <c>$XDG_DATA_HOME/flatpak</c>.</param>
    /// <param name="flatpakPath">Absolute path of the flatpak command; located automatically when null.</param>
    public FlatpakProvider(
        IProcessRunner runner, string userInstallationDir, string? flatpakPath = null,
        string systemInstallationDir = "/var/lib/flatpak", string? appDataDir = null)
    {
        _appDataDir = appDataDir;
        _systemInstallationDir = systemInstallationDir.TrimEnd('/') + "/";
        _runner = runner;
        _userInstallationDir = userInstallationDir.TrimEnd('/') + "/";
        _flatpak = flatpakPath ?? DefaultLocations.FirstOrDefault(File.Exists);
    }

    public string Id => ProviderId;

    public async Task<RawPermissions?> GetPermissionsAsync(AppInfo app, CancellationToken ct)
    {
        if (_flatpak is null || app.ProviderId != ProviderId ||
            !app.Technical.TryGetValue("ref", out var reference) || !IsRef(reference) ||
            !app.Technical.TryGetValue("installation", out var installation))
        {
            return null;
        }

        var output = await RunAsync(
            ["info", "--show-permissions", installation == "user" ? "--user" : "--system", reference], ct)
            .ConfigureAwait(false);
        return FlatpakOutput.ParsePermissions(output);
    }

    /// <summary>
    /// Folders in the per-user app data directory that belong to no installed Flatpak. Flatpak keeps an
    /// app's settings there and leaves them behind when the app is removed.
    /// </summary>
    public Task<IReadOnlyList<LeftoverData>> FindLeftoversAsync(IReadOnlyList<AppInfo> installed, CancellationToken ct) =>
        Task.Run<IReadOnlyList<LeftoverData>>(() =>
        {
            if (_appDataDir is null || !Directory.Exists(_appDataDir))
                return [];

            var installedIds = installed.Select(app => app.ComponentId).Where(id => id is not null)
                .ToHashSet(StringComparer.Ordinal);
            var leftovers = new List<LeftoverData>();
            foreach (var directory in Directory.EnumerateDirectories(_appDataDir).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var info = new DirectoryInfo(directory);
                // A link could point anywhere; only real folders count as app data.
                if (info.LinkTarget is not null || installedIds.Contains(info.Name))
                    continue;
                leftovers.Add(new(ProviderId, info.Name, directory, FolderSize.Measure(directory, ct)));
            }
            return leftovers;
        }, ct);

    /// <summary>The runtimes Flatpak apps are built on. Several apps share one, so they are counted on their own.</summary>
    public async Task<IReadOnlyList<SharedPart>> GetSharedPartsAsync(CancellationToken ct) =>
        _flatpak is null
            ? []
            : FlatpakOutput.ParseRuntimes(
                await RunAsync(["list", "--runtime", Columns(FlatpakOutput.RuntimeColumns)], ct).ConfigureAwait(false));

    private static bool IsRef(string reference) =>
        reference.Length is > 0 and <= 512 && !reference.StartsWith('-') &&
        reference.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/');

    // Sources are only ever added for the current user. That needs no password and cannot affect
    // other people using this computer.
    public bool CanAdd => _flatpak is not null;

    public async Task AddAsync(SourceRequest request, CancellationToken ct)
    {
        var (checkedRequest, problem) = SourceAddress.Check(request.Url, request.Name);
        if (checkedRequest is null)
            throw new ArgumentException(problem, nameof(request));

        await RunAsync(
            ["remote-add", "--user", "--if-not-exists", checkedRequest.Name, checkedRequest.Url], ct, ChangeTimeout)
            .ConfigureAwait(false);

        // Fetch the new source's list of apps, so they can be shown. The source is added either way.
        try
        {
            await RunAsync(["update", "--user", "--appstream", checkedRequest.Name], ct, CatalogTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
        }
    }

    /// <summary>Only sources the user added for themselves can be removed; system-wide ones are left alone.</summary>
    public bool CanRemove(SourceInfo source) =>
        _flatpak is not null && source.ProviderId == ProviderId && source.Scope == SourceScope.User &&
        RemoteNameOf(source) is not null;

    public async Task RemoveAsync(SourceInfo source, CancellationToken ct)
    {
        if (!CanRemove(source))
            throw new InvalidOperationException("This source cannot be removed from here.");
        // flatpak itself refuses while apps from this source are still installed.
        await RunAsync(["remote-delete", "--user", RemoteNameOf(source)!], ct, ChangeTimeout).ConfigureAwait(false);
    }

    public bool CanToggle(SourceInfo source) =>
        _flatpak is not null && source.ProviderId == ProviderId && RemoteNameOf(source) is not null;

    /// <summary>
    /// Switches a Flatpak source on or off. For the user's own sources that needs no password;
    /// for system-wide ones Flatpak itself asks for it.
    /// </summary>
    public async Task SetEnabledAsync(SourceInfo source, bool enabled, CancellationToken ct)
    {
        if (!CanToggle(source))
            throw new InvalidOperationException("This source cannot be switched from here.");

        await RunAsync(
            ["remote-modify", $"--{InstallationName(source.Scope)}", enabled ? "--enable" : "--disable", RemoteNameOf(source)!],
            ct, PromptTimeout).ConfigureAwait(false);
    }

    private static string? RemoteNameOf(SourceInfo source) =>
        source.Technical.TryGetValue("remote", out var remote) && remote.Length > 0 &&
        remote == SourceAddress.SanitizeName(remote)
            ? remote
            : null;

    public ProviderFacts Facts => new(ProviderId, "https://flatpak.org/",
    [
        new("Apps for everyone on this computer", _systemInstallationDir.TrimEnd('/')),
        new("Apps only for you", _userInstallationDir.TrimEnd('/')),
    ]);

    public async Task<UpdateCheck> GetUpdatesAsync(IReadOnlyList<AppInfo> installed, CancellationToken ct)
    {
        // Four quick questions to flatpak, asked at the same time.
        var queries =
            from installation in Installations
            from kind in RefKinds
            select (installation, kind);
        var failures = new List<Exception>();
        var answers = await Task.WhenAll(queries.Select(async query =>
        {
            IReadOnlyList<FlatpakUpdate> found = [];
            try
            {
                found = FlatpakOutput.ParseUpdates(await RunAsync(
                    ["remote-ls", "--updates", query.kind, $"--{query.installation}", Columns(FlatpakOutput.UpdateColumns)],
                    ct, UpdateTimeout).ConfigureAwait(false));
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException)
            {
                lock (failures)
                    failures.Add(e);
            }
            return (query.installation, IsApp: query.kind == "--app", Updates: found);
        })).ConfigureAwait(false);
        // Some answers are better than none; only when every question failed is the check itself a failure.
        if (failures.Count == answers.Length)
            throw failures[0];

        var updates = new List<UpdateInfo>();
        foreach (var (installation, isApp, found) in answers)
        {
            foreach (var update in found)
            {
                var app = isApp
                    ? installed.FirstOrDefault(a =>
                        a.ComponentId == update.Application &&
                        a.Technical.TryGetValue("installation", out var where) && where == installation)
                    : null;
                updates.Add(new UpdateInfo(
                    ProviderId,
                    Id: $"{installation}:{update.Application}",
                    Name: app?.Name ?? update.Name ?? update.Application,
                    CurrentVersion: app?.Version,
                    NewVersion: update.Version,
                    Severity: UpdateSeverity.Normal,
                    SourceId: SourceId(installation, update.Origin),
                    AppId: app?.Id));
            }
        }
        return new UpdateCheck(updates);
    }

    public Task<ProviderStatus> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(_flatpak is null ? ProviderStatus.NotInstalled : ProviderStatus.Available);

    public async Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken ct)
    {
        var sources = new List<SourceInfo>();
        foreach (var scope in new[] { SourceScope.System, SourceScope.User })
        {
            var installation = InstallationName(scope);
            var output = await RunAsync(
                ["remotes", "--show-disabled", $"--{installation}", Columns(FlatpakOutput.RemoteColumns)],
                ct).ConfigureAwait(false);

            sources.AddRange(FlatpakOutput.ParseRemotes(output).Select(remote => ToSource(remote, scope)));
        }
        return sources;
    }

    public async Task<IReadOnlyList<AppInfo>> ClaimAppsAsync(DesktopEntryIndex unclaimed, CancellationToken ct)
    {
        var output = await RunAsync(["list", "--app", Columns(FlatpakOutput.AppColumns)], ct).ConfigureAwait(false);

        return FlatpakOutput.ParseApps(output)
            .Select(app => ToApp(app, LaunchersOf(app, unclaimed)))
            .ToList();
    }

    private static SourceInfo ToSource(FlatpakRemote remote, SourceScope scope)
    {
        var installation = InstallationName(scope);
        var technical = new Dictionary<string, string>
        {
            ["remote"] = remote.Name,
            ["installation"] = installation,
        };
        if (remote.Options.Count > 0)
            technical["options"] = string.Join(",", remote.Options);
        if (remote.Homepage is not null)
            technical["homepage"] = remote.Homepage;
        if (remote.Options.Contains("no-gpg-verify"))
            technical["signature-check"] = "off";

        return new SourceInfo(
            ProviderId,
            Id: SourceId(installation, remote.Name),
            Name: remote.Title ?? remote.Name,
            Description: remote.Description,
            Url: remote.Url,
            Enabled: !remote.Options.Contains("disabled"),
            Scope: scope,
            Trust: TrustLevel.Unknown,
            Technical: technical);
    }

    private AppInfo ToApp(FlatpakApp app, List<DesktopEntry> launchers)
    {
        var primary = launchers.FirstOrDefault();
        var installation = app.Installation == "user" ? _userInstallationDir : _systemInstallationDir;
        var files = Path.Combine(installation, "app", app.Application, "current", "active", "files");
        return new AppInfo(
            ProviderId,
            Id: $"{app.Installation}:{app.Ref}",
            Name: app.Name ?? primary?.Name ?? app.Application,
            Summary: primary?.Comment,
            IconName: primary?.Icon ?? app.Application,
            IconPath: null,
            Version: app.Version,
            SourceId: SourceId(app.Installation, app.Origin),
            SizeBytes: app.SizeBytes,
            InstalledAt: null,
            LauncherPaths: launchers.Select(l => l.Path).ToList(),
            ComponentId: app.Application,
            Technical: new Dictionary<string, string>
            {
                ["application"] = app.Application,
                ["ref"] = app.Ref,
                ["branch"] = app.Branch,
                ["installation"] = app.Installation,
            })
        {
            InstallPath = Directory.Exists(files) ? files : null,
            DataPath = _appDataDir is not null && Path.Combine(_appDataDir, app.Application) is var data &&
                Directory.Exists(data) ? data : null,
        };
    }

    // The launcher named after the app comes first; an app may export more (LibreOffice does).
    private List<DesktopEntry> LaunchersOf(FlatpakApp app, DesktopEntryIndex unclaimed) =>
        unclaimed.Entries
            .Where(entry => entry.GetKey("X-Flatpak") == app.Application &&
                IsUserPath(entry.Path) == (app.Installation == "user"))
            .OrderByDescending(entry => entry.DesktopId == app.Application + ".desktop")
            .ToList();

    private bool IsUserPath(string path) => path.StartsWith(_userInstallationDir, StringComparison.Ordinal);

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        var result = await _runner.RunAsync(_flatpak!, arguments, timeout ?? Timeout, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"flatpak {arguments[0]} failed: {result.StdErr.Trim()}");
        return result.StdOut;
    }

    private static string Columns(string[] columns) => "--columns=" + string.Join(",", columns);

    private static string InstallationName(SourceScope scope) => scope == SourceScope.User ? "user" : "system";

    private static string SourceId(string installation, string remote) => $"{installation}:{remote}";
}
