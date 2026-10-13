using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>
/// Reads the app catalogs that package managers keep on this computer: the distribution's
/// AppStream catalog and the one of every Flatpak remote. Nothing is downloaded.
/// </summary>
public sealed class StoreCatalogReader(XdgDirs dirs, ICacheStore? cache = null, CultureInfo? culture = null)
    : IStoreCatalogReader
{
    private const int MaxDescriptionChars = 4000;
    private const int CacheFormatVersion = 3;

    private static readonly JsonSerializerOptions CacheOptions = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private readonly CultureInfo _culture = culture ?? CultureInfo.CurrentUICulture;

    /// <summary>One catalog file and what its entries should be attributed to.</summary>
    public sealed record CatalogFile(string Path, string ProviderId, string? SourceId, string IconRoot);

    public Task<IReadOnlyList<CatalogEntry>> ReadAsync(CancellationToken ct) =>
        Task.Run(() => Read(FindCatalogs(), ct), ct);

    public IReadOnlyList<CatalogEntry> Read(IReadOnlyList<CatalogFile> files, CancellationToken ct = default)
    {
        var entryName = $"store-{(_culture.Name.Length == 0 ? "c" : _culture.Name)}.json";
        var remembered = ReadCache(entryName);
        var current = new Dictionary<string, CachedFile>(StringComparer.Ordinal);
        var all = new List<CatalogEntry>();
        var changed = remembered.Count != files.Count;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            FileInfo info;
            try
            {
                info = new FileInfo(file.Path);
                if (!info.Exists || info.Length > AppStreamXml.MaxFileBytes)
                    continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var stamp = (info.LastWriteTimeUtc.Ticks, info.Length);
            if (!remembered.TryGetValue(file.Path, out var known) || (known.ModifiedTicks, known.Length) != stamp)
            {
                known = new(stamp.Ticks, stamp.Length, ReadFile(file));
                changed = true;
            }
            current[file.Path] = known;
            all.AddRange(known.Entries);
        }

        if (changed)
            cache?.Write(entryName, JsonSerializer.Serialize(new CacheEnvelope(CacheFormatVersion, current), CacheOptions));
        return all;
    }

    /// <summary>The distribution's catalogs and those of every Flatpak remote, system-wide and per-user.</summary>
    public IReadOnlyList<CatalogFile> FindCatalogs()
    {
        var files = new List<CatalogFile>();

        string[] roots = ["/usr/share/swcatalog", "/var/lib/swcatalog", "/var/cache/swcatalog",
            "/usr/share/app-info", "/var/lib/app-info", "/var/cache/app-info"];
        foreach (var root in roots)
        {
            var xmlDir = Path.Combine(root, root.EndsWith("app-info", StringComparison.Ordinal) ? "xmls" : "xml");
            foreach (var path in Files(xmlDir))
                files.Add(new(path, "packagekit", SourceId: null, Path.Combine(root, "icons")));
        }

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            var other => other.ToString().ToLowerInvariant(),
        };
        foreach (var (installation, directory) in new[]
        {
            ("system", "/var/lib/flatpak"), ("user", Path.Combine(dirs.DataHome, "flatpak")),
        })
        {
            foreach (var remoteDir in Directories(Path.Combine(directory, "appstream")))
            {
                var active = Path.Combine(remoteDir, arch, "active");
                var catalog = Path.Combine(active, "appstream.xml.gz");
                if (File.Exists(catalog))
                {
                    files.Add(new(catalog, "flatpak", $"{installation}:{Path.GetFileName(remoteDir)}",
                        Path.Combine(active, "icons")));
                }
            }
        }
        return files;
    }

    private List<CatalogEntry> ReadFile(CatalogFile file)
    {
        var entries = new List<CatalogEntry>();
        var locales = LocaleFallback.Candidates(_culture);
        try
        {
            using var stream = File.OpenRead(file.Path);
            using Stream content = file.Path.EndsWith(".gz", StringComparison.Ordinal)
                ? new GZipStream(stream, CompressionMode.Decompress)
                : stream;
            using var reader = XmlReader.Create(content, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1024L * 1024 * 1024,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
            });

            string? origin = null;
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }
                if (reader.Name == "components")
                {
                    origin = reader.GetAttribute("origin");
                    reader.Read();
                }
                else if (reader.Name == "component" && reader.GetAttribute("type") is "desktop-application" or "desktop")
                {
                    // ReadFrom leaves the reader on the node after the component.
                    if (XNode.ReadFrom(reader) is XElement element && ToEntry(element, file, origin, locales) is { } entry)
                        entries.Add(entry);
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        {
            // A damaged catalog still gives whatever was read before the damage.
        }
        return entries;
    }

    private static CatalogEntry? ToEntry(
        XElement component, CatalogFile file, string? origin, IReadOnlyList<string> locales)
    {
        var id = component.Element("id")?.Value.Trim();
        var name = Localized(component, "name", locales);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            return null;

        var sourceId = file.SourceId ?? origin;
        if (string.IsNullOrEmpty(sourceId))
            return null;

        var custom = component.Element("custom")?.Elements("value")
            .Where(v => v.Attribute("key") is not null)
            .GroupBy(v => v.Attribute("key")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value.Trim(), StringComparer.Ordinal) ?? [];
        var verified = custom.GetValueOrDefault("flathub::verification::verified") == "true";

        var otherIds = (component.Element("provides")?.Elements("id").Select(e => e.Value.Trim()) ?? [])
            .Concat(component.Elements("launchable").Where(l => (string?)l.Attribute("type") == "desktop-id")
                .Select(l => l.Value.Trim()))
            .Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        var (iconPath, iconName) = Icon(component, file, origin);
        return new CatalogEntry(
            ProviderId: file.ProviderId,
            SourceId: sourceId,
            ComponentId: id,
            Name: name,
            Summary: Localized(component, "summary", locales),
            Description: Description(component),
            Developer: Nullable(component.Element("developer")?.Element("name")?.Value ?? component.Element("developer_name")?.Value),
            License: Nullable(component.Element("project_license")?.Value),
            Homepage: Nullable(component.Elements("url").FirstOrDefault(u => (string?)u.Attribute("type") == "homepage")?.Value),
            IconPath: iconPath,
            IconName: iconName,
            DesktopCategories: component.Element("categories")?.Elements("category").Select(c => c.Value.Trim()).ToList() ?? [],
            OtherIds: otherIds,
            PackageName: Nullable(component.Element("pkgname")?.Value ?? component.Element("bundle")?.Value),
            Version: Nullable((string?)component.Element("releases")?.Element("release")?.Attribute("version")),
            MakerVerified: verified,
            // Only a website says something to a reader; account names on code-hosting sites do not.
            VerifiedBy: verified ? Nullable(custom.GetValueOrDefault("flathub::verification::website")) : null)
        {
            Screenshots = Screenshots(component),
        };
    }

    private static (string? Path, string? Name) Icon(XElement component, CatalogFile file, string? origin)
    {
        string? stock = null;
        string? best = null;
        var bestWidth = 0;
        foreach (var icon in component.Elements("icon"))
        {
            var type = (string?)icon.Attribute("type");
            var value = icon.Value.Trim();
            if (type == "stock")
            {
                stock ??= value;
            }
            else if (type == "cached" && icon.Attribute("scale") is null && value.Length > 0 &&
                // A catalog must not be able to point at files outside its own icon folder.
                value.IndexOfAny(['/', '\\']) < 0 && !value.StartsWith('.'))
            {
                var width = (int?)icon.Attribute("width") ?? 64;
                // 64 is what rows use; prefer it, otherwise take the closest larger one.
                if (best is null || Math.Abs(width - 64) < Math.Abs(bestWidth - 64))
                {
                    var folder = $"{width}x{width}";
                    var candidates = new[]
                    {
                        Path.Combine(file.IconRoot, folder, value),
                        origin is null ? null : Path.Combine(file.IconRoot, origin, folder, value),
                    };
                    if (candidates.FirstOrDefault(c => c is not null && File.Exists(c)) is { } found)
                    {
                        best = found;
                        bestWidth = width;
                    }
                }
            }
        }
        return (best, stock);
    }

    // The default screenshot first, then the others in catalog order.
    private static List<string> Screenshots(XElement component) =>
        (component.Element("screenshots")?.Elements("screenshot") ?? [])
            .OrderBy(shot => (string?)shot.Attribute("type") == "default" ? 0 : 1)
            .Select(shot => ScreenshotUrls.Choose(shot.Elements("image").Select(image => (
                image.Value.Trim(),
                (string?)image.Attribute("type"),
                int.TryParse((string?)image.Attribute("width"), out var width) ? width : (int?)null))))
            .Where(url => url is not null)
            .Select(url => url!)
            .Distinct(StringComparer.Ordinal)
            .Take(ScreenshotUrls.MaxPerApp)
            .ToList();

    // AppStream descriptions are small HTML: paragraphs and lists. Shown as plain text.
    private static string? Description(XElement component)
    {
        var description = component.Elements("description")
            .FirstOrDefault(d => d.Attribute(XNamespace.Xml + "lang") is null);
        if (description is null)
            return null;

        var text = new StringBuilder();
        foreach (var block in description.Elements().Where(e => e.Attribute(XNamespace.Xml + "lang") is null))
        {
            if (block.Name.LocalName is "ul" or "ol")
            {
                foreach (var item in block.Elements("li").Where(e => e.Attribute(XNamespace.Xml + "lang") is null))
                    text.Append("• ").Append(Collapse(item.Value)).Append('\n');
                text.Append('\n');
            }
            else
            {
                text.Append(Collapse(block.Value)).Append("\n\n");
            }
            if (text.Length > MaxDescriptionChars)
                break;
        }

        var result = text.ToString().Trim();
        if (result.Length > MaxDescriptionChars)
            result = result[..MaxDescriptionChars].TrimEnd() + "…";
        return result.Length == 0 ? null : result;
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Localized(XElement component, string element, IReadOnlyList<string> locales)
    {
        string? fallback = null;
        string? best = null;
        var bestRank = int.MaxValue;
        foreach (var candidate in component.Elements(element))
        {
            var text = candidate.Value.Trim();
            if (text.Length == 0)
                continue;
            var language = (string?)candidate.Attribute(XNamespace.Xml + "lang");
            if (string.IsNullOrEmpty(language) || language == "C")
            {
                fallback ??= text;
                continue;
            }
            var normalized = language.Replace('-', '_');
            for (var i = 0; i < locales.Count && i < bestRank; i++)
            {
                if (string.Equals(locales[i], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    best = text;
                    bestRank = i;
                }
            }
        }
        return best ?? fallback;
    }

    private static string? Nullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Dictionary<string, CachedFile> ReadCache(string entryName)
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
            // A damaged file only means reading the catalogs again.
        }
        return [];
    }

    private static List<string> Files(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory)
                    .Where(f => f.EndsWith(".xml", StringComparison.Ordinal) || f.EndsWith(".xml.gz", StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> Directories(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed record CachedFile(long ModifiedTicks, long Length, List<CatalogEntry> Entries);

    private sealed record CacheEnvelope(int FormatVersion, Dictionary<string, CachedFile> Files);
}
