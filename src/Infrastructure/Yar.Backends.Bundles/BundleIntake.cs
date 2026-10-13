using System.Globalization;
using System.Text.RegularExpressions;
using Yar.Core;
using Yar.Metadata;

namespace Yar.Backends.Bundles;

/// <summary>Where the things Yar creates for an added app go.</summary>
/// <param name="CacheDir">Yar's own cache folder; unpacked archives wait here until the user decides.</param>
/// <param name="LauncherDir">The user's own app-menu folder.</param>
/// <param name="IconDir">Where Yar keeps the pictures of apps it added.</param>
public sealed record IntakePlaces(string Home, string CacheDir, string LauncherDir, string IconDir);

/// <summary>
/// Takes an app the user brought as a file. Each kind has one standard way of being installed:
/// an AppImage or single program is one file in the apps folder, an archive is unpacked into a
/// folder of its own there, and packages and Flatpak files are handed to the tool that owns them.
/// Every one gets an app-menu entry and a note of what was created, so it can be removed again.
/// </summary>
public sealed partial class BundleIntake(
    IntakePlaces places, ManifestStore manifests, IProcessRunner runner, ITrash trash, BundleInspector inspector,
    IReadOnlyDictionary<string, IFileIntakeHandler>? otherKinds = null, TimeProvider? time = null) : IAppIntake
{
    public const string ProviderId = ManualAppsProvider.ProviderId;

    // More than any real app; a "zip bomb" claims far more.
    private const long MaxUnpackedBytes = 40L * 1024 * 1024 * 1024;
    private const long SpareBytes = 200 * 1024 * 1024;

    private static readonly string[] Unsquashfs = ["/usr/bin/unsquashfs", "/usr/sbin/unsquashfs", "/bin/unsquashfs"];
    private static readonly string[] FuseLibraries = ["libfuse.so.2"];
    private static readonly string[] IconEndings = [".svg", ".png"];

    private string IntakeDir => Path.Combine(places.CacheDir, "intake");

    public async Task<IntakeReport> InspectAsync(string path, string? originUrl, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        var identity = FileIdentifier.Identify(path);
        var fileName = Path.GetFileName(path);
        if (identity.Kind == BundleKind.Unsupported)
        {
            return new(path, fileName, identity.Kind, identity.Label, FileBytes: 0, Sha256: "", CleanName(fileName))
            {
                OriginUrl = originUrl,
                Blocked = identity.WhyNot,
                Checks = [new(CheckState.Bad, identity.WhyNot!)],
            };
        }

        var basics = new IntakeReport(
            path, fileName, identity.Kind, identity.Label, new FileInfo(path).Length,
            await HashCheck.ComputeAsync(path, HashKind.Sha256, ct).ConfigureAwait(false), CleanName(fileName))
        {
            OriginUrl = originUrl,
            Technical = new Dictionary<string, string> { ["format"] = identity.Format },
        };

        try
        {
            return identity.Kind switch
            {
                BundleKind.AppImage or BundleKind.Program => await InspectSingleAsync(basics, ct).ConfigureAwait(false),
                BundleKind.Archive => await Task.Run(() => InspectArchive(basics, identity.Format, ct), ct).ConfigureAwait(false),
                _ when otherKinds?.GetValueOrDefault(identity.Format) is { } handler =>
                    await handler.InspectAsync(basics, ct).ConfigureAwait(false),
                _ => Refuse(basics, "I can't install this kind of file on this computer."),
            };
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            Discard(basics);
            return Refuse(basics, e.Message);
        }
    }

    public void Discard(IntakeReport report)
    {
        // Only ever a folder Yar made itself inside its own cache.
        if (StageOf(report) is { } stage && Directory.Exists(stage))
        {
            try { Directory.Delete(stage, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static IntakeReport Refuse(IntakeReport report, string reason) =>
        report with { Blocked = reason, Checks = [.. report.Checks, new(CheckState.Bad, reason)] };

    private string? StageOf(IntakeReport report) =>
        report.Technical.GetValueOrDefault("stage") is { } id && id.Length == 32 && id.All(char.IsAsciiHexDigit)
            ? Path.Combine(IntakeDir, id)
            : null;

    private (string Id, string Path) NewStage()
    {
        // What an earlier, abandoned look left behind is not kept for ever.
        try
        {
            if (Directory.Exists(IntakeDir))
            {
                foreach (var old in Directory.EnumerateDirectories(IntakeDir)
                    .Where(d => Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-1)))
                {
                    Directory.Delete(old, recursive: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(IntakeDir, id);
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (id, path);
    }

    private async Task<IntakeReport> InspectSingleAsync(IntakeReport basics, CancellationToken ct)
    {
        var checks = new List<IntakeCheck>();
        var elf = ElfFile.Read(basics.SourcePath);
        if (elf is null)
            return Refuse(basics, "The file is damaged: it starts like a program but can't be read as one.");

        string? blocked = null;
        if (basics.Kind == BundleKind.Program)
        {
            (checks, blocked) = inspector.CheckProgram(basics.SourcePath, "This program");
            checks.Add(new(CheckState.Warning,
                "A single program file carries no name, picture or description, so I can't tell whether it is an app with a window or a tool for the terminal."));
            return basics with { Checks = checks, Blocked = blocked, InstalledBytes = basics.FileBytes };
        }

        if (elf.Machine != BundleInspector.ThisMachine)
        {
            blocked = $"This AppImage is built for {elf.MachineName}, and this computer has a different kind of processor. It can't run here.";
            checks.Add(new(CheckState.Bad, blocked));
            return basics with { Checks = checks, Blocked = blocked };
        }
        checks.Add(new(CheckState.Good, $"Built for this computer's processor ({elf.MachineName})."));

        // Older AppImages start through a helper library; newer ones have it built in.
        if (elf.Interpreter is not null && !FuseLibraries.Any(name => new LibraryFinder().Has(name, elf)))
        {
            checks.Add(new(CheckState.Warning,
                "This AppImage is of the older kind that needs a helper called FUSE 2 (libfuse2), which I can't find on this computer. " +
                "It may not start until that is installed."));
        }
        else
        {
            checks.Add(new(CheckState.Good, "It brings everything it needs along in the one file."));
        }

        var report = basics with { Checks = checks, InstalledBytes = basics.FileBytes };
        return await PeekIntoAppImageAsync(report, elf, ct).ConfigureAwait(false);
    }

    // Reads the name, description and picture an AppImage carries, with the system's own tool for
    // that kind of file. The AppImage itself is never started.
    private async Task<IntakeReport> PeekIntoAppImageAsync(IntakeReport report, ElfFile elf, CancellationToken ct)
    {
        var tool = Unsquashfs.FirstOrDefault(File.Exists);
        if (tool is null || elf.EndOfSections <= 0 || elf.EndOfSections >= report.FileBytes ||
            report.Technical.GetValueOrDefault("format") != "appimage")
        {
            return report with
            {
                Checks = [.. report.Checks, new(CheckState.Warning,
                    "I couldn't look inside for its name and picture" +
                    (tool is null ? " (that needs the tool unsquashfs, which is not installed)" : "") +
                    ", so I went by the file's name.")],
            };
        }

        var (id, stage) = NewStage();
        report = report with { Technical = new Dictionary<string, string>(report.Technical) { ["stage"] = id } };
        var offset = elf.EndOfSections.ToString(CultureInfo.InvariantCulture);
        string[] Extract(params string[] what) =>
            [tool, "-n", "-no-xattrs", "-f", "-o", offset, "-d", stage, report.SourcePath, .. what];

        // The tool complains about patterns that match nothing; what counts is what it produced.
        var first = Extract("*.desktop", ".DirIcon", "*.png", "*.svg");
        await runner.RunAsync(first[0], first[1..], TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);

        var launcher = Directory.Exists(stage)
            ? Directory.EnumerateFiles(stage, "*.desktop").Order(StringComparer.Ordinal)
                .Where(file => new FileInfo(file).LinkTarget is null)
                .Select(file => DesktopEntryParser.ParseFile(file, Path.GetFileName(file)))
                .FirstOrDefault(entry => entry is { Type: "Application" })
            : null;

        string? icon = null;
        var dirIcon = Path.Combine(stage, ".DirIcon");
        if (BundleInspector.IsIcon(dirIcon))
        {
            icon = dirIcon;
        }
        else if (new FileInfo(dirIcon).LinkTarget is { } target && IsPlainRelative(target))
        {
            // The picture is often a link to the real file elsewhere in the AppImage; fetch that one too.
            var second = Extract(target);
            await runner.RunAsync(second[0], second[1..], TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
            var pointed = Path.GetFullPath(Path.Combine(stage, target));
            if (pointed.StartsWith(stage + "/", StringComparison.Ordinal) && BundleInspector.IsIcon(pointed))
                icon = pointed;
        }
        if (icon is null && launcher?.Icon is { } named && !named.Contains('/'))
        {
            icon = IconEndings.Select(extension => Path.Combine(stage, named + extension))
                .FirstOrDefault(BundleInspector.IsIcon);
        }

        return report with
        {
            SuggestedName = launcher?.Name ?? report.SuggestedName,
            Summary = launcher?.Comment ?? launcher?.GenericName,
            Categories = launcher?.Categories ?? [],
            Version = launcher?.GetKey("X-AppImage-Version"),
            IconPath = icon,
        };
    }

    private IntakeReport InspectArchive(IntakeReport basics, string format, CancellationToken ct)
    {
        var (id, stage) = NewStage();
        var report = basics with { Technical = new Dictionary<string, string>(basics.Technical) { ["stage"] = id } };
        var unpacked = Path.Combine(stage, "app");
        var room = Math.Min(MaxUnpackedBytes, new DriveInfo(stage).AvailableFreeSpace - SpareBytes);
        UnpackResult result;
        try
        {
            result = ArchiveUnpacker.Unpack(basics.SourcePath, format, unpacked, room, ct);
        }
        catch
        {
            // Whatever was unpacked before it went wrong is not kept.
            Discard(report);
            throw;
        }

        // Most archives wrap everything in one folder; that folder is the app.
        var root = unpacked;
        while (Directory.EnumerateFileSystemEntries(root).Take(2).ToList() is [var only] &&
            Directory.Exists(only) && new DirectoryInfo(only).LinkTarget is null)
        {
            root = only;
        }

        var found = inspector.InspectFolder(root, basics.SuggestedName, ct);
        var checks = found.Checks.ToList();
        if (result.LeftOut.Count > 0)
        {
            checks.Add(new(CheckState.Warning,
                $"I left out {result.LeftOut.Count} {(result.LeftOut.Count == 1 ? "entry" : "entries")} that had no business in an app, " +
                $"such as links leading outside it: {string.Join(", ", result.LeftOut.Take(3))}."));
        }
        return report with
        {
            StagedPath = root,
            SuggestedName = found.Name ?? basics.SuggestedName,
            Summary = found.Summary,
            IconPath = found.IconPath,
            Programs = found.Programs,
            Categories = found.Categories,
            InstalledBytes = result.Bytes,
            Checks = checks,
            Blocked = found.Blocked,
        };
    }

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
        try
        {
            progress.Report(new(OperationPhase.Installing, Detail: "putting it in place"));
            Directory.CreateDirectory(Path.GetDirectoryName(layout.Destination)!);
            ct.ThrowIfCancellationRequested();

            // The picture first: it may sit inside the very folder that is about to move.
            if (layout.Icon is not null)
            {
                Directory.CreateDirectory(places.IconDir);
                created.Add(layout.Icon);
                File.Copy(choices.IconPath!, layout.Icon, overwrite: false);
            }

            if (report.Kind == BundleKind.Archive)
            {
                created.Add(layout.Destination);
                MoveFolder(report.StagedPath!, layout.Destination, ct);
            }
            else
            {
                created.Add(layout.Destination);
                if (choices.DeleteOriginal)
                    File.Move(report.SourcePath, layout.Destination, overwrite: false);
                else
                    File.Copy(report.SourcePath, layout.Destination, overwrite: false);
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
                created.Add(layout.Launcher);
                using var stream = new FileStream(layout.Launcher, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.Write(LauncherText(
                    name, report.Summary, program, report.Kind == BundleKind.Archive ? layout.Destination : null,
                    layout.Icon, report.Categories, layout.Slug, takesFiles: report.Kind == BundleKind.AppImage));
            }

            created.Add(manifests.PathOf(layout.Slug));
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
                    if (Directory.Exists(path))
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

    internal static string LauncherText(
        string name, string? summary, string program, string? workingFolder, string? icon,
        IReadOnlyList<string> categories, string slug, bool takesFiles)
    {
        var lines = new List<string>
        {
            "[Desktop Entry]",
            "Type=Application",
            $"Name={CleanValue(name)}",
        };
        if (!string.IsNullOrWhiteSpace(summary))
            lines.Add($"Comment={CleanValue(summary)}");
        lines.Add($"Exec=\"{program}\"{(takesFiles ? " %U" : "")}");
        if (workingFolder is not null)
            lines.Add($"Path={workingFolder}");
        lines.Add($"Icon={icon ?? "application-x-executable"}");
        lines.Add("Terminal=false");
        var kept = categories.Select(CleanValue).Where(c => c.Length > 0 && c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')).ToList();
        if (kept.Count > 0)
            lines.Add($"Categories={string.Join(';', kept)};");
        lines.Add($"X-Yar-App={slug}");
        return string.Join('\n', lines) + "\n";
    }

    // One line, no control characters, and no backslash to be read as an escape.
    private static string CleanValue(string text) =>
        new string(text.Where(c => !char.IsControl(c) && c != '\\').ToArray()).Trim();

    private static bool IsPlainRelative(string target) =>
        target.Length is > 0 and < 300 && !Path.IsPathRooted(target) && !target.Split('/').Contains("..") &&
        !target.Any(c => char.IsControl(c) || c is '*' or '?' or '[');

    private static string DestinationName(string slug, BundleKind kind) =>
        kind == BundleKind.AppImage ? slug + ".AppImage" : slug;

    // A short, plain name for files and folders, free in every place it will be used.
    private string FreeSlug(string name, string folder, BundleKind kind)
    {
        var plain = NotSlugCharacter().Replace(name.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormKD), "-").Trim('-');
        if (plain.Length == 0)
            plain = "app";
        if (plain.Length > 48)
            plain = plain[..48].Trim('-');
        for (var attempt = 1; ; attempt++)
        {
            var slug = attempt == 1 ? plain : $"{plain}-{attempt}";
            var destination = Path.Combine(folder, DestinationName(slug, kind));
            if (!manifests.Exists(slug) && !File.Exists(destination) && !Directory.Exists(destination) &&
                !File.Exists(Path.Combine(places.LauncherDir, $"yar-{slug}.desktop")) &&
                !IconEndings.Any(ending => File.Exists(Path.Combine(places.IconDir, slug + ending))))
            {
                return slug;
            }
        }
    }

    /// <summary>"krita-5.2.9-x86_64.AppImage" becomes "Krita": the file name without version, processor and file type.</summary>
    public static string CleanName(string fileName)
    {
        var name = FileTypeEnding().Replace(fileName, "");
        name = VersionOnwards().Replace(name, "");
        name = ProcessorOrSystem().Replace(name, " ");
        name = name.Replace('_', ' ').Replace('-', ' ').Trim(' ', '.');
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!name.Any(char.IsLetter))
            return "App";
        return char.IsLower(name[0]) && name.Skip(1).All(c => !char.IsUpper(c)) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    [GeneratedRegex(@"(\.tar)?\.(appimage|zip|gz|tgz|xz|txz|bz2|zst|tar|run|bin|rpm|deb|flatpak|flatpakref)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileTypeEnding();

    [GeneratedRegex(@"[-_ .]v?\d+(\.\d+)*([-_.][0-9A-Za-z]+)*$")]
    private static partial Regex VersionOnwards();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(x86[-_]64|amd64|x64|aarch64|arm64|i[36]86|linux|gnu|glibc|portable)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessorOrSystem();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotSlugCharacter();
}
