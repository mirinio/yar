using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>
/// Reads app catalogs in DEP-11 form: the YAML that Debian and Ubuntu use for the same
/// information other distributions ship as AppStream XML.
/// </summary>
public static partial class Dep11Yaml
{
    private const int MaxDescriptionChars = 4000;
    private const int MaxDocuments = 200_000;

    /// <summary>
    /// Reads a plain or gzip-compressed catalog. A damaged file yields what was read before the damage.
    /// </summary>
    /// <param name="iconRoot">The folder that holds one sub-folder of icons per catalog origin.</param>
    public static IReadOnlyList<CatalogEntry> ReadFile(string path, string providerId, string iconRoot, CultureInfo? culture = null)
    {
        var entries = new List<CatalogEntry>();
        try
        {
            if (new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
                path = target.FullName;
            if (new FileInfo(path).Length > AppStreamXml.MaxFileBytes)
                return entries;

            using var file = File.OpenRead(path);
            using Stream content = path.EndsWith(".gz", StringComparison.Ordinal)
                ? new GZipStream(file, CompressionMode.Decompress)
                : file;
            using var reader = new StreamReader(content, Encoding.UTF8);
            Read(reader, providerId, iconRoot, culture, entries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or YamlException)
        {
            // Metadata is optional. A truncated or damaged catalog must not break the app list.
        }
        return entries;
    }

    public static IReadOnlyList<CatalogEntry> Read(TextReader reader, string providerId, string iconRoot, CultureInfo? culture = null)
    {
        var entries = new List<CatalogEntry>();
        Read(reader, providerId, iconRoot, culture, entries);
        return entries;
    }

    private static void Read(
        TextReader reader, string providerId, string iconRoot, CultureInfo? culture, List<CatalogEntry> entries)
    {
        var locales = LocaleFallback.Candidates(culture ?? CultureInfo.CurrentUICulture);
        var deserializer = new DeserializerBuilder().Build();
        var parser = new Parser(reader);
        parser.Consume<StreamStart>();

        string? origin = null;
        string? mediaBase = null;
        var documents = 0;
        while (parser.Accept<DocumentStart>(out _) && documents++ < MaxDocuments)
        {
            // Documents are read one at a time, so a large catalog is never held in memory whole.
            if (deserializer.Deserialize<Dictionary<object, object>>(parser) is not { } document)
                continue;

            if (document.ContainsKey("File"))
            {
                origin = Text(document, "Origin");
                mediaBase = Text(document, "MediaBaseUrl");
            }
            else if (origin is not null && ToEntry(document, providerId, origin, mediaBase, iconRoot, locales) is { } entry)
            {
                entries.Add(entry);
            }
        }
    }

    private static CatalogEntry? ToEntry(
        Dictionary<object, object> document, string providerId, string origin, string? mediaBase, string iconRoot,
        IReadOnlyList<string> locales)
    {
        if (Text(document, "Type") != "desktop-application")
            return null;
        var id = Text(document, "ID");
        var name = Localized(document, "Name", locales);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            return null;

        var otherIds = Strings(Map(document, "Launchable"), "desktop-id")
            .Concat(Strings(Map(document, "Provides"), "ids"))
            .Distinct(StringComparer.Ordinal).ToList();

        var (iconPath, iconName) = Icon(Map(document, "Icon"), iconRoot, origin);
        return new CatalogEntry(
            ProviderId: providerId,
            SourceId: origin,
            ComponentId: id,
            Name: name,
            Summary: Localized(document, "Summary", locales),
            Description: PlainText(Localized(document, "Description", locales)),
            Developer: Localized(Map(document, "Developer"), "name", locales) ?? Localized(document, "DeveloperName", locales),
            License: Text(document, "ProjectLicense"),
            Homepage: Text(Map(document, "Url"), "homepage"),
            IconPath: iconPath,
            IconName: iconName,
            DesktopCategories: Strings(document, "Categories"),
            OtherIds: otherIds,
            PackageName: Text(document, "Package"),
            Version: List(document, "Releases").OfType<Dictionary<object, object>>().Select(r => Text(r, "version"))
                .FirstOrDefault(v => v is not null),
            MakerVerified: false,
            VerifiedBy: null)
        {
            Screenshots = Screenshots(document, mediaBase),
        };
    }

    private static (string? Path, string? Name) Icon(Dictionary<object, object>? icon, string iconRoot, string origin)
    {
        if (icon is null)
            return (null, null);

        string? best = null;
        var bestWidth = 0;
        foreach (var cached in List(icon, "cached").OfType<Dictionary<object, object>>())
        {
            var name = Text(cached, "name");
            // A catalog must not be able to point at files outside its own icon folder.
            if (name is null || name.IndexOfAny(['/', '\\']) >= 0 || name.StartsWith('.') || cached.ContainsKey("scale"))
                continue;
            var width = Number(cached, "width") ?? 64;
            if (best is not null && Math.Abs(width - 64) >= Math.Abs(bestWidth - 64))
                continue;
            var path = System.IO.Path.Combine(iconRoot, origin, $"{width}x{width}", name);
            if (File.Exists(path))
            {
                best = path;
                bestWidth = width;
            }
        }
        return (best, Text(icon, "stock"));
    }

    private static List<string> Screenshots(Dictionary<object, object> document, string? mediaBase) =>
        List(document, "Screenshots").OfType<Dictionary<object, object>>()
            .OrderBy(shot => Text(shot, "default") == "true" ? 0 : 1)
            .Select(shot => ScreenshotUrls.Choose(
                List(shot, "thumbnails").OfType<Dictionary<object, object>>()
                    .Select(image => (Absolute(Text(image, "url"), mediaBase), (string?)"thumbnail", Number(image, "width")))
                    .Concat(Map(shot, "source-image") is { } source
                        ? [(Absolute(Text(source, "url"), mediaBase), "source", Number(source, "width"))]
                        : [])))
            .Where(url => url is not null)
            .Select(url => url!)
            .Distinct(StringComparer.Ordinal)
            .Take(ScreenshotUrls.MaxPerApp)
            .ToList();

    // Picture addresses in these catalogs are relative to the catalog's media server.
    private static string Absolute(string? url, string? mediaBase) =>
        url is null ? ""
        : url.Contains("://", StringComparison.Ordinal) || mediaBase is null ? url
        : mediaBase.TrimEnd('/') + "/" + url.TrimStart('/');

    private static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var text = ListItem().Replace(html, "• ");
        text = BlockEnd().Replace(text, "\n\n");
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => string.Join(' ', p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(p => p.Length > 0)
            .ToList();
        // List items belong together; paragraphs get an empty line between them.
        var builder = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            if (builder.Length > 0)
                builder.Append(paragraph.StartsWith('•') && builder.ToString().Split('\n')[^1].StartsWith('•') ? "\n" : "\n\n");
            builder.Append(paragraph);
        }
        var result = builder.ToString();
        if (result.Length > MaxDescriptionChars)
            result = result[..MaxDescriptionChars].TrimEnd() + "…";
        return result.Length == 0 ? null : result;
    }

    private static string? Localized(Dictionary<object, object>? parent, string key, IReadOnlyList<string> locales)
    {
        if (Map(parent, key) is not { } translations)
            return Text(parent, key);

        foreach (var locale in locales)
        {
            // These catalogs write "pt-BR" where desktop files write "pt_BR".
            foreach (var (language, value) in translations)
            {
                if (language is string name && value is string text && text.Trim().Length > 0 &&
                    string.Equals(name.Replace('-', '_'), locale, StringComparison.OrdinalIgnoreCase))
                {
                    return text.Trim();
                }
            }
        }
        return Text(translations, "C");
    }

    private static string? Text(Dictionary<object, object>? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) && value is string text && text.Trim().Length > 0
            ? text.Trim()
            : null;

    private static int? Number(Dictionary<object, object> map, string key) =>
        int.TryParse(Text(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static Dictionary<object, object>? Map(Dictionary<object, object>? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) ? value as Dictionary<object, object> : null;

    private static List<object> List(Dictionary<object, object>? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) && value is List<object> list ? list : [];

    private static List<string> Strings(Dictionary<object, object>? map, string key) =>
        List(map, key).OfType<string>().Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"</(p|li|ul|ol)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]{0,200}>")]
    private static partial Regex AnyTag();
}
