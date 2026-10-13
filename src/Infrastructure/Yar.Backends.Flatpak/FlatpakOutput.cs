using System.Globalization;
using Yar.Core;

namespace Yar.Backends.Flatpak;

public sealed record FlatpakRemote(
    string Name, string? Title, string? Url, IReadOnlyList<string> Options, string? Description, string? Homepage);

public sealed record FlatpakApp(
    string Application, string? Name, string? Version, string Branch, string Origin,
    string Installation, long? SizeBytes, string Ref);

public sealed record FlatpakUpdate(string Application, string? Name, string? Version, string Origin);

/// <summary>Parses the tab-separated output of <c>flatpak remotes</c> and <c>flatpak list</c>.</summary>
public static class FlatpakOutput
{
    public static readonly string[] RemoteColumns = ["name", "title", "url", "options", "description", "homepage"];

    public static readonly string[] AppColumns =
        ["application", "name", "version", "branch", "origin", "installation", "size", "ref"];

    public static readonly string[] UpdateColumns = ["application", "name", "version", "origin"];

    public static IReadOnlyList<FlatpakUpdate> ParseUpdates(string output) =>
        Rows(output, UpdateColumns.Length)
            .Where(row => row[0].Length > 0)
            .Select(row => new FlatpakUpdate(row[0], Optional(row[1]), Optional(row[2]), row[3]))
            .ToList();

    /// <summary>
    /// Parses what <c>flatpak info --show-permissions</c> prints: sections of <c>key=value;value;</c> lines.
    /// </summary>
    public static RawPermissions ParsePermissions(string output)
    {
        var context = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var sessionBus = new List<string>();
        var systemBus = new List<string>();
        var section = "";

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = line[..separator];
            var value = line[(separator + 1)..];
            switch (section)
            {
                case "Context":
                    context[key] = [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                // Here the key is the name of the other program; "none" means access was taken away.
                case "Session Bus Policy" when value != "none":
                    sessionBus.Add(key);
                    break;
                case "System Bus Policy" when value != "none":
                    systemBus.Add(key);
                    break;
            }
        }

        List<string> Of(string key) => context.GetValueOrDefault(key) ?? [];
        return new(Of("shared"), Of("sockets"), Of("devices"), Of("filesystems"), sessionBus, systemBus);
    }

    public static readonly string[] RuntimeColumns = ["application", "name", "branch", "size", "installation"];

    /// <summary>Parses <c>flatpak list --runtime</c>: the shared parts Flatpak apps are built on.</summary>
    public static IReadOnlyList<SharedPart> ParseRuntimes(string output) =>
        Rows(output, RuntimeColumns.Length)
            .Where(row => row[0].Length > 0 && ParseSize(row[3]) is > 0)
            .Select(row => new SharedPart(
                "flatpak", $"{row[4]}:{row[0]}/{row[2]}",
                // Some runtimes already carry their version in the name.
                Optional(row[1]) is { } name
                    ? name.Contains(row[2], StringComparison.Ordinal) ? name : $"{name} {row[2]}"
                    : $"{row[0]} {row[2]}",
                ParseSize(row[3])!.Value))
            .ToList();

    public static IReadOnlyList<FlatpakRemote> ParseRemotes(string output) =>
        Rows(output, RemoteColumns.Length)
            .Where(row => row[0].Length > 0)
            .Select(row => new FlatpakRemote(
                Name: row[0],
                Title: Optional(row[1]),
                Url: Optional(row[2]),
                Options: row[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Description: Optional(row[4]),
                Homepage: Optional(row[5])))
            .ToList();

    public static IReadOnlyList<FlatpakApp> ParseApps(string output) =>
        Rows(output, AppColumns.Length)
            .Where(row => row[0].Length > 0 && row[7].Length > 0)
            .Select(row => new FlatpakApp(
                Application: row[0],
                Name: Optional(row[1]),
                Version: Optional(row[2]),
                Branch: row[3],
                Origin: row[4],
                Installation: row[5],
                SizeBytes: ParseSize(row[6]),
                Ref: row[7]))
            .ToList();

    /// <summary>Parses sizes as flatpak prints them, e.g. "608.3 MB" (with a non-breaking space).</summary>
    public static long? ParseSize(string text)
    {
        var parts = text.Split([' ', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        double? factor = parts[1] switch
        {
            "byte" or "bytes" => 1,
            "kB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            "KiB" => 1024,
            "MiB" => 1024 * 1024,
            "GiB" => 1024 * 1024 * 1024,
            _ => null,
        };
        return factor is null ? null : (long)Math.Round(number * factor.Value);
    }

    // Lines with an unexpected number of columns are dropped rather than guessed at.
    private static IEnumerable<string[]> Rows(string output, int columns) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(row => row.Length == columns)
            .Select(row => row.Select(cell => cell.Trim()).ToArray());

    private static string? Optional(string cell) => cell is "" or "-" ? null : cell;
}
