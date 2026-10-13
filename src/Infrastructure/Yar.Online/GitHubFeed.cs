using System.Globalization;
using System.Net;
using System.Text.Json;
using Yar.Core;

namespace Yar.Online;

/// <summary>
/// New source-code projects that are gathering interest, from GitHub's public search. GitHub has
/// no "trending" service, so this asks for repositories created in the last month with the most
/// stars. No account or token is used, and answers are kept for hours to stay well within the
/// small number of searches GitHub allows.
/// </summary>
public sealed class GitHubFeed : IDeveloperFeed, IDisposable
{
    public const int MaxLanguages = 8;
    public const int PerLanguage = 6;

    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(6);

    private readonly HttpClient _client;
    private readonly ICacheStore? _cache;
    private readonly TimeProvider _time;

    public GitHubFeed(ICacheStore? cache, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _cache = cache;
        _time = time ?? TimeProvider.System;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(15);
        _client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Yar/0.1");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<IReadOnlyList<DevProject>> GetTrendingAsync(IReadOnlyList<string> languages, CancellationToken ct)
    {
        var queries = languages.Select(DevLanguages.QueryFor).Where(q => q is not null).Select(q => q!)
            .Distinct().Take(MaxLanguages).ToList();

        var projects = new List<DevProject>();
        // One after another: GitHub counts searches per minute, and there is no hurry.
        foreach (var query in queries)
            projects.AddRange(await ForLanguageAsync(query, ct).ConfigureAwait(false));

        return projects.DistinctBy(p => p.FullName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Stars).ToList();
    }

    public void Dispose() => _client.Dispose();

    public static IReadOnlyList<DevProject> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var projects = new List<DevProject>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                Text(item, "full_name") is not { } name || Text(item, "html_url") is not { } url)
            {
                continue;
            }
            // Only links to GitHub itself are ever offered for opening.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com")
                continue;

            var description = Text(item, "description");
            projects.Add(new DevProject(
                name,
                description is { Length: > 300 } ? description[..300] + "…" : description,
                Text(item, "language"),
                item.TryGetProperty("stargazers_count", out var stars) && stars.TryGetInt64(out var count) ? count : 0,
                uri.AbsoluteUri));
        }
        return projects;
    }

    private async Task<IReadOnlyList<DevProject>> ForLanguageAsync(string query, CancellationToken ct)
    {
        var entryName = $"github-{query}.json";
        var kept = ReadKept(entryName);
        if (kept is not null && _time.GetUtcNow() - kept.FetchedAt < KeepFor)
            return Parse(kept.Body);

        try
        {
            var since = _time.GetUtcNow().AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = "https://api.github.com/search/repositories" +
                $"?q={Uri.EscapeDataString($"created:>{since} language:{query}")}&sort=stars&order=desc&per_page={PerLanguage}";
            using var response = await _client.GetAsync(url, ct).ConfigureAwait(false);
            // "Too many searches" is expected now and then; fall back to what was kept, or nothing.
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || !response.IsSuccessStatusCode)
                return kept is null ? [] : Parse(kept.Body);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var projects = Parse(body);
            _cache?.Write(entryName, JsonSerializer.Serialize(new Kept(_time.GetUtcNow(), body)));
            return projects;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return kept is null ? [] : Parse(kept.Body);
        }
    }

    private Kept? ReadKept(string entryName)
    {
        try
        {
            return _cache?.Read(entryName) is { } json ? JsonSerializer.Deserialize<Kept>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;

    private sealed record Kept(DateTimeOffset FetchedAt, string Body);
}
