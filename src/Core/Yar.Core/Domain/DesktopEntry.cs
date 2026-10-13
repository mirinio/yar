namespace Yar.Core;

/// <summary>The parts of a <c>.desktop</c> launcher file that Yar uses.</summary>
public sealed record DesktopEntry(
    string Path,
    string DesktopId,
    string Type,
    string Name,
    string? GenericName,
    string? Comment,
    string? Icon,
    string? Exec,
    string? TryExec,
    bool NoDisplay,
    bool Hidden,
    IReadOnlyList<string> Categories,
    IReadOnlyDictionary<string, string> Keys)
{
    /// <summary>True for entries a user would see in their app menu.</summary>
    public bool IsVisibleApplication => Type == "Application" && !NoDisplay && !Hidden;

    public string? GetKey(string key) => Keys.TryGetValue(key, out var value) ? value : null;
}

/// <summary>The launchers that no provider has claimed yet.</summary>
public sealed class DesktopEntryIndex
{
    public static DesktopEntryIndex Empty { get; } = new([]);

    public DesktopEntryIndex(IReadOnlyList<DesktopEntry> entries) => Entries = entries;

    public IReadOnlyList<DesktopEntry> Entries { get; }

    public DesktopEntryIndex Without(IReadOnlySet<string> paths) =>
        paths.Count == 0 ? this : new(Entries.Where(e => !paths.Contains(e.Path)).ToList());
}
