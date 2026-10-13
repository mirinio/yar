using System.Globalization;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>
/// The user's Trash as the desktop specification describes it: the thing itself under
/// <c>Trash/files</c> and a note beside it under <c>Trash/info</c> saying where it was. File managers
/// show and restore what is put here.
/// </summary>
public sealed class HomeTrash(string home, string dataHome, TimeProvider? time = null) : ITrash
{
    private readonly string _home = home.TrimEnd('/');
    private readonly string _files = Path.Combine(dataHome, "Trash", "files");
    private readonly string _info = Path.Combine(dataHome, "Trash", "info");

    public string? WhyNot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path.TrimEnd('/'))
            return "That is not a plain, full path.";
        if (!path.StartsWith(_home + "/", StringComparison.Ordinal))
            return "It is outside your home folder, and I only move your own things to the Trash.";
        if (path.StartsWith(Path.Combine(dataHome, "Trash") + "/", StringComparison.Ordinal))
            return "It is already in the Trash.";

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
            return "It is not there any more.";
        // Trashing a link would be harmless, but whoever asked meant the thing it points at.
        if (info.LinkTarget is not null)
            return "It is a link to something else, not the thing itself.";
        // A link further up could lead out of the home folder.
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length > _home.Length;
            parent = Path.GetDirectoryName(parent))
        {
            if (new DirectoryInfo(parent).LinkTarget is not null)
                return "It is reached through a link, so I can't be sure where it really is.";
        }
        return null;
    }

    public TrashedItem Move(string path)
    {
        path = path.TrimEnd('/');
        if (WhyNot(path) is { } reason)
            throw new InvalidOperationException(reason);

        try
        {
            Directory.CreateDirectory(_files, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(_info, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var (name, infoPath) = Reserve(Path.GetFileName(path), path);
            var trashPath = Path.Combine(_files, name);
            try
            {
                if (Directory.Exists(path))
                    Directory.Move(path, trashPath);
                else
                    File.Move(path, trashPath, overwrite: false);
            }
            catch
            {
                File.Delete(infoPath);
                throw;
            }
            return new(path, trashPath, infoPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"I couldn't move {path} to the Trash: {e.Message}", e);
        }
    }

    public bool Holds(TrashedItem item) =>
        IsMine(item) && File.Exists(item.InfoPath) && (File.Exists(item.TrashPath) || Directory.Exists(item.TrashPath));

    public void Restore(TrashedItem item)
    {
        if (!Holds(item))
            throw new InvalidOperationException("It is no longer in the Trash, so I can't put it back.");
        if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
            throw new InvalidOperationException($"Something else is at {item.OriginalPath} now, and I won't overwrite it.");

        try
        {
            if (Path.GetDirectoryName(item.OriginalPath) is { } parent)
                Directory.CreateDirectory(parent);
            if (Directory.Exists(item.TrashPath))
                Directory.Move(item.TrashPath, item.OriginalPath);
            else
                File.Move(item.TrashPath, item.OriginalPath, overwrite: false);
            File.Delete(item.InfoPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"I couldn't put {item.OriginalPath} back: {e.Message}", e);
        }
    }

    // A record could have been edited by hand; only ever touch what lies inside the Trash itself.
    private bool IsMine(TrashedItem item) =>
        Path.GetDirectoryName(item.TrashPath) == _files && Path.GetDirectoryName(item.InfoPath) == _info &&
        item.OriginalPath.StartsWith(_home + "/", StringComparison.Ordinal) &&
        Path.GetFullPath(item.OriginalPath) == item.OriginalPath;

    // Writes the note first, under a name nothing else has: creating it is what claims the name.
    private (string Name, string InfoPath) Reserve(string wanted, string originalPath)
    {
        var note = "[Trash Info]\n" +
            $"Path={string.Join('/', originalPath.Split('/').Select(Uri.EscapeDataString))}\n" +
            $"DeletionDate={(time ?? TimeProvider.System).GetLocalNow().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\n";
        for (var attempt = 1; attempt < 10_000; attempt++)
        {
            var name = attempt == 1 ? wanted : $"{wanted}.{attempt}";
            var infoPath = Path.Combine(_info, name + ".trashinfo");
            if (File.Exists(Path.Combine(_files, name)) || Directory.Exists(Path.Combine(_files, name)))
                continue;
            try
            {
                using var stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.Write(note);
                return (name, infoPath);
            }
            catch (IOException) when (File.Exists(infoPath))
            {
            }
        }
        throw new IOException("The Trash has too many things with this name.");
    }
}
