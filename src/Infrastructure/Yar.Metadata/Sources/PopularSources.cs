using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>A source Yar knows about and can suggest. Without a <see cref="Url"/> it can only be read about.</summary>
public sealed record SuggestedSource(
    string Name, string ProviderId, string? RemoteName, string? Url, string Homepage,
    TrustLevel Trust, string Description, string? Caution);

/// <summary>The bundled list of popular sources. It ships with Yar; it is never fetched.</summary>
public static class PopularSources
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IReadOnlyList<SuggestedSource> LoadBundled()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("popular-sources.json")
            ?? throw new InvalidOperationException("The bundled list of popular sources is missing.");
        return JsonSerializer.Deserialize<List<SuggestedSource>>(stream, Options) ?? [];
    }

    /// <summary>The source on this computer that a suggestion corresponds to, if it has been added already.</summary>
    public static SourceInfo? FindExisting(SuggestedSource suggestion, IReadOnlyList<SourceInfo> sources) =>
        sources.FirstOrDefault(source =>
            source.ProviderId == suggestion.ProviderId && suggestion.RemoteName is not null &&
            source.Technical.TryGetValue("remote", out var remote) && remote == suggestion.RemoteName);
}
