using System.Text.Json;

namespace Yar.Backends.Snap;

public sealed record SnapPackage(
    string Name, string? Title, string? Summary, string? Version, string? Revision, string? Channel,
    string? Publisher, string? Confinement, long? InstalledSizeBytes, string? Refreshed,
    IReadOnlyList<string> DesktopFiles);

/// <summary>Parses the response of snapd's <c>GET /v2/snaps</c>.</summary>
public static class SnapdJson
{
    /// <summary>Parses <c>GET /v2/find?select=refresh</c>: snaps with a newer revision waiting.</summary>
    public static IReadOnlyList<(string Name, string? Title, string? Version)> ParseRefreshable(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("snapd sent an answer Yar does not understand.");
        }

        var snaps = new List<(string, string?, string?)>();
        foreach (var snap in result.EnumerateArray())
        {
            if (snap.ValueKind == JsonValueKind.Object && Text(snap, "name") is { Length: > 0 } name)
                snaps.Add((name, Text(snap, "title"), Text(snap, "version")));
        }
        return snaps;
    }

    /// <summary>Returns the installed snaps that are apps (not bases, kernels or snapd itself).</summary>
    public static IReadOnlyList<SnapPackage> ParseInstalledApps(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("snapd sent an answer Yar does not understand.");
        }

        var snaps = new List<SnapPackage>();
        foreach (var snap in result.EnumerateArray())
        {
            if (snap.ValueKind != JsonValueKind.Object || Text(snap, "type") != "app")
                continue;
            if (Text(snap, "name") is not { Length: > 0 } name)
                continue;

            snaps.Add(new SnapPackage(
                Name: name,
                Title: Text(snap, "title"),
                Summary: Text(snap, "summary"),
                Version: Text(snap, "version"),
                Revision: Text(snap, "revision"),
                Channel: Text(snap, "tracking-channel") ?? Text(snap, "channel"),
                Publisher: PublisherName(snap),
                Confinement: Text(snap, "confinement"),
                InstalledSizeBytes: snap.TryGetProperty("installed-size", out var size) &&
                    size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) ? bytes : null,
                Refreshed: Text(snap, "install-date"),
                DesktopFiles: DesktopFiles(snap)));
        }
        return snaps;
    }

    private static string? PublisherName(JsonElement snap) =>
        snap.TryGetProperty("publisher", out var publisher) && publisher.ValueKind == JsonValueKind.Object
            ? Text(publisher, "display-name") ?? Text(publisher, "username")
            : null;

    private static List<string> DesktopFiles(JsonElement snap)
    {
        var files = new List<string>();
        if (snap.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
        {
            foreach (var app in apps.EnumerateArray())
            {
                if (app.ValueKind == JsonValueKind.Object && Text(app, "desktop-file") is { Length: > 0 } file)
                    files.Add(file);
            }
        }
        return files;
    }

    // Revisions arrive as strings from some snapd versions and as numbers from others.
    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}
