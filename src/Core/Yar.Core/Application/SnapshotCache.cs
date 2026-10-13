using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yar.Core;

/// <summary>
/// Saves a <see cref="CatalogSnapshot"/> so the next start can show it at once. The saved file is
/// a convenience, not a record: anything odd about it means "no saved snapshot".
/// </summary>
public static class SnapshotCache
{
    /// <summary>Raise this whenever a record inside the snapshot changes shape.</summary>
    public const int FormatVersion = 2;

    public const string EntryName = "snapshot.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // A missing or null list in a tampered or outdated file must fail here, not crash a page later.
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32,
    };

    public static void Write(ICacheStore store, CatalogSnapshot snapshot)
    {
        try
        {
            store.Write(EntryName, JsonSerializer.Serialize(new Envelope(FormatVersion, DateTimeOffset.UtcNow, snapshot), Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not being able to save only means the next start is slower.
        }
    }

    public static CatalogSnapshot? Read(ICacheStore store)
    {
        try
        {
            if (store.Read(EntryName) is not { } json)
                return null;
            var envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
            return envelope is { FormatVersion: FormatVersion } ? envelope.Snapshot : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when two snapshots would look the same to the user. Timings and the moment of the
    /// last update check differ on every load and are left out.
    /// </summary>
    public static bool SameContent(CatalogSnapshot a, CatalogSnapshot b) =>
        ContentKey(a) == ContentKey(b);

    private static string ContentKey(CatalogSnapshot snapshot) => JsonSerializer.Serialize(
        snapshot with
        {
            Timings = [],
            Providers = snapshot.Providers.Select(p => p with { UpdatesRefreshedAt = null }).ToList(),
        },
        Options);

    private sealed record Envelope(int FormatVersion, DateTimeOffset SavedAt, CatalogSnapshot Snapshot);
}
