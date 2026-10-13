using System.Net;
using Yar.Core;

namespace Yar.Backends.Snap.Tests;

public class SnapTests
{
    // Trimmed to the fields Yar reads; same structure as a real `GET /v2/snaps` answer.
    private const string Response = """
        {
          "type": "sync", "status-code": 200, "status": "OK",
          "result": [
            {
              "name": "firefox", "title": "Firefox", "summary": "Mozilla Firefox web browser",
              "version": "131.0-2", "revision": "5091", "type": "app",
              "channel": "latest/stable", "tracking-channel": "latest/stable/ubuntu-24.04",
              "publisher": { "username": "mozilla", "display-name": "Mozilla", "validation": "verified" },
              "confinement": "strict", "installed-size": 283000832, "install-date": "2026-09-30T08:12:44Z",
              "apps": [
                { "snap": "firefox", "name": "firefox", "desktop-file": "/var/lib/snapd/desktop/applications/firefox_firefox.desktop" },
                { "snap": "firefox", "name": "geckodriver" }
              ]
            },
            { "name": "core22", "type": "base", "version": "20260101", "revision": "1612" },
            { "name": "snapd", "type": "snapd", "version": "2.66", "revision": "21759" },
            { "name": "hello", "type": "app", "version": "2.10", "revision": 42, "publisher": { "username": "canonical" } },
            { "type": "app" },
            "garbage"
          ]
        }
        """;

    private sealed class FakeSnapd(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestedPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestedPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Parses_only_app_snaps()
    {
        var snaps = SnapdJson.ParseInstalledApps(Response);

        Assert.Equal(["firefox", "hello"], snaps.Select(s => s.Name));
        Assert.Equal("Mozilla", snaps[0].Publisher);
        Assert.Equal("latest/stable/ubuntu-24.04", snaps[0].Channel);
        Assert.Equal(283000832, snaps[0].InstalledSizeBytes);
        Assert.Equal(["/var/lib/snapd/desktop/applications/firefox_firefox.desktop"], snaps[0].DesktopFiles);
        Assert.Equal("42", snaps[1].Revision);
        Assert.Equal("canonical", snaps[1].Publisher);
        Assert.Null(snaps[1].InstalledSizeBytes);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"result\": \"nope\"}")]
    public void Unexpected_answers_are_rejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => SnapdJson.ParseInstalledApps(json));
    }

    [Fact]
    public async Task Claims_launchers_named_by_snapd()
    {
        var handler = new FakeSnapd(HttpStatusCode.OK, Response);
        using var provider = new SnapProvider("/unused", handler);
        var path = "/var/lib/snapd/desktop/applications/firefox_firefox.desktop";
        var index = new DesktopEntryIndex(
        [
            new DesktopEntry(path, "firefox_firefox.desktop", "Application", "Firefox Web Browser", null, null,
                "/snap/firefox/5091/default256.png", "firefox %u", null, false, false, [],
                new Dictionary<string, string> { ["X-SnapInstanceName"] = "firefox" }),
        ]);

        var apps = await provider.ClaimAppsAsync(index, Token);

        Assert.Equal("/v2/snaps", handler.RequestedPath);
        Assert.Equal(2, apps.Count);
        Assert.Equal("Firefox", apps[0].Name);
        Assert.Equal([path], apps[0].LauncherPaths);
        Assert.Equal("/snap/firefox/5091/default256.png", apps[0].IconPath);
        Assert.Null(apps[0].IconName);
        Assert.Null(apps[0].InstalledAt);
        Assert.Equal("2026-09-30T08:12:44Z", apps[0].Technical["refreshed"]);
        Assert.Equal(SnapProvider.StoreSourceId, apps[0].SourceId);
        Assert.Empty(apps[1].LauncherPaths);
    }

    [Fact]
    public async Task Lists_snaps_with_a_newer_revision()
    {
        var handler = new FakeSnapd(HttpStatusCode.OK,
            "{\"type\":\"sync\",\"result\":[{\"name\":\"firefox\",\"title\":\"Firefox\",\"version\":\"132.0\"},{\"name\":\"core22\",\"version\":\"20261001\"},{}]}");
        using var provider = new SnapProvider("/unused", handler);
        AppInfo[] installed =
        [
            new(SnapProvider.ProviderId, "firefox", "Firefox Browser", null, null, null, "131.0-2", SnapProvider.StoreSourceId,
                null, null, [], null, new Dictionary<string, string>()),
        ];

        var check = await provider.GetUpdatesAsync(installed, Token);

        Assert.Equal("/v2/find", handler.RequestedPath);
        Assert.Equal(2, check.Updates.Count);
        Assert.Equal("firefox", check.Updates[0].AppId);
        Assert.Equal("Firefox Browser", check.Updates[0].Name);
        Assert.Equal("131.0-2", check.Updates[0].CurrentVersion);
        Assert.Equal("132.0", check.Updates[0].NewVersion);
        Assert.Null(check.Updates[1].AppId);
    }

    [Fact]
    public async Task An_http_error_is_an_error_not_an_empty_list()
    {
        using var provider = new SnapProvider("/unused", new FakeSnapd(HttpStatusCode.InternalServerError, "{}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ClaimAppsAsync(DesktopEntryIndex.Empty, Token));
    }

    [Fact]
    public async Task Reports_not_installed_when_the_socket_is_missing()
    {
        using var provider = new SnapProvider("/nonexistent/snapd.socket");

        Assert.Equal(ProviderStatus.NotInstalled, await provider.ProbeAsync(Token));
    }
}
