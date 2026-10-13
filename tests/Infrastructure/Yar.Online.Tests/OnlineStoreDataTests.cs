using System.Net;
using Yar.Core;

namespace Yar.Online.Tests;

public class OnlineStoreDataTests
{
    private sealed class MemoryCache : ICacheStore
    {
        public Dictionary<string, string> Entries { get; } = [];
        public string? Read(string name) => Entries.GetValueOrDefault(name);
        public void Write(string name, string content) => Entries[name] = content;
    }

    private sealed class FakeWeb(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.AbsoluteUri, body));
            var (status, content) = respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(content) };
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Parses_collections_picks_and_install_counts()
    {
        var collection = OnlineStoreData.ParseCollection(
            "{\"hits\":[{\"app_id\":\"org.mozilla.firefox\",\"installs_last_month\":501234},{\"name\":\"no id\"},{\"app_id\":7},\"junk\",{\"app_id\":\"org.kde.krita\",\"installs_last_month\":-3}]}");
        Assert.Equal([new StoreListing("org.mozilla.firefox", 501234), new StoreListing("org.kde.krita")], collection);
        Assert.Throws<InvalidDataException>(() => OnlineStoreData.ParseCollection("[]"));

        var picks = OnlineStoreData.ParsePicks(
            "{\"apps\":[{\"app_id\":\"b\",\"position\":2},{\"app_id\":\"a\",\"position\":1},{\"position\":3},{\"app_id\":\"c\"}]}");
        Assert.Equal(["a", "b", "c"], picks.Select(p => p.AppId));

        Assert.Equal(
            new InstallStats(1775099, 34800, 7585),
            OnlineStoreData.ParseStats("{\"installs_total\":1775099,\"installs_per_day\":{},\"installs_last_month\":34800,\"installs_last_7_days\":7585,\"id\":\"org.kde.krita\"}"));
        Assert.Null(OnlineStoreData.ParseStats("{\"detail\":\"Not Found\"}"));
        Assert.Null(OnlineStoreData.ParseStats("not json"));
    }

    [Fact]
    public void Parses_ratings()
    {
        var ratings = OnlineStoreData.ParseRatings(
            "{\"org.kde.krita\":{\"star0\":0,\"star1\":92,\"star2\":32,\"star3\":35,\"star4\":70,\"star5\":272,\"total\":501}," +
            "\"empty\":{\"star1\":0},\"odd\":\"text\",\"negative\":{\"star5\":-4,\"star4\":1}}");

        Assert.Equal(new StarRating(92, 32, 35, 70, 272), ratings["ORG.KDE.KRITA"]);
        Assert.Equal(1, ratings["negative"].Total);
        Assert.Equal(2, ratings.Count);
    }

    [Fact]
    public void Parses_reviews_and_drops_reported_or_empty_ones()
    {
        var reviews = OnlineStoreData.ParseReviews("""
            [
              {"rating":"100","summary":"Great","description":"Does what I need.","user_display":"Ada","date_created":"1789773327.0","version":"6.0","distro":"Fedora Linux","reported":"0"},
              {"rating":20,"summary":"Be better","description":"","date_created":1700000000},
              {"rating":"80","summary":"Reported","description":"spam","reported":"3"},
              {"rating":"60","summary":"","description":""},
              {"rating":"0","summary":"No stars"},
              {"summary":"No rating"},
              "junk"
            ]
            """);

        Assert.Equal(2, reviews.Count);
        Assert.Equal(new AppReview(5, "Great", "Does what I need.", "Ada",
            DateTimeOffset.FromUnixTimeSeconds(1789773327), "6.0", "Fedora Linux"), reviews[0]);
        Assert.Equal(1, reviews[1].Stars);
        Assert.Null(reviews[1].Text);
        Assert.Empty(OnlineStoreData.ParseReviews("{}"));
    }

    [Fact]
    public async Task Lists_are_fetched_once_a_day_and_the_old_copy_survives_an_outage()
    {
        var cache = new MemoryCache();
        var clock = new Clock();
        var online = true;
        var web = new FakeWeb((request, _) => !online ? (HttpStatusCode.ServiceUnavailable, "")
            : request.RequestUri!.AbsolutePath.Contains("app-picks") ? (HttpStatusCode.OK, "{\"apps\":[{\"app_id\":\"pick\",\"position\":1}]}")
            : request.RequestUri.AbsolutePath.Contains("trending") ? (HttpStatusCode.InternalServerError, "")
            : (HttpStatusCode.OK, "{\"hits\":[{\"app_id\":\"a\",\"installs_last_month\":5}]}"));
        using var data = new OnlineStoreData(cache, "Fedora Linux", handler: web, time: clock);

        var first = await data.GetCollectionsAsync(Token);
        Assert.Equal(["pick"], first.Picks.Select(p => p.AppId));
        // One list failing leaves the others intact.
        Assert.Empty(first.Trending);
        Assert.Equal(5, Assert.Single(first.MostInstalled).InstallsLastMonth);
        Assert.Single(first.RecentlyUpdated);
        Assert.Equal(4, web.Requests.Count);
        Assert.All(web.Requests, r => Assert.StartsWith("https://flathub.org/api/v2/", r.Url));
        Assert.Contains(web.Requests, r => r.Url.EndsWith("/apps-of-the-week/2026-10-13", StringComparison.Ordinal));

        await data.GetCollectionsAsync(Token);
        Assert.Equal(5, web.Requests.Count); // only the list that failed is asked for again

        clock.Now += TimeSpan.FromHours(25);
        online = false;
        var later = await data.GetCollectionsAsync(Token);
        Assert.Single(later.MostInstalled);
        Assert.Empty(later.Trending);
    }

    [Fact]
    public async Task Install_counts_are_asked_once_per_app_and_only_for_well_formed_ids()
    {
        var web = new FakeWeb((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/org.kde.krita", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, "{\"installs_total\":10,\"installs_last_month\":2,\"installs_last_7_days\":1}")
            : (HttpStatusCode.NotFound, "{\"detail\":\"Not Found\"}"));
        using var data = new OnlineStoreData(null, null, handler: web);

        Assert.Equal(new InstallStats(10, 2, 1), await data.GetInstallStatsAsync("org.kde.krita", Token));
        Assert.Equal(new InstallStats(10, 2, 1), await data.GetInstallStatsAsync("org.kde.krita", Token));
        Assert.Null(await data.GetInstallStatsAsync("firefox.desktop", Token));
        Assert.Null(await data.GetInstallStatsAsync("../../admin?x=1", Token));
        Assert.Null(await data.GetInstallStatsAsync("", Token));
        Assert.Equal(2, web.Requests.Count);
    }

    [Fact]
    public async Task Without_a_kept_copy_an_outage_is_an_error()
    {
        using var data = new OnlineStoreData(
            new MemoryCache(), null, handler: new FakeWeb((_, _) => (HttpStatusCode.ServiceUnavailable, "")));

        await Assert.ThrowsAsync<HttpRequestException>(() => data.GetRatingsAsync(Token));
    }

    [Fact]
    public async Task Reviews_are_asked_for_under_each_id_with_a_throwaway_asker_id()
    {
        var web = new FakeWeb((_, body) => body.Contains("\"app_id\":\"org.kde.krita.desktop\"")
            ? (HttpStatusCode.OK, "[{\"rating\":\"80\",\"summary\":\"Nice\"}]")
            : (HttpStatusCode.OK, "[]"));
        using var data = new OnlineStoreData(null, "Fedora Linux", new System.Globalization.CultureInfo("de-DE"), web);

        var reviews = await data.GetReviewsAsync(["org.kde.krita", "org.kde.krita.desktop", "never.asked", "nor.this"], Token);

        Assert.Equal(4, Assert.Single(reviews).Stars);
        Assert.Equal(2, web.Requests.Count);
        Assert.All(web.Requests, r => Assert.Equal(OnlineStoreData.ReviewsUrl, r.Url));
        Assert.Contains("\"locale\":\"de_DE\"", web.Requests[0].Body);
        Assert.Contains("\"distro\":\"Fedora Linux\"", web.Requests[0].Body);
        string Hash(string body) => System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("user_hash").GetString()!;
        Assert.Equal(40, Hash(web.Requests[0].Body).Length);
        Assert.NotEqual(Hash(web.Requests[0].Body), Hash(web.Requests[1].Body));
    }
}
