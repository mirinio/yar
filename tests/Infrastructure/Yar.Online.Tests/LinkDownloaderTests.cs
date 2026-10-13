using System.Net;
using System.Net.Http.Headers;
using Yar.Core;

namespace Yar.Online.Tests;

public sealed class LinkDownloaderTests : IDisposable
{
    private sealed class Web(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.AbsoluteUri);
            var response = respond(request.RequestUri);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private readonly string _directory = Directory.CreateTempSubdirectory("yar-downloads-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static HttpResponseMessage File_(byte[] content, string type = "application/octet-stream", string? attachment = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        if (attachment is not null)
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = attachment };
        return response;
    }

    private static HttpResponseMessage Forward(string to)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Theory]
    [InlineData("https://example.org/app.AppImage", true)]
    [InlineData("  https://example.org/a%20b.zip  ", true)]
    [InlineData("http://example.org/app.AppImage", false)]
    [InlineData("ftp://example.org/app", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://user:secret@example.org/x", false)]
    [InlineData("https://example.org/a b", false)]
    [InlineData("example.org/app", false)]
    public void Only_plain_https_links_are_accepted(string text, bool accepted)
    {
        var (link, problem) = DownloadLink.Check(text);

        Assert.Equal(accepted, link is not null);
        Assert.Equal(accepted, problem is null);
    }

    [Fact]
    public async Task A_link_is_followed_to_the_file_and_downloaded_under_a_safe_name()
    {
        var content = "the app"u8.ToArray();
        var web = new Web(url => url.Host == "example.org"
            ? Forward("https://cdn.example.net/files/Cool%20App-1.0.AppImage?token=1")
            : File_(content));
        using var downloader = new LinkDownloader(_directory, web);
        var seen = new List<long>();

        var info = await downloader.ProbeAsync(new Uri("https://example.org/get"), Token);
        var path = await downloader.DownloadAsync(info, new Collect(seen), Token);

        Assert.Equal("cdn.example.net", info.Address.Host);
        Assert.Equal("Cool App-1.0.AppImage", info.FileName);
        Assert.Equal(content.Length, info.Bytes);
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal(_directory, Path.GetDirectoryName(Path.GetDirectoryName(path)));
        Assert.Equal(content.Length, seen[^1]);

        downloader.Discard(path);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData("../../.bashrc", "bashrc")]
    [InlineData("/etc/cron.d/evil", "evil")]
    [InlineData("..\\..\\evil.zip", "evil.zip")]
    [InlineData("-rf", "rf")]
    public async Task The_server_cannot_choose_where_the_file_lands(string offered, string kept)
    {
        using var downloader = new LinkDownloader(_directory, new Web(_ => File_([1, 2, 3], attachment: offered)));

        var info = await downloader.ProbeAsync(new Uri("https://example.org/x"), Token);
        var path = await downloader.DownloadAsync(info, new Collect([]), Token);

        Assert.Equal(kept, Path.GetFileName(path));
        Assert.StartsWith(_directory + "/", path);
    }

    [Fact]
    public async Task Forwarding_to_an_unprotected_address_is_refused()
    {
        var web = new Web(_ => Forward("http://plain.example.net/app.AppImage"));
        using var downloader = new LinkDownloader(_directory, web);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.ProbeAsync(new Uri("https://example.org/get"), Token));

        Assert.Contains("plain.example.net", error.Message);
        Assert.Single(web.Asked);
    }

    [Fact]
    public async Task Web_pages_missing_files_and_endless_forwarding_are_explained()
    {
        using var page = new LinkDownloader(_directory, new Web(_ => File_("<html>"u8.ToArray(), "text/html")));
        using var missing = new LinkDownloader(_directory, new Web(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var loop = new LinkDownloader(_directory, new Web(_ => Forward("/again")));
        var link = new Uri("https://example.org/x");

        Assert.Contains("web page", (await Assert.ThrowsAsync<InvalidOperationException>(() => page.ProbeAsync(link, Token))).Message);
        Assert.Contains("not found", (await Assert.ThrowsAsync<InvalidOperationException>(() => missing.ProbeAsync(link, Token))).Message);
        Assert.Contains("on and on", (await Assert.ThrowsAsync<InvalidOperationException>(() => loop.ProbeAsync(link, Token))).Message);
    }

    [Fact]
    public async Task Discarding_only_ever_touches_what_was_downloaded()
    {
        using var downloader = new LinkDownloader(_directory, new Web(_ => File_([1, 2, 3])));
        var path = await downloader.DownloadAsync(new(new Uri("https://example.org/x.zip"), "x.zip", 3), new Collect([]), Token);

        downloader.Discard("/etc/hostname");
        downloader.Discard(_directory);

        Assert.True(File.Exists("/etc/hostname"));
        Assert.True(File.Exists(path));
    }

    private sealed class Collect(List<long> into) : IProgress<long>
    {
        public void Report(long value) => into.Add(value);
    }
}
