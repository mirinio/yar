namespace Yar.Core;

public enum SearchMatchKind { None, Word, Tag }

/// <summary>How an app matched a search: by a word in its name or summary, or by one of its category tags.</summary>
public readonly record struct SearchMatch(SearchMatchKind Kind, AppCategory? Category = null)
{
    public static SearchMatch None { get; } = new(SearchMatchKind.None);
}

public static class AppSearch
{
    /// <summary>
    /// Matches <paramref name="query"/> against an app. A hit in the name or summary wins over a
    /// tag hit, so an app is never reported twice. <paramref name="categoryName"/> gives the tag
    /// text as the user sees it, which is what they will type.
    /// </summary>
    public static SearchMatch Match(AppInfo app, string query, Func<AppCategory, string> categoryName)
    {
        query = query.Trim();
        if (query.Length == 0)
            return SearchMatch.None;

        if (Contains(app.Name, query) || (app.Summary is not null && Contains(app.Summary, query)))
            return new(SearchMatchKind.Word);

        foreach (var category in app.Categories)
        {
            // "Other" is the absence of a tag, so it is not something to search for.
            if (category != AppCategory.Other && MatchesTag(categoryName(category), query))
                return new(SearchMatchKind.Tag, category);
        }
        return SearchMatch.None;
    }

    // "game" finds "Games" and "games" finds "Game"; a single letter is too little to mean a tag.
    private static bool MatchesTag(string tag, string query) =>
        query.Length >= 2 && (Contains(tag, query) || (query.Length >= 3 && Contains(query, tag)));

    private static bool Contains(string text, string part) =>
        text.Contains(part, StringComparison.CurrentCultureIgnoreCase);
}
