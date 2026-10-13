using Yar.Core;

namespace Yar.Backends.Flatpak;

// Installing and removing Flatpak apps. Everything here acts on one named app from one named source.
public sealed partial class FlatpakProvider
{
    // A large app over a slow connection takes a while; this only stops a download that hangs for good.
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromHours(3);

    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(10);

    public IReadOnlyList<InstallTarget> TargetsFor(StoreOffer offer, IReadOnlyList<SourceInfo> sources)
    {
        if (_flatpak is null || offer.ProviderId != ProviderId || RefOf(offer) is null ||
            RemoteOf(offer.SourceId) is not { } remote)
        {
            return [];
        }

        bool Has(SourceScope scope) => sources.Any(source =>
            source.ProviderId == ProviderId && source.Scope == scope && source.Enabled && RemoteNameOf(source) == remote);
        var forUser = Has(SourceScope.User);
        var forEveryone = Has(SourceScope.System);

        var targets = new List<InstallTarget>();
        if (forUser)
        {
            targets.Add(new("user", "Only for you",
                $"Goes into your own home folder ({_userInstallationDir.TrimEnd('/')}). Needs no password, and other " +
                "people using this computer don't get it.", NeedsPassword: false));
        }
        if (forEveryone)
        {
            targets.Add(new("system", "For everyone on this computer",
                $"Goes into the system's Flatpak folder ({_systemInstallationDir.TrimEnd('/')}). Your system may ask " +
                "for your password." +
                (forUser ? "" : " To install apps only for yourself instead, add this source for yourself in Sources first."),
                NeedsPassword: false));
        }
        return targets;
    }

    public Task<ChangePlan> PlanInstallAsync(StoreOffer offer, InstallTarget target, string name, CancellationToken ct) =>
        PlanInstallAsync(RemoteOf(offer.SourceId), target.Id, RefOf(offer), name, ct);

    public Task<ChangePlan> PlanReinstallAsync(ChangeRecord removal, CancellationToken ct) =>
        PlanInstallAsync(
            removal.Undo.GetValueOrDefault("remote"), removal.Undo.GetValueOrDefault("installation") ?? "",
            removal.Undo.GetValueOrDefault("ref"), removal.Name, ct);

    private async Task<ChangePlan> PlanInstallAsync(
        string? remote, string installation, string? reference, string name, CancellationToken ct)
    {
        if (_flatpak is null || remote is null || reference is null || !IsRef(reference) ||
            remote != SourceAddress.SanitizeName(remote) || installation is not ("user" or "system"))
        {
            return ChangePlan.Refused(ProviderId, reference ?? "", name, "I can't tell exactly which app and source this is, so I won't guess.");
        }

        var flag = $"--{installation}";
        IReadOnlyDictionary<string, string> info;
        try
        {
            info = FlatpakOutput.ParseRemoteInfo(
                await RunAsync(["remote-info", flag, remote, reference], ct, CatalogTimeout).ConfigureAwait(false));
        }
        catch (InvalidOperationException e)
        {
            return ChangePlan.Refused(
                ProviderId, reference, name, $"The source {remote} doesn't offer this app right now ({LastLine(e.Message)}).");
        }

        // What flatpak calls the app from here on: kind, ID, processor type and branch.
        var fullRef = info.GetValueOrDefault("Ref") is { } printed && IsRef(printed) ? printed : reference;
        var download = Size(info, "Download Size");
        var installed = Size(info, "Installed Size");
        var items = new List<PlanItem> { new(name, info.GetValueOrDefault("Version") is { } v ? $"Version {v}" : null, installed) };
        var notes = new List<string>
        {
            "It may bring a few small add-ons with it, such as translations.",
        };

        if (info.GetValueOrDefault("Runtime") is { } runtime && IsRef(runtime) && !await HasRuntimeAsync(runtime, ct).ConfigureAwait(false))
        {
            long? runtimeDownload = null, runtimeInstalled = null;
            try
            {
                var runtimeInfo = FlatpakOutput.ParseRemoteInfo(await RunAsync(
                    ["remote-info", flag, remote, "runtime/" + runtime], ct, CatalogTimeout).ConfigureAwait(false));
                runtimeDownload = Size(runtimeInfo, "Download Size");
                runtimeInstalled = Size(runtimeInfo, "Installed Size");
            }
            catch (InvalidOperationException)
            {
                // It comes from another source; flatpak finds it, but I can't say how big it is.
            }
            items.Add(new(
                runtime.Split('/')[0] + " " + runtime.Split('/')[^1],
                "Building blocks this app runs on. They are installed once and shared with other apps that need them.",
                runtimeInstalled));
            // A total with a part missing would be a wrong total.
            download = download is null || runtimeDownload is null ? null : download + runtimeDownload;
            installed = installed is null || runtimeInstalled is null ? null : installed + runtimeInstalled;
        }

        var warnings = new List<string>();
        try
        {
            var report = FlatpakPermissions.Describe(FlatpakOutput.ParsePermissions(await RunAsync(
                ["remote-info", flag, "--show-metadata", remote, fullRef], ct, CatalogTimeout).ConfigureAwait(false)));
            if (report.Verdict == PermissionWeight.Broad)
            {
                warnings.Add(
                    "This app asks for broad access, so it is hardly kept apart from the rest of your computer: " +
                    string.Join("; ", report.Notes.Where(n => n.Weight == PermissionWeight.Broad).Select(n => n.Text)) + ".");
            }
        }
        catch (InvalidOperationException)
        {
            notes.Add("I couldn't find out what this app may reach on your computer.");
        }

        var installedRef = fullRef.StartsWith("app/", StringComparison.Ordinal) ? fullRef["app/".Length..] : fullRef;
        return new(ProviderId, $"{installation}:{installedRef}", name, items)
        {
            DownloadBytes = download,
            DiskBytes = installed,
            Warnings = warnings,
            Notes = notes,
            Origin = SourceId(installation, remote),
            Technical = new Dictionary<string, string>
            {
                ["ref"] = fullRef,
                ["remote"] = remote,
                ["installation"] = installation,
            },
        };
    }

    public async Task InstallAsync(ChangePlan plan, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        var (reference, remote, installation) = Checked(plan);
        if (remote is null)
            throw new InvalidOperationException("This plan does not say which source to install from.");

        progress.Report(new(OperationPhase.Waiting));
        await RunChangeAsync(
            ["install", $"--{installation}", "--noninteractive", remote, reference], InstallTimeout,
            OperationPhase.Installing, progress, ct).ConfigureAwait(false);
    }

    public string? WhyNotRemovable(AppInfo app)
    {
        if (_flatpak is null)
            return "Flatpak is not installed on this computer.";
        if (app.ProviderId != ProviderId || !app.Technical.TryGetValue("ref", out var reference) || !IsRef(reference) ||
            app.Technical.GetValueOrDefault("installation") is not ("user" or "system"))
        {
            return "I can't tell exactly which Flatpak app this is, so I won't remove anything.";
        }
        return null;
    }

    public Task<ChangePlan> PlanRemovalAsync(AppInfo app, CancellationToken ct) => Task.Run(() =>
    {
        if (WhyNotRemovable(app) is { } reason)
            return ChangePlan.Refused(ProviderId, app.Id, app.Name, reason);

        var installation = app.Technical["installation"];
        var notes = new List<string>
        {
            "The building blocks it runs on stay, because other apps may use them.",
        };
        if (installation == "system")
            notes.Add("It is installed for everyone on this computer, so your system may ask for your password.");

        // Data is only offered for removal where it can go to the Trash and come back.
        var dataPath = app.DataPath is { } path && _trash?.WhyNot(path) is null && _trash is not null ? path : null;
        var technical = new Dictionary<string, string>
        {
            ["ref"] = "app/" + app.Technical["ref"],
            ["installation"] = installation,
        };
        if (RemoteOf(app.SourceId) is { } remote)
            technical["remote"] = remote;

        return new ChangePlan(
            ProviderId, app.Id, app.Name, [new(app.Name, app.Version is null ? null : $"Version {app.Version}", app.SizeBytes)])
        {
            DiskBytes = app.SizeBytes,
            Notes = notes,
            DataPath = dataPath,
            DataBytes = dataPath is null ? null : FolderSize.Measure(dataPath, ct),
            Origin = app.SourceId,
            Technical = technical,
        };
    }, ct);

    public async Task<RemovalResult> RemoveAsync(
        ChangePlan plan, bool removeData, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        var (reference, _, installation) = Checked(plan);

        progress.Report(new(OperationPhase.Waiting));
        // Never --delete-data: that would erase the app's data for good, with no way back.
        await RunChangeAsync(
            ["uninstall", $"--{installation}", "--noninteractive", reference], RemoveTimeout,
            OperationPhase.Removing, progress, ct).ConfigureAwait(false);

        if (!removeData || plan.DataPath is not { } data || _trash is null)
            return new([]);
        // The data folder is named after the app; anything else in a plan is not one I made.
        if (_appDataDir is null || Path.GetDirectoryName(data) != _appDataDir.TrimEnd('/') ||
            Path.GetFileName(data) != reference.Split('/')[1])
        {
            throw new InvalidOperationException("The app is removed, but its data folder is not where I expected, so I left it.");
        }
        progress.Report(new(OperationPhase.Finishing, Detail: "moving its data to the Trash", CanCancel: false));
        return new([_trash.Move(data)]);
    }

    // Runs a command that changes what is installed, passing on how far along it is.
    private async Task RunChangeAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, OperationPhase phase, IProgress<OperationProgress> progress,
        CancellationToken ct)
    {
        await EnsureSetUpAsync().WaitAsync(ct).ConfigureAwait(false);
        // Flatpak names each part as it starts on it but gives no percentages here, so the bar can
        // only say which part it is on, not how far along.
        var step = 0;
        var result = await _runner.RunAsync(_flatpak!, arguments, timeout, line =>
        {
            if (FlatpakOutput.ParseStep(line) is { } part)
                progress.Report(new(phase, Fraction: null, $"part {Interlocked.Increment(ref step)}: {part}"));
        }, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(LastLine(result.StdErr.Length > 0 ? result.StdErr : result.StdOut));
    }

    // A plan comes back from the window; check again that it names one app and nothing else.
    private (string Ref, string? Remote, string Installation) Checked(ChangePlan plan)
    {
        var reference = plan.Technical.GetValueOrDefault("ref");
        var remote = plan.Technical.GetValueOrDefault("remote");
        var installation = plan.Technical.GetValueOrDefault("installation");
        if (_flatpak is null || plan.ProviderId != ProviderId || reference is null || !IsRef(reference) ||
            !reference.StartsWith("app/", StringComparison.Ordinal) || reference.Split('/').Length != 4 ||
            installation is not ("user" or "system") ||
            (remote is not null && remote != SourceAddress.SanitizeName(remote)))
        {
            throw new InvalidOperationException("This is not a plan I made for a Flatpak app.");
        }
        return (reference, remote, installation);
    }

    private async Task<bool> HasRuntimeAsync(string runtime, CancellationToken ct)
    {
        var parts = runtime.Split('/');
        if (parts.Length != 3)
            return false;
        var output = await RunAsync(["list", "--runtime", "--columns=application,branch"], ct).ConfigureAwait(false);
        return output.Split('\n').Any(line => line.Split('\t') is [var id, var branch] && id.Trim() == parts[0] && branch.Trim() == parts[2]);
    }

    private static long? Size(IReadOnlyDictionary<string, string> info, string key) =>
        info.TryGetValue(key, out var text) ? FlatpakOutput.ParseSize(text) : null;

    // The catalog names the full ref where it can; the plain ID does as well.
    private static string? RefOf(StoreOffer offer)
    {
        if (offer.PackageName is { } bundle && IsRef(bundle) && bundle.StartsWith("app/", StringComparison.Ordinal))
            return bundle;
        var id = offer.ComponentId.EndsWith(".desktop", StringComparison.Ordinal)
            ? offer.ComponentId[..^".desktop".Length]
            : offer.ComponentId;
        return IsRef(id) && id.Contains('.') && !id.Contains('/') ? id : null;
    }

    // "system:flathub" names the source flathub.
    private static string? RemoteOf(string? sourceId) =>
        sourceId?.Split(':', 2) is [_, var remote] && remote.Length > 0 && remote == SourceAddress.SanitizeName(remote)
            ? remote
            : null;

    private static string LastLine(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? message;
        return line.StartsWith("error: ", StringComparison.Ordinal) ? line["error: ".Length..] : line;
    }
}
