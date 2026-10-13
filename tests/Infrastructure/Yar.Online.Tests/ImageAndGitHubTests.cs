using System.Net;
using System.Net.Http.Headers;
using Yar.Core;

namespace Yar.Online.Tests;

public sealed class ImageAndGitHubTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("yar-images-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Web(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class MemoryCache : ICacheStore
    {
        public Dictionary<string, string> Entries { get; } = [];
        public string? Read(string name) => Entries.GetValueOrDefault(name);
        public void Write(string name, string content) => Entries[name] = content;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HttpResponseMessage Image(byte[] bytes, string type = "image/png") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(type) } } };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Url = "https://dl.flathub.org/media/org/kde/krita/abc/screenshots/image-1_752x408@1.png";

    [Fact]
    public async Task A_picture_is_downloaded_once_and_then_read_from_disk()
    {
        var web = new Web(_ => Image([1, 2, 3]));
        using var client = new HttpClient(web);
        var store = new ImageStore(Path.Combine(_root, "images"));

        Assert.Equal([1, 2, 3], await store.GetAsync(client, Url, Token));
        Assert.Equal([1, 2, 3], await store.GetAsync(client, Url, Token));

        Assert.Single(web.Requests);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "images")));
    }

    [Fact]
    public async Task Pictures_from_elsewhere_of_the_wrong_kind_or_too_big_are_refused()
    {
        var store = new ImageStore(_root);

        var untouched = new Web(_ => Image([1]));
        using (var client = new HttpClient(untouched))
        {
            Assert.Null(await store.GetAsync(client, "https://evil.example/shot.png", Token));
            Assert.Null(await store.GetAsync(client, "http://dl.flathub.org/media/shot.png", Token));
            Assert.Null(await store.GetAsync(client, "file:///etc/passwd", Token));
        }
        Assert.Empty(untouched.Requests);

        using (var client = new HttpClient(new Web(_ => Image([1], "text/html"))))
            Assert.Null(await store.GetAsync(client, Url, Token));
        using (var client = new HttpClient(new Web(_ => Image([1], "image/svg+xml"))))
            Assert.Null(await store.GetAsync(client, Url, Token));
        using (var client = new HttpClient(new Web(_ => Image(new byte[ImageStore.MaxImageBytes + 1]))))
            Assert.Null(await store.GetAsync(client, Url, Token));
        using (var client = new HttpClient(new Web(_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
            Assert.Null(await store.GetAsync(client, Url, Token));
        // The server sent us on to a host that is not accepted.
        using (var client = new HttpClient(new Web(_ =>
        {
            var response = Image([1]);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/elsewhere.png");
            return response;
        })))
        {
            Assert.Null(await store.GetAsync(client, Url, Token));
        }

        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task The_folder_is_kept_under_its_limit()
    {
        var web = new Web(_ => Image(new byte[400]));
        using var client = new HttpClient(web);
        var store = new ImageStore(_root, maxTotalBytes: 1000);

        for (var i = 0; i < 5; i++)
            await store.GetAsync(client, $"https://dl.flathub.org/media/{i}.png", Token);

        Assert.InRange(Directory.EnumerateFiles(_root).Count(), 1, 2);
    }

    private const string Search = """
        {"total_count":2,"items":[
          {"full_name":"ada/engine","html_url":"https://github.com/ada/engine","description":"A fast engine","language":"C#","stargazers_count":2625},
          {"full_name":"bob/tool","html_url":"https://github.com/bob/tool","description":null,"language":null,"stargazers_count":10},
          {"full_name":"eve/trap","html_url":"https://evil.example/eve/trap","stargazers_count":99999},
          {"full_name":"no/url"},
          "junk"
        ]}
        """;

    [Fact]
    public void Parses_projects_and_only_keeps_links_to_github()
    {
        var projects = GitHubFeed.Parse(Search);

        Assert.Equal(2, projects.Count);
        Assert.Equal(new DevProject("ada/engine", "A fast engine", "C#", 2625, "https://github.com/ada/engine"), projects[0]);
        Assert.Null(projects[1].Description);
        Assert.Empty(GitHubFeed.Parse("{\"message\":\"rate limited\"}"));
    }

    [Fact]
    public async Task Asks_once_per_chosen_language_and_keeps_the_answer_for_hours()
    {
        var cache = new MemoryCache();
        var clock = new Clock();
        var web = new Web(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Search) });
        using var feed = new GitHubFeed(cache, web, clock);

        var projects = await feed.GetTrendingAsync(["C#", "Rust", "C#", "Klingon"], FeedPeriod.Month, Token);

        Assert.Equal(2, web.Requests.Count);
        Assert.All(web.Requests, r => Assert.StartsWith("https://api.github.com/search/repositories?q=", r));
        Assert.Contains(web.Requests, r => r.Contains("language%3Acsharp") && r.Contains("created%3A%3E2026-09-13"));
        Assert.Contains(web.Requests, r => r.Contains("language%3Arust"));
        // The same project found under two languages is listed once, most stars first.
        Assert.Equal(["ada/engine", "bob/tool"], projects.Select(p => p.FullName));

        await feed.GetTrendingAsync(["C#", "Rust"], FeedPeriod.Month, Token);
        Assert.Equal(2, web.Requests.Count);

        clock.Now += TimeSpan.FromHours(7);
        await feed.GetTrendingAsync(["C#"], FeedPeriod.Month, Token);
        Assert.Equal(3, web.Requests.Count);
    }

    [Fact]
    public async Task Each_period_looks_back_its_own_distance_and_is_kept_separately()
    {
        var cache = new MemoryCache();
        var web = new Web(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Search) });
        using var feed = new GitHubFeed(cache, web, new Clock());

        await feed.GetTrendingAsync(["Rust"], FeedPeriod.Week, Token);
        await feed.GetTrendingAsync(["Rust"], FeedPeriod.Year, Token);
        await feed.GetTrendingAsync(["Rust"], FeedPeriod.Week, Token);

        Assert.Equal(2, web.Requests.Count);
        Assert.Contains("created%3A%3E2026-10-06", web.Requests[0]);
        Assert.Contains("created%3A%3E2025-10-13", web.Requests[1]);
        Assert.Equal(["github-rust-365.json", "github-rust-7.json"], cache.Entries.Keys.Order());
    }

    [Fact]
    public async Task Being_told_to_slow_down_shows_what_was_kept_or_nothing()
    {
        var cache = new MemoryCache();
        var clock = new Clock();
        var limited = false;
        var web = new Web(_ => limited
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Search) });
        using var feed = new GitHubFeed(cache, web, clock);

        limited = true;
        Assert.Empty(await feed.GetTrendingAsync(["Go"], FeedPeriod.Month, Token));

        limited = false;
        Assert.Equal(2, (await feed.GetTrendingAsync(["Go"], FeedPeriod.Month, Token)).Count);

        limited = true;
        clock.Now += TimeSpan.FromHours(7);
        Assert.Equal(2, (await feed.GetTrendingAsync(["Go"], FeedPeriod.Month, Token)).Count);
    }

    [Fact]
    public async Task At_most_eight_languages_are_asked_about()
    {
        var web = new Web(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[]}") });
        using var feed = new GitHubFeed(null, web);

        await feed.GetTrendingAsync(DevLanguages.All.Select(l => l.Name).ToList(), FeedPeriod.Month, Token);

        Assert.Equal(GitHubFeed.MaxLanguages, web.Requests.Count);
    }
}
