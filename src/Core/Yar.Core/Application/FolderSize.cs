namespace Yar.Core;

/// <summary>Adds up the size of a folder without letting a huge or odd one hold things up.</summary>
public static class FolderSize
{
    public const int MaxFiles = 50_000;
    private static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(2);

    /// <summary>The total size in bytes, or null if the folder has too many files to count quickly.</summary>
    public static long? Measure(string directory, CancellationToken ct) =>
        MeasureUpTo(directory, MaxFiles, MaxTime, ct) is { Complete: true } result ? result.Bytes : null;

    /// <summary>
    /// Counts until the folder is done or a limit is reached. When <c>Complete</c> is false, the
    /// folder holds at least <c>Bytes</c>.
    /// </summary>
    public static (long Bytes, bool Complete) MeasureUpTo(string directory, int maxFiles, TimeSpan maxTime, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + maxTime;
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
                if (++files > maxFiles || DateTime.UtcNow > deadline)
                    return (total, false);
                total += file.Length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (total, false);
        }
        return (total, true);
    }
}
