using Yar.Core;

namespace Yar.Backends.Bundles;

// Putting a brought app in place: the plan the user confirms, then exactly that, and nothing
// left half done when something goes wrong.
public sealed partial class BundleIntake
{
    public Task<ChangePlan> PlanAsync(IntakeReport report, IntakeChoices choices, CancellationToken ct)
    {
        if (!report.FolderIsChoosable && report.Blocked is null &&
            otherKinds?.GetValueOrDefault(report.Technical.GetValueOrDefault("format") ?? "") is { } handler)
        {
            return handler.PlanAsync(report, choices, ct);
        }
        return Task.FromResult(Plan(report, choices).Plan);
    }

    public Task<AddedApp> AddAsync(
        IntakeReport report, IntakeChoices choices, ChangePlan plan, IProgress<OperationProgress> progress,
        CancellationToken ct)
    {
        if (!report.FolderIsChoosable && report.Blocked is null &&
            otherKinds?.GetValueOrDefault(report.Technical.GetValueOrDefault("format") ?? "") is { } handler)
        {
            return handler.AddAsync(report, choices, plan, progress, ct);
        }
        return Task.Run(() => Add(report, choices, plan, progress, ct), ct);
    }

    private sealed record Layout(string Slug, string Destination, string? Program, string? Launcher, string? Icon);

    private (ChangePlan Plan, Layout? Layout) Plan(IntakeReport report, IntakeChoices choices)
    {
        var name = CleanValue(choices.Name);
        ChangePlan Refused(string reason) => ChangePlan.Refused(ProviderId, report.FileName, name.Length > 0 ? name : report.SuggestedName, reason);

        if (report.Blocked is { } blocked)
            return (Refused(blocked), null);
        if (name.Length == 0)
            return (Refused("The app needs a name."), null);
        if (!File.Exists(report.SourcePath) || (report.Kind == BundleKind.Archive && !Directory.Exists(report.StagedPath)))
            return (Refused("The file is not there any more. Drop it in again."), null);

        var folder = choices.Folder.Length > 1 ? choices.Folder.TrimEnd('/') : choices.Folder;
        if (!Path.IsPathFullyQualified(folder) || Path.GetFullPath(folder) != folder || folder.Any(c => char.IsControl(c) || c is '"' or '`' or '$' or '\\' or '%'))
            return (Refused("That folder's name has characters in it that an app-menu entry can't carry safely. Pick another folder."), null);
        if (File.Exists(folder))
            return (Refused("That is a file, not a folder."), null);

        string? program = null;
        if (report.Kind == BundleKind.Archive)
        {
            program = choices.Program ?? (report.Programs.Count > 0 ? report.Programs[0].Path : null);
            if (program is null || report.Programs.All(p => p.Path != program))
                return (Refused("Pick which program starts the app."), null);
            if (program.Any(c => char.IsControl(c) || c is '"' or '`' or '$' or '\\' or '%'))
                return (Refused("The program inside has characters in its name that an app-menu entry can't carry safely."), null);
        }
        if (choices.IconPath is { } picture && !BundleInspector.IsIcon(picture))
            return (Refused("The picture has to be a PNG or SVG file of at most 5 MB."), null);

        var slug = FreeSlug(name, folder, report.Kind);
        var destination = Path.Combine(folder, DestinationName(slug, report.Kind));
        var launcher = choices.AppMenu ? Path.Combine(places.LauncherDir, $"yar-{slug}.desktop") : null;
        var icon = choices.IconPath is { } chosen
            ? Path.Combine(places.IconDir, slug + Path.GetExtension(chosen).ToLowerInvariant())
            : null;

        var needed = report.InstalledBytes ?? report.FileBytes;
        var existing = folder;
        while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
            existing = parent;
        try
        {
            if (new DriveInfo(existing).AvailableFreeSpace < needed + SpareBytes)
                return (Refused($"There is not enough room in {folder} for this app."), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        var items = new List<PlanItem> { new(name, destination, needed) };
        if (launcher is not null)
            items.Add(new("App menu entry", launcher));
        if (icon is not null)
            items.Add(new("Its picture", icon));

        var notes = new List<string>
        {
            "Nobody checks or updates this app for you. When a new version comes out, you add it again.",
        };
        var warnings = new List<string>();
        if (!choices.HashVerified)
            notes.Add("It was not compared with a hash from its publisher, so I can't tell whether it is the genuine file.");
        else
            notes.Add("It matches the hash you pasted from its publisher.");
        if (!destination.StartsWith(places.Home.TrimEnd('/') + "/", StringComparison.Ordinal))
            warnings.Add("That folder is outside your home folder, where I can't use the Trash. I couldn't remove this app for you later; you would delete it yourself.");
        if (choices.DeleteOriginal)
            notes.Add(report.Kind == BundleKind.Archive
                ? $"The archive you brought ({report.FileName}) goes to the Trash once the app is in place."
                : $"The file you brought ({report.FileName}) is moved, not copied.");

        var plan = new ChangePlan(ProviderId, launcher is null ? destination : Path.GetFileName(launcher), name, items)
        {
            DiskBytes = needed,
            Warnings = warnings,
            Notes = notes,
            Origin = report.OriginUrl ?? report.FileName,
            Technical = new Dictionary<string, string>
            {
                ["destination"] = destination,
                ["sha256"] = report.Sha256,
            },
        };
        return (plan, new(slug, destination, program, launcher, icon));
    }

    private AddedApp Add(
        IntakeReport report, IntakeChoices choices, ChangePlan shown, IProgress<OperationProgress> progress,
        CancellationToken ct)
    {
        // Worked out afresh: what is done must be what was shown, as things are now.
        var (plan, layout) = Plan(report, choices);
        if (plan.Blocked is { } blocked)
            throw new InvalidOperationException(blocked);
        if (layout is null || plan.Technical["destination"] != shown.Technical.GetValueOrDefault("destination") || plan.Target != shown.Target)
            throw new InvalidOperationException("Something changed since the preview. Look at it again and confirm once more.");

        var name = plan.Name;
        var created = new List<string>();
        var movedOriginal = false;
        // Only what was not there a moment before it was made is Yar's to clean up if something goes wrong.
        void Claim(string path)
        {
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"{path} is already there.");
            created.Add(path);
        }
        try
        {
            progress.Report(new(OperationPhase.Installing, Detail: "putting it in place"));
            Directory.CreateDirectory(Path.GetDirectoryName(layout.Destination)!);
            ct.ThrowIfCancellationRequested();

            // The picture first: it may sit inside the very folder that is about to move.
            if (layout.Icon is not null)
            {
                Directory.CreateDirectory(places.IconDir);
                Claim(layout.Icon);
                File.Copy(choices.IconPath!, layout.Icon, overwrite: false);
            }

            if (report.Kind == BundleKind.Archive)
            {
                Claim(layout.Destination);
                MoveFolder(report.StagedPath!, layout.Destination, ct);
            }
            else
            {
                Claim(layout.Destination);
                if (choices.DeleteOriginal)
                {
                    File.Move(report.SourcePath, layout.Destination, overwrite: false);
                    movedOriginal = true;
                }
                else
                {
                    File.Copy(report.SourcePath, layout.Destination, overwrite: false);
                }
                File.SetUnixFileMode(
                    layout.Destination,
                    File.GetUnixFileMode(layout.Destination) | UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            // From here on stopping would leave half an app; it is a matter of a few small files.
            progress.Report(new(OperationPhase.Finishing, Detail: "adding it to your app menu", CanCancel: false));
            var program = layout.Program is null ? layout.Destination : Path.Combine(layout.Destination, layout.Program);
            if (layout.Launcher is not null)
            {
                Directory.CreateDirectory(places.LauncherDir);
                Claim(layout.Launcher);
                using var stream = new FileStream(layout.Launcher, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.Write(LauncherText(
                    name, report.Summary, program, report.Kind == BundleKind.Archive ? layout.Destination : null,
                    layout.Icon, report.Categories, layout.Slug, takesFiles: report.Kind == BundleKind.AppImage));
            }

            Claim(manifests.PathOf(layout.Slug));
            manifests.Write(new(
                layout.Slug, name, report.Kind.ToString(), (time ?? TimeProvider.System).GetUtcNow(), layout.Destination,
                program, layout.Launcher, layout.Icon, report.OriginUrl ?? report.FileName, report.Sha256, choices.HashVerified));
        }
        catch
        {
            // Leave nothing half done behind.
            foreach (var path in Enumerable.Reverse(created))
            {
                try
                {
                    // The user's own file was moved, not copied: it goes back, it is not deleted.
                    if (movedOriginal && path == layout.Destination && !File.Exists(report.SourcePath))
                        File.Move(path, report.SourcePath, overwrite: false);
                    else if (Directory.Exists(path))
                        Directory.Delete(path, recursive: true);
                    else
                        File.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
            throw;
        }

        if (choices.DeleteOriginal && report.Kind == BundleKind.Archive && trash.WhyNot(report.SourcePath) is null)
            trash.Move(report.SourcePath);
        Discard(report);
        return new(ProviderId, plan.Target, name);
    }

    // Moving is instant on the same disk; onto another disk the folder is copied piece by piece.
    private static void MoveFolder(string from, string to, CancellationToken ct)
    {
        if (Directory.Exists(to) || File.Exists(to))
            throw new IOException($"{to} is already there.");
        try
        {
            Directory.Move(from, to);
            return;
        }
        catch (IOException) when (!Directory.Exists(to))
        {
        }
        CopyFolder(new DirectoryInfo(from), to, ct);
    }

    private static void CopyFolder(DirectoryInfo from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in from.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(to, entry.Name);
            if (entry.LinkTarget is { } link)
                File.CreateSymbolicLink(target, link);
            else if (entry is DirectoryInfo folder)
                CopyFolder(folder, target, ct);
            else
                File.Copy(entry.FullName, target, overwrite: false);
        }
    }
}
