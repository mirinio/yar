using System.Globalization;

namespace Yar.Core;

/// <summary>Decides which pictures of an app Yar is willing to download.</summary>
public static class ScreenshotUrls
{
    public const int MaxPerApp = 8;

    // A catalog can name any server for an app's pictures. Opening an app's page should not make
    // this computer contact servers nobody has heard of, so only the catalogs' own hosts count.
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "dl.flathub.org",
        "dl.fedoraproject.org",
        "appstream.ubuntu.com",
        "appstream.debian.org",
    };

    private const int WantedWidth = 752;

    /// <summary>
    /// From the versions of one screenshot, picks the one closest to the size Yar shows, on an
    /// accepted host, and returns its https address. Null if none qualifies.
    /// </summary>
    public static string? Choose(IEnumerable<(string Url, string? Type, int? Width)> versions) =>
        versions
            .Select(version => (Url: Secure(version.Url), version.Type, version.Width))
            .Where(version => version.Url is not null)
            // A thumbnail near the wanted width; the full-size original only as a last resort.
            .OrderBy(version => version.Type == "thumbnail" ? 0 : 1)
            .ThenBy(version => Math.Abs((version.Width ?? int.MaxValue / 2) - WantedWidth))
            .Select(version => version.Url)
            .FirstOrDefault();

    public static bool IsAllowed(string? url) => Secure(url) == url && url is not null;

    /// <summary>The address as https if it is on an accepted host; otherwise null.</summary>
    public static string? Secure(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;
        if (!AllowedHosts.Contains(uri.Host) || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
            return null;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
    }
}

/// <summary>Writes big counts the way people say them: 34.8k, 1.8M.</summary>
public static class CompactNumber
{
    public static string Format(long value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return value switch
        {
            < 1_000 => value.ToString(culture),
            < 10_000 => (value / 1_000.0).ToString("0.#", culture) + "k",
            < 1_000_000 => (value / 1_000.0).ToString("0", culture) + "k",
            < 10_000_000 => (value / 1_000_000.0).ToString("0.#", culture) + "M",
            _ => (value / 1_000_000.0).ToString("0", culture) + "M",
        };
    }
}

/// <summary>The programming languages a developer may want to follow, and a guess at which ones they use.</summary>
public static class DevLanguages
{
    /// <summary>Display name and the name the project search understands.</summary>
    public static IReadOnlyList<(string Name, string Query)> All { get; } =
    [
        ("C#", "csharp"), ("C++", "cpp"), ("Go", "go"), ("Java", "java"), ("JavaScript", "javascript"),
        ("Kotlin", "kotlin"), ("PHP", "php"), ("Python", "python"), ("Ruby", "ruby"), ("Rust", "rust"),
        ("Swift", "swift"), ("TypeScript", "typescript"), ("Zig", "zig"),
    ];

    // Tools that say something about their owner. Python and the C compiler are on almost every
    // Linux system and would tell nothing, so they are not here.
    private static readonly (string Tool, string[] Languages)[] Tools =
    [
        ("dotnet", ["C#"]), ("cargo", ["Rust"]), ("go", ["Go"]), ("node", ["JavaScript", "TypeScript"]),
        ("javac", ["Java"]), ("kotlinc", ["Kotlin"]), ("php", ["PHP"]), ("ruby", ["Ruby"]),
        ("swift", ["Swift"]), ("zig", ["Zig"]),
    ];

    /// <summary>Languages whose tools are found in the given directories.</summary>
    public static IReadOnlyList<string> Detect(IEnumerable<string> searchPath)
    {
        var directories = searchPath.Where(Path.IsPathFullyQualified).ToList();
        return Tools
            .Where(tool => directories.Any(directory => File.Exists(Path.Combine(directory, tool.Tool))))
            .SelectMany(tool => tool.Languages)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public static string? QueryFor(string name) =>
        All.FirstOrDefault(language => language.Name == name) is { Query: { } query } ? query : null;
}
