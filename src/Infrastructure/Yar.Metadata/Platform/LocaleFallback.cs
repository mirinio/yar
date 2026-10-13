using System.Globalization;

namespace Yar.Metadata;

/// <summary>Turns a .NET culture into the locale names freedesktop.org files use.</summary>
public static class LocaleFallback
{
    /// <summary>The lookup order for a locale such as <c>de_AT@euro</c>, most specific first.</summary>
    public static IReadOnlyList<string> Candidates(CultureInfo culture)
    {
        // .NET names look like "de-AT" or "sr-Latn-RS"; desktop files use "de_AT" and "sr@latin".
        var parts = culture.Name.Split('-');
        if (parts.Length == 0 || parts[0].Length == 0)
            return [];

        var language = parts[0];
        string? country = null;
        string? modifier = null;
        foreach (var part in parts.Skip(1))
        {
            if (part.Length == 4)
                modifier = part switch { "Latn" => "latin", "Cyrl" => "cyrillic", _ => part.ToLowerInvariant() };
            else if (country is null)
                country = part.ToUpperInvariant();
        }

        var candidates = new List<string>(4);
        if (country is not null && modifier is not null)
            candidates.Add($"{language}_{country}@{modifier}");
        if (country is not null)
            candidates.Add($"{language}_{country}");
        if (modifier is not null)
            candidates.Add($"{language}@{modifier}");
        candidates.Add(language);
        return candidates;
    }
}
