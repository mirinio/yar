namespace Yar.Backends.Flatpak;

/// <summary>Adds up the size of a folder without letting a huge or odd one hold things up.</summary>
internal static class FolderSize
{
    public const int MaxFiles = 50_000;
    private static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(2);

    /// <summary>The total size in bytes, or null if the folder has too many files to count quickly.</summary>
    public static long? Measure(string directory, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + MaxTime;
        long total = 0;
        var files = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Links are counted as links, never followed out of the folder.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (++files > MaxFiles || DateTime.UtcNow > deadline)
                    return null;
                total += file.Length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return total;
    }
}
