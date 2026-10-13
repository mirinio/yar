using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>The parts of an AppStream component that Yar shows or matches on.</summary>
public sealed record AppStreamComponent(
    string Id, string Name, string? Summary, string? PackageName, IReadOnlyList<string> DesktopIds);

/// <summary>
/// Reads AppStream XML: distribution catalogs (<c>&lt;components&gt;</c>) and the metainfo files
/// apps install (<c>&lt;component&gt;</c>). Only desktop applications are returned.
/// </summary>
public static class AppStreamXml
{
    public const long MaxFileBytes = 256L * 1024 * 1024;
    private const long MaxCharacters = 1024L * 1024 * 1024;

    /// <summary>Reads a plain or gzip-compressed file. Unreadable or broken files yield what was read so far.</summary>
    public static IReadOnlyList<AppStreamComponent> ReadFile(string path, CultureInfo? culture = null)
    {
        var components = new List<AppStreamComponent>();
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
                return components;

            using var file = File.OpenRead(path);
            using Stream stream = path.EndsWith(".gz", StringComparison.Ordinal)
                ? new GZipStream(file, CompressionMode.Decompress)
                : file;
            Read(stream, culture, components);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        {
            // Metadata is optional. A truncated or damaged catalog must not break the app list.
        }
        return components;
    }

    public static IReadOnlyList<AppStreamComponent> Read(Stream stream, CultureInfo? culture = null)
    {
        var components = new List<AppStreamComponent>();
        Read(stream, culture, components);
        return components;
    }

    private static void Read(Stream stream, CultureInfo? culture, List<AppStreamComponent> components)
    {
        var locales = LocaleFallback.Candidates(culture ?? CultureInfo.CurrentUICulture);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
        };

        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "component")
                continue;

            var type = reader.GetAttribute("type");
            if (type is "desktop-application" or "desktop")
            {
                using var subtree = reader.ReadSubtree();
                if (ReadComponent(subtree, locales) is { } component)
                    components.Add(component);
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static AppStreamComponent? ReadComponent(XmlReader reader, IReadOnlyList<string> locales)
    {
        string? id = null, packageName = null;
        var name = new Localized(locales);
        var summary = new Localized(locales);
        var desktopIds = new List<string>();

        reader.Read(); // <component>
        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF)
        {
            // Only direct children; names nested in <description> or <releases> are something else.
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1)
            {
                reader.Read();
                continue;
            }

            switch (reader.Name)
            {
                case "id":
                    id = reader.ReadElementContentAsString().Trim();
                    break;
                case "pkgname":
                    packageName ??= reader.ReadElementContentAsString().Trim();
                    break;
                case "name":
                    name.Offer(reader.GetAttribute("xml:lang"), reader.ReadElementContentAsString().Trim());
                    break;
                case "summary":
                    summary.Offer(reader.GetAttribute("xml:lang"), reader.ReadElementContentAsString().Trim());
                    break;
                case "launchable":
                    var isDesktopId = reader.GetAttribute("type") == "desktop-id";
                    var value = reader.ReadElementContentAsString().Trim();
                    if (isDesktopId && value.Length > 0)
                        desktopIds.Add(value);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (string.IsNullOrEmpty(id) || name.Value is not { Length: > 0 } componentName)
            return null;

        // Older catalogs have no <launchable>; there the component ID is the desktop ID.
        if (desktopIds.Count == 0)
            desktopIds.Add(id.EndsWith(".desktop", StringComparison.Ordinal) ? id : id + ".desktop");

        return new(id, componentName, summary.Value, string.IsNullOrEmpty(packageName) ? null : packageName, desktopIds);
    }

    /// <summary>Keeps the best translation seen so far for the wanted locales.</summary>
    private sealed class Localized(IReadOnlyList<string> locales)
    {
        private int _rank = int.MaxValue;

        public string? Value { get; private set; }

        public void Offer(string? language, string text)
        {
            if (text.Length == 0)
                return;

            int rank;
            if (string.IsNullOrEmpty(language) || language == "C")
            {
                rank = locales.Count; // The untranslated text: better than nothing, worse than any match.
            }
            else
            {
                // AppStream uses both "pt_BR" and "pt-BR".
                var normalized = language.Replace('-', '_');
                rank = -1;
                for (var i = 0; i < locales.Count; i++)
                {
                    if (string.Equals(locales[i], normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        rank = i;
                        break;
                    }
                }
                if (rank < 0)
                    return;
            }

            if (rank < _rank)
            {
                _rank = rank;
                Value = text;
            }
        }
    }
}

/// <summary>Finds components by the launcher they describe.</summary>
public sealed class ComponentIndex
{
    private readonly Dictionary<string, List<AppStreamComponent>> _byDesktopId = new(StringComparer.Ordinal);

    public int Count { get; private set; }

    public void Add(IEnumerable<AppStreamComponent> components)
    {
        foreach (var component in components)
        {
            Count++;
            foreach (var desktopId in component.DesktopIds)
            {
                if (!_byDesktopId.TryGetValue(desktopId, out var list))
                    _byDesktopId[desktopId] = list = [];
                list.Add(component);
            }
        }
    }

    /// <summary>
    /// The component for a launcher. When several sources describe the same desktop ID,
    /// the one for <paramref name="packageName"/> wins.
    /// </summary>
    public AppStreamComponent? Find(string desktopId, string? packageName = null)
    {
        if (!_byDesktopId.TryGetValue(desktopId, out var candidates))
            return null;
        return candidates.FirstOrDefault(c => packageName is not null && c.PackageName == packageName) ?? candidates[0];
    }

    /// <summary>
    /// Loads every AppStream file found in the usual places. With a <paramref name="cache"/>, files that
    /// have not changed since last time are taken from it instead of being parsed again.
    /// </summary>
    public static ComponentIndex Load(XdgDirs dirs, CultureInfo? culture = null, ICacheStore? cache = null) =>
        Load(AppStreamFiles(dirs), culture ?? CultureInfo.CurrentUICulture, cache, ReadAny);

    // XML on most distributions, YAML on Debian and Ubuntu; the index does not care which.
    private static IReadOnlyList<AppStreamComponent> ReadAny(string path, CultureInfo? culture) =>
        StoreCatalogReader.IsYaml(path)
            ? Dep11Yaml.ReadFile(path, "packagekit", iconRoot: "", culture)
                .Select(entry => new AppStreamComponent(
                    entry.ComponentId, entry.Name, entry.Summary, entry.PackageName,
                    entry.OtherIds.Count > 0
                        ? entry.OtherIds
                        : [entry.ComponentId.EndsWith(".desktop", StringComparison.Ordinal) ? entry.ComponentId : entry.ComponentId + ".desktop"]))
                .ToList()
            : AppStreamXml.ReadFile(path, culture);

    public static ComponentIndex Load(
        IReadOnlyList<string> files, CultureInfo culture, ICacheStore? cache,
        Func<string, CultureInfo?, IReadOnlyList<AppStreamComponent>> readFile)
    {
        // Names are picked by language, so each language has its own saved index.
        var entryName = $"appstream-{(culture.Name.Length == 0 ? "c" : culture.Name)}.json";
        var remembered = ReadCache(cache, entryName);
        var current = new Dictionary<string, CachedFile>(StringComparer.Ordinal);
        var index = new ComponentIndex();
        var changed = remembered.Count != files.Count;

        foreach (var file in files)
        {
            var stamp = StampOf(file);
            if (stamp is null)
            {
                changed = true;
                continue;
            }

            if (!remembered.TryGetValue(file, out var known) || known.ModifiedTicks != stamp.Value.Ticks ||
                known.Length != stamp.Value.Length)
            {
                known = new(stamp.Value.Ticks, stamp.Value.Length, readFile(file, culture));
                changed = true;
            }
            current[file] = known;
            index.Add(known.Components);
        }

        if (changed && cache is not null)
            cache.Write(entryName, JsonSerializer.Serialize(new CacheEnvelope(CacheFormatVersion, current), CacheOptions));
        return index;
    }

    private const int CacheFormatVersion = 1;

    private static readonly JsonSerializerOptions CacheOptions = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private static Dictionary<string, CachedFile> ReadCache(ICacheStore? cache, string entryName)
    {
        try
        {
            if (cache?.Read(entryName) is { } json &&
                JsonSerializer.Deserialize<CacheEnvelope>(json, CacheOptions) is { FormatVersion: CacheFormatVersion } envelope)
            {
                return envelope.Files;
            }
        }
        catch (JsonException)
        {
            // A damaged file only means parsing the catalogs again.
        }
        return [];
    }

    private static (long Ticks, long Length)? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record CachedFile(long ModifiedTicks, long Length, IReadOnlyList<AppStreamComponent> Components);

    private sealed record CacheEnvelope(int FormatVersion, Dictionary<string, CachedFile> Files);

    /// <summary>Catalogs first, then metainfo of installed apps for anything the catalogs lack.</summary>
    public static IReadOnlyList<string> AppStreamFiles(XdgDirs dirs)
    {
        string[] catalogDirs =
        [
            "/usr/share/swcatalog/xml", "/var/lib/swcatalog/xml", "/var/cache/swcatalog/xml",
            "/usr/share/app-info/xmls", "/var/lib/app-info/xmls", "/var/cache/app-info/xmls",
        ];
        var metainfoDirs = dirs.DataDirs.Prepend(dirs.DataHome).Distinct(StringComparer.Ordinal)
            .SelectMany(d => new[] { Path.Combine(d, "metainfo"), Path.Combine(d, "appdata") });

        string[] yamlDirs =
        [
            "/usr/share/swcatalog/yaml", "/var/lib/swcatalog/yaml", "/var/cache/swcatalog/yaml",
            "/usr/share/app-info/yaml", "/var/lib/app-info/yaml", "/var/cache/app-info/yaml",
        ];

        return catalogDirs.SelectMany(d => Files(d, "*.xml*"))
            .Concat(metainfoDirs.SelectMany(d => Files(d, "*.xml")))
            .Where(f => f.EndsWith(".xml", StringComparison.Ordinal) || f.EndsWith(".xml.gz", StringComparison.Ordinal))
            .Concat(yamlDirs.SelectMany(d => StoreCatalogReader.Files(d, yaml: true)))
            .ToList();
    }

    private static List<string> Files(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern).Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
