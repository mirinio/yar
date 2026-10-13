namespace Yar.Core;

/// <summary>Maps the freedesktop.org menu categories of a launcher to the tags Yar shows.</summary>
public static class AppCategories
{
    private static readonly Dictionary<string, AppCategory> Known = new(StringComparer.Ordinal)
    {
        ["Game"] = AppCategory.Games,
        ["Network"] = AppCategory.Internet,
        ["WebBrowser"] = AppCategory.Internet,
        ["Email"] = AppCategory.Internet,
        ["Chat"] = AppCategory.Internet,
        ["InstantMessaging"] = AppCategory.Internet,
        ["Office"] = AppCategory.Office,
        ["Graphics"] = AppCategory.Graphics,
        ["AudioVideo"] = AppCategory.SoundAndVideo,
        ["Audio"] = AppCategory.SoundAndVideo,
        ["Video"] = AppCategory.SoundAndVideo,
        ["Development"] = AppCategory.Development,
        ["Education"] = AppCategory.Education,
        ["Science"] = AppCategory.Science,
        ["Utility"] = AppCategory.Utilities,
        ["System"] = AppCategory.System,
        ["Settings"] = AppCategory.Settings,
    };

    /// <summary>
    /// Returns the tags in the order the launcher lists them, without repeats. Toolkit markers
    /// (Qt, GTK, KDE) and vendor extensions (X-...) say nothing about what an app is for and are
    /// ignored. An app with no usable category is <see cref="AppCategory.Other"/>.
    /// </summary>
    public static IReadOnlyList<AppCategory> FromDesktopCategories(IReadOnlyList<string> desktopCategories)
    {
        var categories = new List<AppCategory>();
        foreach (var name in desktopCategories)
        {
            if (Known.TryGetValue(name, out var category) && !categories.Contains(category))
                categories.Add(category);
        }
        if (categories.Count == 0)
            categories.Add(AppCategory.Other);
        return categories;
    }
}
