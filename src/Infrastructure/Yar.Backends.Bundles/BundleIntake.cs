using System.Globalization;
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

        // Most archives wrap everything in one folder; that folder is the app. Only that one wrapper
        // is taken off: a lone "bin" folder inside it is part of how the app is laid out.
        var root = unpacked;
        if (Directory.EnumerateFileSystemEntries(root).Take(2).ToList() is [var only] &&
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
}
