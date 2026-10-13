using System.Globalization;
using System.Text;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>
/// Reads <c>.desktop</c> files as described by the freedesktop.org Desktop Entry specification.
/// Input is untrusted: anything malformed makes <see cref="Parse"/> return null instead of throwing.
/// </summary>
public static class DesktopEntryParser
{
    public const long MaxFileBytes = 1024 * 1024;

    private const string MainGroup = "Desktop Entry";

    public static DesktopEntry? ParseFile(string path, string desktopId, CultureInfo? culture = null)
    {
        try
        {
            var info = new FileInfo(path);
            // An empty file is no launcher. This also skips named pipes, which report no length
            // and would make reading wait forever.
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes)
                return null;
            return Parse(File.ReadAllText(path, Encoding.UTF8), path, desktopId, culture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static DesktopEntry? Parse(string content, string path, string desktopId, CultureInfo? culture = null)
    {
        var raw = ReadMainGroup(content);
        if (raw is null)
            return null;

        var locales = LocaleFallback.Candidates(culture ?? CultureInfo.CurrentUICulture);
        string? Localized(string key)
        {
            foreach (var locale in locales)
            {
                if (raw.TryGetValue($"{key}[{locale}]", out var localized))
                    return Unescape(localized);
            }
            return raw.TryGetValue(key, out var value) ? Unescape(value) : null;
        }
        string? Plain(string key) => raw.TryGetValue(key, out var value) ? Unescape(value) : null;
        bool Flag(string key) => raw.TryGetValue(key, out var value) && value == "true";

        var type = Plain("Type");
        var name = Localized("Name");
        if (string.IsNullOrEmpty(type) || string.IsNullOrWhiteSpace(name))
            return null;

        // Unlocalized keys only: localized variants are resolved above and not needed again.
        var keys = raw.Where(pair => !pair.Key.Contains('['))
            .ToDictionary(pair => pair.Key, pair => Unescape(pair.Value), StringComparer.Ordinal);

        return new DesktopEntry(
            Path: path,
            DesktopId: desktopId,
            Type: type,
            Name: name,
            GenericName: Localized("GenericName"),
            Comment: Localized("Comment"),
            Icon: Localized("Icon"),
            Exec: Plain("Exec"),
            TryExec: Plain("TryExec"),
            NoDisplay: Flag("NoDisplay"),
            Hidden: Flag("Hidden"),
            Categories: raw.TryGetValue("Categories", out var categories) ? SplitList(categories) : [],
            Keys: keys);
    }

    private static Dictionary<string, string>? ReadMainGroup(string content)
    {
        Dictionary<string, string>? values = null;
        var inMainGroup = false;

        foreach (var rawLine in content.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty || line[0] == '#')
                continue;

            if (line[0] == '[')
            {
                if (line[^1] != ']')
                    return null;
                var group = line[1..^1];
                inMainGroup = group.SequenceEqual(MainGroup);
                if (inMainGroup)
                {
                    if (values is not null)
                        return null; // The main group may only appear once.
                    values = new(StringComparer.Ordinal);
                }
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
                return null;
            if (!inMainGroup)
            {
                if (values is null)
                    return null; // Entries before any group header.
                continue;
            }

            var key = line[..separator].TrimEnd().ToString();
            var value = line[(separator + 1)..].TrimStart().ToString();
            // The spec forbids duplicate keys; keep the first, as GLib does not define an order.
            values!.TryAdd(key, value);
        }

        return values;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 == value.Length)
            {
                builder.Append(c);
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 's': builder.Append(' '); break;
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '\\': builder.Append('\\'); break;
                default: builder.Append('\\').Append(next); break;
            }
        }
        return builder.ToString();
    }

    private static List<string> SplitList(string value)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length && value[i + 1] == ';')
            {
                current.Append(';');
                i++;
            }
            else if (c == ';')
            {
                if (current.Length > 0)
                    items.Add(Unescape(current.ToString()));
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            items.Add(Unescape(current.ToString()));
        return items;
    }
}
