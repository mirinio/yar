using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Yar.Core;

namespace Yar.Online;

/// <summary>
/// Ratings and reviews from ODRS (the service GNOME Software and KDE Discover use) and the list
/// of most-installed apps from Flathub. This is the only part of Yar that talks to the internet,
/// and it is only used while the user leaves online data switched on.
/// </summary>
public sealed class OnlineStoreData : IOnlineStoreData, IDisposable
{
    public const string FlathubApi = "https://flathub.org/api/v2/";
    public const string RatingsUrl = "https://odrs.gnome.org/1.0/reviews/api/ratings";
    public const string ReviewsUrl = "https://odrs.gnome.org/1.0/reviews/api/fetch";

    private const int MaxReviews = 30;
    private const int MaxIdsToAsk = 3;
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

    private readonly HttpClient _client;
    private readonly ICacheStore? _cache;
    private readonly string _distro;
    private readonly string _locale;
    private readonly TimeProvider _time;
    private readonly ImageStore? _images;
    private readonly Dictionary<string, InstallStats?> _stats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppSummary?> _summaries = new(StringComparer.Ordinal);

    public OnlineStoreData(
        ICacheStore? cache, string? distroName, CultureInfo? culture = null,
        HttpMessageHandler? handler = null, TimeProvider? time = null, string? imageDirectory = null)
    {
        _images = imageDirectory is null ? null : new ImageStore(imageDirectory);
        _cache = cache;
        _distro = string.IsNullOrWhiteSpace(distroName) ? "Linux" : distroName;
        _locale = (culture ?? CultureInfo.CurrentUICulture).Name.Replace('-', '_') is { Length: > 0 } name ? name : "en_US";
        _time = time ?? TimeProvider.System;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(15);
        _client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Yar/0.1");
    }

    public async Task<StoreCollections> GetCollectionsAsync(CancellationToken ct)
    {
        // Each list is its own request; one that fails just stays empty.
        async Task<IReadOnlyList<StoreListing>> List(string name, string path, Func<string, IReadOnlyList<StoreListing>> parse)
        {
            try
            {
                return parse(await GetKeptAsync($"online-{name}.json", FlathubApi + path, ct).ConfigureAwait(false));
            }
            catch (Exception e) when (
                e is HttpRequestException or InvalidDataException or JsonException ||
                (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                return [];
            }
        }

        var today = _time.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var picks = List("picks", $"app-picks/apps-of-the-week/{today}", ParsePicks);
        var trending = List("trending", "collection/trending?page=1&per_page=40", ParseCollection);
        var popular = List("popular", "collection/popular?page=1&per_page=100", ParseCollection);
        var updated = List("updated", "collection/recently-updated?page=1&per_page=40", ParseCollection);
        return new(
            await picks.ConfigureAwait(false), await trending.ConfigureAwait(false),
            await popular.ConfigureAwait(false), await updated.ConfigureAwait(false));
    }

    public async Task<InstallStats?> GetInstallStatsAsync(string appId, CancellationToken ct)
    {
        if (!IsAppId(appId))
            return null;
        lock (_stats)
        {
            if (_stats.TryGetValue(appId, out var known))
                return known;
        }

        using var response = await _client.GetAsync(FlathubApi + "stats/" + appId, ct).ConfigureAwait(false);
        // The service answers "not found" for apps it does not carry, such as distribution packages.
        var stats = response.IsSuccessStatusCode
            ? ParseStats(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
            : null;
        lock (_stats)
            _stats[appId] = stats;
        return stats;
    }

    public async Task<AppSummary?> GetAppSummaryAsync(string appId, CancellationToken ct)
    {
        if (!IsAppId(appId))
            return null;
        lock (_summaries)
        {
            if (_summaries.TryGetValue(appId, out var known))
                return known;
        }

        using var response = await _client.GetAsync(FlathubApi + "summary/" + appId, ct).ConfigureAwait(false);
        var summary = response.IsSuccessStatusCode
            ? ParseSummary(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
            : null;
        lock (_summaries)
            _summaries[appId] = summary;
        return summary;
    }

    public Task<byte[]?> GetImageAsync(string url, CancellationToken ct) =>
        _images is null ? Task.FromResult<byte[]?>(null) : _images.GetAsync(_client, url, ct);

    public async Task<IReadOnlyDictionary<string, StarRating>> GetRatingsAsync(CancellationToken ct) =>
        ParseRatings(await GetKeptAsync("online-ratings.json", RatingsUrl, ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<AppReview>> GetReviewsAsync(IReadOnlyList<string> appIds, CancellationToken ct)
    {
        // The same app goes by several IDs; ask for the first few until one has reviews.
        foreach (var appId in appIds.Take(MaxIdsToAsk))
        {
            using var response = await _client.PostAsJsonAsync(ReviewsUrl, new Dictionary<string, object>
            {
                // The service wants an ID for the asker. A fresh random one each time identifies nobody.
                ["user_hash"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20)),
                ["app_id"] = appId,
                ["locale"] = _locale,
                ["distro"] = _distro,
                ["version"] = "unknown",
                ["limit"] = MaxReviews,
            }, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var reviews = ParseReviews(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (reviews.Count > 0)
                return reviews;
        }
        return [];
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Parses a Flathub collection: a list of apps, each with last month's install count.</summary>
    public static IReadOnlyList<StoreListing> ParseCollection(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("A list of apps came in a form I do not understand.");
        }
        return hits.EnumerateArray()
            .Where(hit => hit.ValueKind == JsonValueKind.Object && Text(hit, "app_id") is not null)
            .Select(hit => new StoreListing(
                Text(hit, "app_id")!,
                Number(hit, "installs_last_month") is { } installs and >= 0 ? (long)installs : null))
            .ToList();
    }

    /// <summary>Parses Flathub's picks of the week, in the order Flathub ranks them.</summary>
    public static IReadOnlyList<StoreListing> ParsePicks(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The picks of the week came in a form I do not understand.");
        }
        return apps.EnumerateArray()
            .Where(app => app.ValueKind == JsonValueKind.Object && Text(app, "app_id") is not null)
            .OrderBy(app => Number(app, "position") ?? double.MaxValue)
            .Select(app => new StoreListing(Text(app, "app_id")!))
            .ToList();
    }

    // App IDs are dotted names; anything else is not sent anywhere.
    private static bool IsAppId(string appId) =>
        appId.Length is > 0 and <= 255 && appId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Parses Flathub's summary of an app: its sizes and what it is allowed to reach.</summary>
    public static AppSummary? ParseSummary(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("installed_size", out _))
                return null;

            long? Bytes(string property) => Number(root, property) is { } value and > 0 ? (long)value : null;
            RawPermissions? permissions = null;
            if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("permissions", out var granted) && granted.ValueKind == JsonValueKind.Object)
            {
                List<string> List(JsonElement parent, string property) =>
                    parent.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
                        ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()!).ToList()
                        : [];
                List<string> Bus(string property) =>
                    granted.TryGetProperty(property, out var bus) && bus.ValueKind == JsonValueKind.Object
                        ? [.. List(bus, "talk"), .. List(bus, "own")]
                        : [];
                permissions = new(
                    List(granted, "shared"), List(granted, "sockets"), List(granted, "devices"),
                    List(granted, "filesystems"), Bus("session-bus"), Bus("system-bus"));
            }
            return new(new OfferSize(Bytes("download_size"), Bytes("installed_size")), permissions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static InstallStats? ParseStats(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                Number(document.RootElement, "installs_total") is not { } total || total < 0)
            {
                return null;
            }
            long Count(string property) =>
                Number(document.RootElement, property) is { } value and >= 0 ? (long)value : 0;
            return new((long)total, Count("installs_last_month"), Count("installs_last_7_days"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyDictionary<string, StarRating> ParseRatings(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The ratings came in a form I do not understand.");

        var ratings = new Dictionary<string, StarRating>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in document.RootElement.EnumerateObject())
        {
            if (app.Value.ValueKind != JsonValueKind.Object)
                continue;
            int Count(string star) =>
                app.Value.TryGetProperty(star, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var count) && count > 0 ? count : 0;
            var rating = new StarRating(Count("star1"), Count("star2"), Count("star3"), Count("star4"), Count("star5"));
            if (rating.Total > 0)
                ratings[app.Name] = rating;
        }
        return ratings;
    }

    public static IReadOnlyList<AppReview> ParseReviews(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var reviews = new List<AppReview>();
        foreach (var review in document.RootElement.EnumerateArray())
        {
            if (review.ValueKind != JsonValueKind.Object || Number(review, "reported") > 0)
                continue;
            // The service scores from 0 to 100 in steps of 20.
            if (Number(review, "rating") is not { } rating || rating <= 0)
                continue;

            var summary = Text(review, "summary");
            var text = Text(review, "description");
            if (summary is null && text is null)
                continue;

            reviews.Add(new AppReview(
                Stars: Math.Clamp((int)Math.Round(rating / 20.0), 1, 5),
                Summary: summary,
                Text: text,
                Reviewer: Text(review, "user_display"),
                Date: Number(review, "date_created") is { } seconds and > 0 and < 32_503_680_000
                    ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
                    : null,
                Version: Text(review, "version"),
                Distro: Text(review, "distro")));
        }
        return reviews;
    }

    // Fetched at most once a day; the copy from last time is used when the service cannot be reached.
    private async Task<string> GetKeptAsync(string entryName, string url, CancellationToken ct)
    {
        var kept = ReadKept(entryName);
        if (kept is not null && _time.GetUtcNow() - kept.FetchedAt < KeepFor)
            return kept.Body;

        try
        {
            var body = await _client.GetStringAsync(url, ct).ConfigureAwait(false);
            _cache?.Write(entryName, JsonSerializer.Serialize(new Kept(_time.GetUtcNow(), body)));
            return body;
        }
        catch (Exception e) when (kept is not null && e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return kept.Body;
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

    // Numbers arrive as numbers from some endpoints and as strings from others.
    private static double? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetDouble(out var number) => number,
                JsonValueKind.String when double.TryParse(
                    value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            }
            : null;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;

    private sealed record Kept(DateTimeOffset FetchedAt, string Body);
}
