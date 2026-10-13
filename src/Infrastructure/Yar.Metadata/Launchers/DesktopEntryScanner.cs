using System.Globalization;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>Builds the list of launchers a user sees in their app menu.</summary>
public sealed class DesktopEntryScanner
{
    private const int MaxDepth = 4;

    private readonly IReadOnlyList<string> _applicationDirs;
    private readonly CultureInfo? _culture;
    private readonly IReadOnlyList<string>? _desktops;
    private readonly IReadOnlyList<string>? _searchPath;

    /// <param name="currentDesktops">
    /// The desktop in use, as <c>XDG_CURRENT_DESKTOP</c> lists it ("KDE", "ubuntu:GNOME"). Launchers
    /// meant for other desktops are then left out, as the app menu does. Null keeps them all.
    /// </param>
    /// <param name="searchPath">Where programs are looked for, to honour a launcher's <c>TryExec</c>. Null skips that check.</param>
    public DesktopEntryScanner(
        XdgDirs dirs, CultureInfo? culture = null, IReadOnlyList<string>? currentDesktops = null,
        IReadOnlyList<string>? searchPath = null)
        : this(dirs.ApplicationDirs, culture, currentDesktops, searchPath)
    {
    }

    public DesktopEntryScanner(
        IReadOnlyList<string> applicationDirs, CultureInfo? culture = null,
        IReadOnlyList<string>? currentDesktops = null, IReadOnlyList<string>? searchPath = null)
    {
        _applicationDirs = applicationDirs;
        _culture = culture;
        _desktops = currentDesktops;
        _searchPath = searchPath;
    }

    /// <summary>The rules by which a desktop leaves a launcher out of its menu, beyond <c>NoDisplay</c> and <c>Hidden</c>.</summary>
    public static bool IsShownOn(DesktopEntry entry, IReadOnlyList<string>? desktops, IReadOnlyList<string>? searchPath)
    {
        static string[] List(string? value) =>
            value?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        if (desktops is not null)
        {
            var only = List(entry.GetKey("OnlyShowIn"));
            if (only.Length > 0 && !only.Any(desktops.Contains))
                return false;
            if (List(entry.GetKey("NotShowIn")).Any(desktops.Contains))
                return false;
        }

        // "TryExec" names a program whose absence means the app is not really installed.
        return searchPath is null || string.IsNullOrWhiteSpace(entry.TryExec) ||
            DesktopExec.FindProgram(entry.TryExec.Trim(), searchPath) is not null;
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
                if (entry is { IsVisibleApplication: true } && IsShownOn(entry, _desktops, _searchPath))
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
