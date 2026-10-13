using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>
/// The bundled table of sources Yar can describe in plain language. A source that is not in
/// the table keeps <see cref="TrustLevel.Unknown"/>; Yar never guesses that a source is safe.
/// </summary>
public sealed class KnownSources
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IReadOnlyList<Rule> _rules;
    private readonly string? _distroId;

    private KnownSources(IReadOnlyList<Rule> rules, string? distroId)
    {
        _rules = rules;
        _distroId = distroId;
    }

    public static KnownSources LoadBundled(string? distroId)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("known-sources.json")
            ?? throw new InvalidOperationException("The bundled list of known sources is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), distroId);
    }

    public static KnownSources Parse(string json, string? distroId) =>
        new(JsonSerializer.Deserialize<List<Rule>>(json, JsonOptions) ?? [], distroId);

    /// <summary>Applies every matching rule; earlier rules win for each field.</summary>
    public SourceInfo Describe(SourceInfo source)
    {
        string? name = null, description = null, homepage = null;
        TrustLevel? trust = null;
        var technical = false;

        foreach (var rule in _rules.Where(rule => Matches(rule, source)))
        {
            name ??= rule.Name;
            description ??= rule.Description;
            homepage ??= rule.Homepage;
            trust ??= rule.Trust;
            technical |= rule.Technical;
        }

        // A homepage the source announces itself (Flatpak remotes do) is kept.
        if (source.Technical.ContainsKey("homepage"))
            homepage = null;

        if (name is null && description is null && trust is null && homepage is null && !technical)
            return source;

        var details = source.Technical;
        if (technical || homepage is not null)
        {
            var extended = new Dictionary<string, string>(source.Technical);
            if (technical)
                extended["for-developers"] = "true";
            if (homepage is not null)
                extended["homepage"] = homepage;
            details = extended;
        }

        return source with
        {
            Name = name ?? source.Name,
            Description = description ?? source.Description,
            Trust = trust ?? source.Trust,
            Technical = details,
        };
    }

    private bool Matches(Rule rule, SourceInfo source)
    {
        if (rule.Provider != source.ProviderId)
            return false;
        if (rule.Distro is not null && rule.Distro != _distroId)
            return false;
        if (rule.Id is null && rule.UrlHost is null)
            return false;
        if (rule.Id is not null && !Glob(rule.Id, source.Id))
            return false;
        if (rule.UrlHost is not null && HostOf(source.Url) != rule.UrlHost)
            return false;
        return true;
    }

    // A single "*" at the start or end of the pattern; everything else must match exactly.
    private static bool Glob(string pattern, string value) =>
        pattern.StartsWith('*') ? value.EndsWith(pattern[1..], StringComparison.Ordinal)
        : pattern.EndsWith('*') ? value.StartsWith(pattern[..^1], StringComparison.Ordinal)
        : pattern == value;

    private static string? HostOf(string? url)
    {
        if (url is null)
            return null;
        // Flatpak writes OCI remotes as "oci+https://host".
        var plus = url.IndexOf('+');
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (plus >= 0 && plus < schemeEnd)
            url = url[(plus + 1)..];
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private sealed record Rule(
        string Provider, string? Distro, string? Id, string? UrlHost,
        string? Name, string? Description, string? Homepage, TrustLevel? Trust, bool Technical);
}
