using Yar.Core;

namespace Yar.Backends.Bundles;

// Removing apps nobody manages. What Yar added itself is removed exactly, from its note. Of
// anything else only what is certainly the app's own is touched: an AppImage file and the
// app-menu entry. Everything goes to the Trash; nothing is erased.
public sealed partial class ManualAppsProvider
{
    private static AppInfo FromNote(InstalledManifest note) => new(
        ProviderId, Id: note.InstallPath, note.Name, Summary: null, IconName: null, IconPath: note.IconPath,
        Version: null, SourceId, SizeBytes: null, InstalledAt: null, LauncherPaths: [], ComponentId: null,
        Technical: new Dictionary<string, string> { ["in-menu"] = "false" });

    private static AppInfo WithNote(AppInfo app, InstalledManifest note, CancellationToken ct)
    {
        var technical = new Dictionary<string, string>(app.Technical)
        {
            ["added-by-yar"] = note.Id,
            ["kind"] = note.Kind,
            ["hash-verified"] = note.HashVerified ? "true" : "false",
        };
        if (note.Origin is not null)
            technical["origin"] = note.Origin;
        if (note.Sha256 is not null)
            technical["sha256"] = note.Sha256;
        if (note.Program is not null)
            technical.TryAdd("program", note.Program);

        long? size = app.SizeBytes;
        if (Directory.Exists(note.InstallPath))
            size = FolderSize.Measure(note.InstallPath, ct);
        else if (File.Exists(note.InstallPath))
            size = new FileInfo(note.InstallPath).Length;

        return app with
        {
            InstalledAt = note.AddedAt,
            InstallPath = note.InstallPath,
            SizeBytes = size,
            Technical = technical,
        };
    }

    public string? WhyNotRemovable(AppInfo app)
    {
        if (app.ProviderId != ProviderId || _trash is null)
            return "I can't remove this app from here.";
        var (paths, _, problem) = WhatGoes(app);
        return problem ?? (paths.Count == 0 ? "Nothing of this app is in a place I may remove things from." : null);
    }

    public Task<ChangePlan> PlanRemovalAsync(AppInfo app, CancellationToken ct) => Task.Run(() =>
    {
        if (WhyNotRemovable(app) is { } reason)
            return ChangePlan.Refused(ProviderId, app.Id, app.Name, reason);

        var (paths, stays, _) = WhatGoes(app);
        var items = paths.Select(path => new PlanItem(Describe(path, app), path, SizeOf(path, ct))).ToList();
        var notes = new List<string> { "Everything goes to your Trash, so you can still get it back." };
        if (stays is not null)
        {
            notes.Add(
                $"Its folder ({stays}) stays where it is. I didn't put it there, so I can't know what else is in it; " +
                "delete it yourself if you are sure.");
        }
        notes.Add("If the app kept settings somewhere in your home folder, those stay. I can't tell where they are.");

        return new ChangePlan(ProviderId, app.Id, app.Name, items)
        {
            DiskBytes = items.Any(i => i.Bytes is null) ? null : items.Sum(i => i.Bytes!.Value),
            Notes = notes,
            Origin = app.Technical.GetValueOrDefault("origin"),
            Technical = new Dictionary<string, string>
            {
                ["paths"] = string.Join('\n', paths),
                ["note"] = app.Technical.GetValueOrDefault("added-by-yar") ?? "",
            },
        };
    }, ct);

    public Task<RemovalResult> RemoveAsync(
        ChangePlan plan, bool removeData, IProgress<OperationProgress> progress, CancellationToken ct) => Task.Run(() =>
    {
        if (plan.ProviderId != ProviderId || _trash is null || plan.Technical.GetValueOrDefault("paths") is not { Length: > 0 } listed)
            throw new InvalidOperationException("This is not a plan I made for one of your own apps.");

        var paths = listed.Split('\n');
        var noteId = plan.Technical.GetValueOrDefault("note") ?? "";
        var noted = noteId.Length > 0 && _manifests?.ReadAll().FirstOrDefault(n => n.Id == noteId) is { } note
            ? note.Paths(_manifests.PathOf(note.Id)).ToHashSet(StringComparer.Ordinal)
            : [];
        // A plan comes back from the window; each path must again be one I am allowed to take.
        foreach (var path in paths)
        {
            if (!noted.Contains(path) && !IsOwnLauncher(path) && AppImageFile.DetectType(path) is null)
                throw new InvalidOperationException($"I won't move {path} to the Trash: it is not something I can be sure belongs to this app.");
            if (_trash.WhyNot(path) is { } reason)
                throw new InvalidOperationException($"I can't move {path} to the Trash. {reason}");
        }

        progress.Report(new(OperationPhase.Removing, CanCancel: false));
        var trashed = new List<TrashedItem>();
        foreach (var path in paths)
            trashed.Add(_trash.Move(path));
        return new RemovalResult(trashed);
    }, ct);

    public async Task<IntegrityResult> CheckAsync(AppInfo app, CancellationToken ct)
    {
        var then = app.Technical.GetValueOrDefault("sha256");
        var verified = app.Technical.GetValueOrDefault("hash-verified") == "true";
        if (app.ProviderId != ProviderId || app.InstallPath is not { } path || !File.Exists(path))
            return new(null, null, then, verified);
        var now = await HashCheck.ComputeAsync(path, HashKind.Sha256, ct).ConfigureAwait(false);
        return new(then is null ? null : then == now, now, then, verified);
    }

    // What removing takes, and the folder that is deliberately left.
    private (List<string> Paths, string? Stays, string? Problem) WhatGoes(AppInfo app)
    {
        var paths = new List<string>();
        string? stays = null;

        if (app.Technical.GetValueOrDefault("added-by-yar") is { } noteId &&
            _manifests?.ReadAll().FirstOrDefault(n => n.Id == noteId) is { } note)
        {
            foreach (var path in note.Paths(_manifests.PathOf(note.Id)).Where(p => File.Exists(p) || Directory.Exists(p)))
            {
                if (_trash!.WhyNot(path) is { } reason)
                    return ([], null, $"I can't move {path} to the Trash. {reason} You would have to delete it yourself.");
                paths.Add(path);
            }
            return (paths, null, null);
        }

        if (app.InstallPath is { } installed)
        {
            if (File.Exists(installed) && AppImageFile.DetectType(installed) is not null)
            {
                if (_trash!.WhyNot(installed) is { } reason)
                    return ([], null, $"I can't move {installed} to the Trash. {reason} You would have to delete it yourself.");
                paths.Add(installed);
            }
            else if (Directory.Exists(installed))
            {
                stays = installed;
            }
        }
        paths.AddRange(app.LauncherPaths.Where(path => IsOwnLauncher(path) && _trash!.WhyNot(path) is null));
        return (paths, stays, null);
    }

    // Only entries in the user's own app-menu folder; the system-wide ones need an administrator.
    private bool IsOwnLauncher(string path) =>
        path.EndsWith(".desktop", StringComparison.Ordinal) && Path.GetDirectoryName(path) + "/" == _launcherDirs[0];

    private static string Describe(string path, AppInfo app) =>
        path.EndsWith(".desktop", StringComparison.Ordinal) ? "App menu entry"
        : path.EndsWith(".json", StringComparison.Ordinal) ? "My note about this app"
        : path == app.InstallPath ? app.Name
        : "Its picture";

    private static long? SizeOf(string path, CancellationToken ct) =>
        Directory.Exists(path) ? FolderSize.Measure(path, ct) : File.Exists(path) ? new FileInfo(path).Length : null;
}
