using Yar.Core;

namespace Yar.Persistence;

/// <summary>Keeps cache entries as files in one directory, usually <c>$XDG_CACHE_HOME/yar</c>.</summary>
public sealed class FileCacheStore(string directory) : ICacheStore
{
    public const long MaxEntryBytes = 32 * 1024 * 1024;

    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = OwnerOnlyFile | UnixFileMode.UserExecute;

    public string? Read(string name)
    {
        try
        {
            var info = new FileInfo(PathOf(name));
            return info.Exists && info.Length <= MaxEntryBytes ? File.ReadAllText(info.FullName) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string name, string content)
    {
        var path = PathOf(name);
        // What could not be read back is not worth writing.
        if (content.Length > MaxEntryBytes)
            return;
        try
        {
            Directory.CreateDirectory(directory, OwnerOnlyDirectory);
            // Written beside the entry and renamed over it, so a reader never sees half a file.
            var temporary = $"{path}.{Environment.ProcessId}.tmp";
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = OwnerOnlyFile,
            }))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written only costs time on the next start.
        }
    }

    // Entry names are chosen by Yar's own code; this keeps a mistake from ever leaving the directory.
    private string PathOf(string name)
    {
        if (name.Length == 0 || name.StartsWith('.') || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            throw new ArgumentException("Not a valid cache entry name.", nameof(name));
        return Path.Combine(directory, name);
    }
}
