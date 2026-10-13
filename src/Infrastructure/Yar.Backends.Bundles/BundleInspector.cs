using System.Runtime.InteropServices;
using Yar.Core;
using Yar.Metadata;

namespace Yar.Backends.Bundles;

/// <summary>What looking inside an app found: its name, picture, the programs it could be started with, and whether it can run here.</summary>
public sealed record InspectedApp(
    string? Name,
    string? Summary,
    string? IconPath,
    IReadOnlyList<ProgramChoice> Programs,
    IReadOnlyList<string> Categories,
    IReadOnlyList<IntakeCheck> Checks,
    string? Blocked);

/// <summary>
/// Looks inside an unpacked app, or at a single program file, for what the preview shows. Files
/// are read; nothing is run, and links are not followed.
/// </summary>
public sealed class BundleInspector(LibraryFinder libraries)
{
    private const int MaxFilesLooked = 60_000;
    private const int MaxDepth = 6;
    private const long MaxIconBytes = 5 * 1024 * 1024;

    private static readonly string[] SourceMarkers =
        ["configure", "configure.ac", "Makefile", "CMakeLists.txt", "meson.build", "Cargo.toml", "setup.py", "pyproject.toml", "go.mod", "package.json"];

    /// <summary>The ELF machine number of the processor Yar itself runs on.</summary>
    public static ushort ThisMachine => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => ElfFile.X8664,
        Architecture.Arm64 => ElfFile.Aarch64,
        Architecture.X86 => ElfFile.X86,
        Architecture.Arm => ElfFile.Arm,
        Architecture.RiscV64 => ElfFile.RiscV,
        _ => 0,
    };

    /// <summary>Checks one program file: right processor, and are the libraries it asks for there?</summary>
    /// <param name="shipped">Names of library files that come with the app itself.</param>
    public (List<IntakeCheck> Checks, string? Blocked) CheckProgram(string path, string label, IReadOnlySet<string>? shipped = null)
    {
        var checks = new List<IntakeCheck>();
        if (ElfFile.Read(path) is not { } elf)
            return (checks, null);

        if (elf.Machine != ThisMachine)
        {
            var reason = $"{label} is built for {elf.MachineName}, and this computer has a different kind of processor. It can't run here.";
            checks.Add(new(CheckState.Bad, reason));
            return (checks, reason);
        }
        checks.Add(new(CheckState.Good, $"Built for this computer's processor ({elf.MachineName})."));

        if (elf.Interpreter is null)
        {
            checks.Add(new(CheckState.Good, "Needs nothing else installed: everything is built in."));
            return (checks, null);
        }
        if (!File.Exists(elf.Interpreter))
        {
            checks.Add(new(CheckState.Warning,
                $"It expects a different kind of Linux system than this one (it looks for {elf.Interpreter}), so it will probably not start."));
        }

        var missing = elf.Needed
            .Where(name => shipped?.Contains(name) != true && !libraries.Has(name, elf))
            .Distinct(StringComparer.Ordinal).ToList();
        checks.Add(missing.Count == 0
            ? new(CheckState.Good, "The shared parts it asks for are all on this computer or come with it.")
            : new(CheckState.Warning,
                $"It asks for {(missing.Count == 1 ? "a shared part" : "shared parts")} I can't find here: {string.Join(", ", missing.Take(8))}" +
                $"{(missing.Count > 8 ? " and more" : "")}. It may not start until {(missing.Count == 1 ? "that is" : "those are")} installed."));
        return (checks, null);
    }

    /// <summary>Looks through an unpacked archive.</summary>
    public InspectedApp InspectFolder(string root, string fallbackName, CancellationToken ct)
    {
        var files = new List<(string Relative, FileInfo Info)>();
        Walk(new DirectoryInfo(root), "", 0, files, ct);

        var shipped = files.Select(f => f.Info.Name).Where(n => n.Contains(".so", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var launcher = files
            .Where(f => f.Info.Extension == ".desktop" && f.Info.Length <= DesktopEntryParser.MaxFileBytes)
            .OrderBy(f => f.Relative.Count(c => c == '/'))
            .Select(f => DesktopEntryParser.ParseFile(f.Info.FullName, f.Info.Name))
            .FirstOrDefault(entry => entry is { Type: "Application" });

        var programs = new List<(ProgramChoice Choice, int Rank, bool IsElf)>();
        foreach (var (relative, info) in files)
        {
            ct.ThrowIfCancellationRequested();
            var depth = relative.Count(c => c == '/');
            if (depth > 3 || info.Length < 4 || !IsRunnable(info) || info.Name.Contains(".so", StringComparison.Ordinal))
                continue;
            var start = FirstBytes(info.FullName);
            var isElf = ElfFile.HasMagic(start);
            var isScript = start.Length >= 2 && start[0] == '#' && start[1] == '!';
            if (isElf && ElfFile.Read(info.FullName) is not { IsProgram: true })
                continue;
            if (!isElf && (!isScript || depth > 1))
                continue;

            var stem = Path.GetFileNameWithoutExtension(info.Name);
            var rank = depth * 10 + (isElf ? 0 : 3);
            if (Matches(stem, fallbackName) || (launcher?.Name is { } named && Matches(stem, named)))
                rank -= 25;
            if (relative.StartsWith("bin/", StringComparison.Ordinal))
                rank -= 5;
            programs.Add((new(relative, isElf ? relative : $"{relative} (a start script)"), rank, isElf));
        }

        // The app's own launcher knows best which program is the one to start.
        var started = DesktopExec.Split(launcher?.Exec)?.FirstOrDefault(part => !part.Contains('='));
        var ordered = programs
            .OrderBy(p => started is not null && Path.GetFileName(p.Choice.Path) == Path.GetFileName(started) ? 0 : 1)
            .ThenBy(p => p.Rank).ThenBy(p => p.Choice.Path, StringComparer.Ordinal)
            .Take(30).ToList();

        var checks = new List<IntakeCheck>();
        string? blocked = null;
        if (ordered.Count == 0)
        {
            blocked = files.Any(f => f.Info.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                ? "This archive holds a Windows program (.exe), not a Linux app. It can't run on Linux by itself."
                : files.Any(f => !f.Relative.Contains('/') && SourceMarkers.Contains(f.Info.Name))
                    ? "This is the app's source code: the recipe, not the finished app. It has to be built first, which I don't do. " +
                      "Look for a ready-made download on the same page, often marked linux, x86_64, AppImage or tar.gz."
                    : files.Any(f => f.Relative.Contains(".app/Contents/MacOS/", StringComparison.Ordinal))
                        ? "This archive holds a Mac app. It can't run on Linux."
                        : "I found no program in this archive that could be started on this computer.";
            checks.Add(new(CheckState.Bad, blocked));
        }
        else
        {
            checks.Add(new(CheckState.Good, ordered.Count == 1
                ? $"Holds a ready-to-run program: {ordered[0].Choice.Path}."
                : $"Holds {ordered.Count} programs that could be the one to start; I picked {ordered[0].Choice.Path}."));
            var first = ordered[0];
            if (first.IsElf)
            {
                var (programChecks, programBlocked) = CheckProgram(Path.Combine(root, first.Choice.Path), first.Choice.Path, shipped);
                checks.AddRange(programChecks);
                blocked = programBlocked;
            }
            else
            {
                checks.Add(new(CheckState.Warning, "It is started by a script, so I can't check ahead of time what it needs."));
            }
        }

        return new(
            launcher?.Name, launcher?.Comment ?? launcher?.GenericName,
            FindIcon(files, launcher?.Icon, ordered.Count > 0 ? Path.GetFileNameWithoutExtension(ordered[0].Choice.Path) : fallbackName),
            ordered.Select(p => p.Choice).ToList(), launcher?.Categories ?? [], checks, blocked);
    }

    /// <summary>True for a file that is a picture Yar can show as an icon: a PNG or an SVG of sensible size.</summary>
    public static bool IsIcon(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length is < 32 or > MaxIconBytes)
                return false;
            var start = FirstBytes(path, 512);
            if (start.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
                return true;
            var text = System.Text.Encoding.UTF8.GetString(start);
            return text.TrimStart('﻿', ' ', '\n', '\r', '\t').StartsWith('<') && text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? FindIcon(List<(string Relative, FileInfo Info)> files, string? wanted, string programName)
    {
        var pictures = files
            .Where(f => f.Info.Extension.ToLowerInvariant() is ".png" or ".svg" && f.Info.Length <= MaxIconBytes)
            .ToList();
        var wantedStem = wanted is null ? null : Path.GetFileNameWithoutExtension(Path.GetFileName(wanted));

        int Score((string Relative, FileInfo Info) f)
        {
            var stem = Path.GetFileNameWithoutExtension(f.Info.Name);
            var score = 0;
            if (wantedStem is not null && stem.Equals(wantedStem, StringComparison.OrdinalIgnoreCase))
                score += 100;
            if (Matches(stem, programName))
                score += 60;
            if (stem.Contains("icon", StringComparison.OrdinalIgnoreCase) || stem.Contains("logo", StringComparison.OrdinalIgnoreCase))
                score += 30;
            // Icons of the app itself sit near the top; pictures deep inside are usually parts of its interface.
            score -= f.Relative.Count(c => c == '/') * 4;
            if (f.Info.Extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
                score += 5;
            return score;
        }

        return pictures
            .Select(f => (File: f, Score: Score(f)))
            .Where(p => p.Score >= 25)
            .OrderByDescending(p => p.Score).ThenByDescending(p => p.File.Info.Length)
            .Select(p => p.File.Info.FullName)
            .FirstOrDefault(IsIcon);
    }

    // "krita" matches "Krita 5", "krita-bin" and "krita".
    private static bool Matches(string stem, string name)
    {
        static string Plain(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var a = Plain(stem);
        var b = Plain(name);
        return a.Length >= 3 && b.Length >= 3 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
    }

    private static bool IsRunnable(FileInfo info) =>
        (info.UnixFileMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private static byte[] FirstBytes(string path, int count = 4)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[count];
            var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
            return buffer[..read];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Lists regular files without ever stepping through a link.
    private static void Walk(DirectoryInfo folder, string prefix, int depth, List<(string, FileInfo)> into, CancellationToken ct)
    {
        if (depth > MaxDepth || into.Count >= MaxFilesLooked)
            return;
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = folder.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.LinkTarget is not null || into.Count >= MaxFilesLooked)
                continue;
            if (entry is DirectoryInfo sub)
                Walk(sub, prefix + sub.Name + "/", depth + 1, into, ct);
            else if (entry is FileInfo file)
                into.Add((prefix + file.Name, file));
        }
    }
}
