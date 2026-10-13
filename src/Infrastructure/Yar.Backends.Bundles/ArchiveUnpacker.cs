using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace Yar.Backends.Bundles;

/// <summary>What unpacking produced, and what it left out on purpose.</summary>
public sealed record UnpackResult(int Files, long Bytes, IReadOnlyList<string> LeftOut);

/// <summary>
/// Unpacks zip and tar archives from strangers. Nothing is ever written outside the target
/// folder: entries with ".." or absolute paths are refused, nothing is written through a link,
/// links may only point inside, and the total size and number of files are capped.
/// </summary>
public static class ArchiveUnpacker
{
    public const int MaxEntries = 300_000;

    private const UnixFileMode Permissions =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static readonly Dictionary<string, string[]> Decompressors = new(StringComparer.Ordinal)
    {
        ["tar.xz"] = ["/usr/bin/xz", "/bin/xz"],
        ["tar.bz2"] = ["/usr/bin/bzip2", "/bin/bzip2"],
        ["tar.zst"] = ["/usr/bin/zstd", "/bin/zstd"],
    };

    /// <param name="maxBytes">Unpacking stops with an error once this much has been written.</param>
    /// <exception cref="InvalidDataException">The archive is damaged, hostile or too big; the message says which.</exception>
    public static UnpackResult Unpack(string archive, string format, string target, long maxBytes, CancellationToken ct)
    {
        var root = Path.GetFullPath(target).TrimEnd('/');
        Directory.CreateDirectory(root);
        var state = new State(root, maxBytes, ct);
        try
        {
            if (format == "zip")
                UnpackZip(archive, state);
            else
                UnpackTar(archive, format, state);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or FormatException or ArgumentException)
        {
            throw new InvalidDataException(
                e is InvalidDataException { Message.Length: > 0 } ? e.Message : "The archive is damaged or cut short.", e);
        }
        return new(state.Files, state.Bytes, state.LeftOut);
    }

    private static void UnpackZip(string archive, State state)
    {
        using var zip = ZipFile.OpenRead(archive);
        // Archives made on Windows carry no permissions at all; those files are made runnable by what they are.
        var hasModes = zip.Entries.Any(entry => (entry.ExternalAttributes >> 16) != 0);
        foreach (var entry in zip.Entries)
        {
            state.Count();
            var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if (entry.FullName.EndsWith('/') || (mode & 0xF000) == 0x4000)
            {
                state.Folder(entry.FullName);
            }
            else if ((mode & 0xF000) == 0xA000)
            {
                using var reader = new StreamReader(entry.Open());
                var buffer = new char[4096];
                var length = reader.ReadBlock(buffer, 0, buffer.Length);
                state.Link(entry.FullName, new string(buffer, 0, length));
            }
            else
            {
                using var content = entry.Open();
                var path = state.File(entry.FullName, content, hasModes ? (UnixFileMode)(mode & 0x1FF) : null);
                if (!hasModes && path is not null && LooksRunnable(path))
                    File.SetUnixFileMode(path, Permissions);
            }
        }
    }

    private static void UnpackTar(string archive, string format, State state)
    {
        using var source = OpenTar(archive, format, state.Ct, out var helper);
        try
        {
            using var reader = new TarReader(source);
            while (reader.GetNextEntry() is { } entry)
            {
                state.Count();
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        state.Folder(entry.Name);
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        state.File(entry.Name, entry.DataStream ?? Stream.Null, entry.Mode);
                        break;
                    case TarEntryType.SymbolicLink:
                        state.Link(entry.Name, entry.LinkName);
                        break;
                    case TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes:
                        break;
                    default:
                        // Hard links, devices and pipes have no place in an app someone downloads.
                        state.Skip(entry.Name);
                        break;
                }
            }
        }
        finally
        {
            if (helper is not null)
            {
                try { helper.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                helper.Dispose();
            }
        }
    }

    // gzip is read by .NET itself; the other compressions by the system's own tool, whose output is piped in.
    private static Stream OpenTar(string archive, string format, CancellationToken ct, out Process? helper)
    {
        helper = null;
        var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        if (format == "tar")
            return file;
        if (format == "tar.gz")
            return new GZipStream(file, CompressionMode.Decompress);

        file.Dispose();
        var tool = Decompressors[format].FirstOrDefault(File.Exists)
            ?? throw new InvalidDataException($"Unpacking this needs the tool {Path.GetFileName(Decompressors[format][0])}, which is not installed.");
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-d", "-c", "--", archive })
            info.ArgumentList.Add(argument);
        helper = Process.Start(info) ?? throw new InvalidDataException("I couldn't start the tool that unpacks this.");
        var started = helper;
        // Nobody reads its complaints; without this it could stall on a full pipe.
        _ = started.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
        return started.StandardOutput.BaseStream;
    }

    private static bool LooksRunnable(string path)
    {
        Span<byte> start = stackalloc byte[4];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var read = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        return read >= 2 && (ElfFile.HasMagic(start[..read]) || start[..2].SequenceEqual("#!"u8));
    }

    private sealed class State(string root, long maxBytes, CancellationToken ct)
    {
        public int Files { get; private set; }
        public long Bytes { get; private set; }
        public List<string> LeftOut { get; } = [];
        public CancellationToken Ct => ct;
        private int _entries;

        public void Count()
        {
            ct.ThrowIfCancellationRequested();
            if (++_entries > MaxEntries)
                throw new InvalidDataException("The archive holds more files than I accept in one app.");
        }

        public void Skip(string name)
        {
            if (LeftOut.Count < 20)
                LeftOut.Add(name);
        }

        public void Folder(string name)
        {
            if (Resolve(name) is { } path)
                Directory.CreateDirectory(path);
        }

        public string? File(string name, Stream content, UnixFileMode? mode)
        {
            if (Resolve(name) is not { } path)
                return null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Never set-user-ID or the like, whatever the archive asks for; always readable and writable by the owner.
            var safeMode = ((mode ?? (UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead)) & Permissions)
                | UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = safeMode,
            });
            var buffer = new byte[1 << 16];
            int read;
            while ((read = content.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                Bytes += read;
                if (Bytes > maxBytes)
                    throw new InvalidDataException("The archive unpacks to more than there is room for, or more than I accept.");
                file.Write(buffer, 0, read);
            }
            Files++;
            return path;
        }

        public void Link(string name, string target)
        {
            if (Resolve(name) is not { } path)
                return;
            // A link may only lead to something inside the app's own folder.
            var leadsTo = target.Length == 0 || Path.IsPathRooted(target)
                ? null
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target));
            if (leadsTo is null || !(leadsTo == root || leadsTo.StartsWith(root + "/", StringComparison.Ordinal)))
            {
                Skip($"{name} (a link leading outside the app)");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.CreateSymbolicLink(path, target);
        }

        // The full path an entry is written to, or null (noted) when it is not acceptable.
        private string? Resolve(string name)
        {
            var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
            if (parts.Count == 0)
                return null;
            if (Path.IsPathRooted(name) || parts.Contains("..") || name.Contains('\0') || parts.Any(p => p.Length > 255))
                throw new InvalidDataException(
                    $"The archive tries to put a file outside its own folder ({Shorten(name)}). Real apps don't do that, so I stopped.");

            var path = root;
            for (var i = 0; i < parts.Count; i++)
            {
                path = Path.Combine(path, parts[i]);
                var info = new FileInfo(path);
                var isLast = i == parts.Count - 1;
                // Writing through a link, or over something already unpacked, is how hostile archives escape.
                if (info.LinkTarget is not null || (isLast && info.Exists) || (!isLast && info.Exists && !info.Attributes.HasFlag(FileAttributes.Directory)))
                {
                    if (isLast && Directory.Exists(path) && info.LinkTarget is null)
                        return path;
                    throw new InvalidDataException(
                        $"The archive writes the same place twice or through a link ({Shorten(name)}). Real apps don't do that, so I stopped.");
                }
            }
            return path;
        }

        private static string Shorten(string name) => name.Length > 80 ? name[..80] + "…" : name;
    }
}
