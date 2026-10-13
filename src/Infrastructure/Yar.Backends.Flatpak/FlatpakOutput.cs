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
