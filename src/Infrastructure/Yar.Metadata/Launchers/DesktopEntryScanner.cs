using System.Globalization;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>Builds the list of launchers a user sees in their app menu.</summary>
public sealed class DesktopEntryScanner
{
    private const int MaxDepth = 4;

    private readonly IReadOnlyList<string> _applicationDirs;
    private readonly CultureInfo? _culture;

    public DesktopEntryScanner(XdgDirs dirs, CultureInfo? culture = null)
        : this(dirs.ApplicationDirs, culture)
    {
    }

    public DesktopEntryScanner(IReadOnlyList<string> applicationDirs, CultureInfo? culture = null)
    {
        _applicationDirs = applicationDirs;
        _culture = culture;
    }

    public DesktopEntryIndex Scan()
    {
        var entries = new List<DesktopEntry>();
        // A desktop ID found in an earlier directory hides the same ID in later ones,
        // even when the earlier file is hidden. That is how users hide system launchers.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var directory in _applicationDirs)
        {
            foreach (var path in EnumerateDesktopFiles(directory, depth: 0))
            {
                var desktopId = Path.GetRelativePath(directory, path).Replace('/', '-');
                if (!seenIds.Add(desktopId))
                    continue;

                var entry = DesktopEntryParser.ParseFile(path, desktopId, _culture);
                if (entry is { IsVisibleApplication: true })
                    entries.Add(entry);
            }
        }

        return new DesktopEntryIndex(entries);
    }

    private static IEnumerable<string> EnumerateDesktopFiles(string directory, int depth)
    {
        IEnumerable<string> files;
        IEnumerable<string> subdirectories;
        try
        {
            if (!Directory.Exists(directory))
                yield break;
            files = Directory.EnumerateFiles(directory, "*.desktop").Order(StringComparer.Ordinal).ToList();
            subdirectories = depth < MaxDepth
                ? Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
            yield return file;
        foreach (var subdirectory in subdirectories)
        {
            foreach (var file in EnumerateDesktopFiles(subdirectory, depth + 1))
                yield return file;
        }
    }
}
