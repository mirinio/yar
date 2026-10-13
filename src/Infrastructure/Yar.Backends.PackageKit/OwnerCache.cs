using System.Text.Json;
using Yar.Core;

namespace Yar.Backends.PackageKit;

/// <summary>A file's last-modified time and length: enough to notice that a launcher was replaced.</summary>
internal readonly record struct FileStamp(long ModifiedTicks, long Length)
{
    public static FileStamp? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new(info.LastWriteTimeUtc.Ticks, info.Length) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Remembers which package owns which launcher, so that the next start does not have to ask
/// PackageKit about every launcher again. Everything in here is re-checked before it is used.
/// </summary>
internal sealed record OwnerCache(
    IReadOnlyDictionary<string, OwnerCache.Owner> Owners, IReadOnlyDictionary<string, long> Sizes)
{
    private const string EntryName = "packagekit-owners.json";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static OwnerCache Empty { get; } = new(new Dictionary<string, Owner>(), new Dictionary<string, long>());

    public static OwnerCache Load(ICacheStore? store)
    {
        try
        {
            if (store?.Read(EntryName) is { } json &&
                JsonSerializer.Deserialize<Envelope>(json, Options) is { FormatVersion: FormatVersion } envelope)
            {
                return envelope.Cache;
            }
        }
        catch (JsonException)
        {
            // A damaged file only means asking PackageKit again.
        }
        return Empty;
    }

    public static void Save(ICacheStore? store, OwnerCache cache) =>
        store?.Write(EntryName, JsonSerializer.Serialize(new Envelope(FormatVersion, cache), Options));

    /// <param name="Package">"name.arch" of the owning package, or null when no package owns the launcher.</param>
    internal sealed record Owner(FileStamp Stamp, string? Package);

    private sealed record Envelope(int FormatVersion, OwnerCache Cache);
}
