namespace Yar.Core.Tests;

public class StoreTests
{
    private static CatalogEntry Entry(
        string provider, string source, string id, string name = "App", string[]? otherIds = null,
        bool verified = false, string? description = null, string[]? categories = null) => new(
        provider, source, id, name, Summary: $"{name} summary", description, Developer: null, License: null,
        Homepage: null, IconPath: null, IconName: null, categories ?? [], otherIds ?? [], PackageName: null,
        Version: null, verified, VerifiedBy: verified ? "example.org" : null);

    [Fact]
    public void The_same_app_from_two_sources_is_listed_once()
    {
        var apps = StoreCatalog.Merge(
        [
            Entry("packagekit", "fedora", "org.kde.krita.desktop", "Krita", description: "short"),
            Entry("flatpak", "system:flathub", "org.kde.krita", "Krita", verified: true, description: "a longer one"),
            Entry("flatpak", "system:flathub", "org.gimp.GIMP", "GIMP"),
        ]);

        Assert.Equal(2, apps.Count);
        var krita = apps.Single(a => a.Name == "Krita");
        Assert.Equal("org.kde.krita", krita.Id);
        Assert.Equal("a longer one", krita.Description);
        Assert.Equal(["packagekit", "flatpak"], krita.Offers.Select(o => o.ProviderId));
        Assert.Single(apps.Single(a => a.Name == "GIMP").Offers);
    }

    [Fact]
    public void Apps_are_joined_through_the_ids_they_provide_not_through_their_names()
    {
        var apps = StoreCatalog.Merge(
        [
            Entry("packagekit", "fedora", "firefox.desktop", "Firefox"),
            Entry("flatpak", "system:flathub", "org.mozilla.firefox", "Firefox", otherIds: ["firefox.desktop"]),
            // Same name, unrelated IDs: a different app.
            Entry("flatpak", "system:other", "com.example.Firefox", "Firefox"),
            // Joined to the first two only through a chain of IDs.
            Entry("flatpak", "user:beta", "org.mozilla.firefox.beta", "Firefox Beta", otherIds: ["org.mozilla.firefox"]),
        ]);

        Assert.Equal(2, apps.Count);
        var firefox = apps.Single(a => a.Offers.Count == 3);
        Assert.Contains("firefox", firefox.KnownIds);
        Assert.Contains("org.mozilla.firefox", firefox.KnownIds);
        Assert.Equal("com.example.Firefox", apps.Single(a => a.Offers.Count == 1).Id);
    }

    [Fact]
    public void The_same_source_listing_an_app_twice_is_one_offer()
    {
        var app = Assert.Single(StoreCatalog.Merge(
        [
            Entry("packagekit", "fedora", "a.desktop"),
            Entry("packagekit", "fedora", "a.desktop"),
        ]));

        Assert.Single(app.Offers);
    }

    [Fact]
    public void Suggests_a_verified_flatpak_then_the_distribution_then_the_best_known_source()
    {
        TrustLevel Trust(StoreOffer offer) => offer.SourceId switch
        {
            "fedora" => TrustLevel.Distro,
            "system:flathub" => TrustLevel.WellKnown,
            _ => TrustLevel.Unknown,
        };
        StoreApp App(params CatalogEntry[] entries) => Assert.Single(StoreCatalog.Merge(entries));

        var verified = App(Entry("packagekit", "fedora", "a"), Entry("flatpak", "system:flathub", "a", verified: true));
        Assert.Equal("flatpak", StoreCatalog.Suggest(verified, Trust)?.ProviderId);

        var unverified = App(Entry("flatpak", "system:flathub", "a"), Entry("packagekit", "fedora", "a"));
        Assert.Equal("packagekit", StoreCatalog.Suggest(unverified, Trust)?.ProviderId);

        var noDistro = App(Entry("packagekit", "some-vendor", "a"), Entry("flatpak", "system:flathub", "a"));
        Assert.Equal("system:flathub", StoreCatalog.Suggest(noDistro, Trust)?.SourceId);
    }

    [Fact]
    public void Recognises_installed_apps_by_component_or_launcher()
    {
        var store = Assert.Single(StoreCatalog.Merge(
            [Entry("flatpak", "system:flathub", "org.mozilla.firefox", otherIds: ["firefox.desktop"])]));
        AppInfo Installed(string? component, params string[] launchers) => new(
            "packagekit", "x", "X", null, null, null, null, null, null, null, launchers, component,
            new Dictionary<string, string>());

        Assert.NotNull(StoreCatalog.InstalledMatch(store, [Installed("org.mozilla.firefox")]));
        Assert.NotNull(StoreCatalog.InstalledMatch(store, [Installed(null, "/usr/share/applications/firefox.desktop")]));
        Assert.Null(StoreCatalog.InstalledMatch(store, [Installed("org.kde.krita", "/usr/share/applications/krita.desktop")]));
    }

    [Fact]
    public void Search_puts_name_matches_first_and_respects_the_limit()
    {
        var apps = StoreCatalog.Merge(
        [
            Entry("flatpak", "s", "a", "Paint Tool"),
            Entry("flatpak", "s", "b", "Krita", description: null),
            Entry("flatpak", "s", "c", "MyPaint"),
            Entry("flatpak", "s", "d", "Chess", categories: ["Game"]),
        ]);
        string Name(AppCategory c) => c == AppCategory.Games ? "Games" : c.ToString();

        Assert.Equal(["Paint Tool", "MyPaint"], StoreCatalog.Search(apps, "paint", Name, 10).Select(a => a.Name));
        Assert.Equal(["Paint Tool"], StoreCatalog.Search(apps, "paint", Name, 1).Select(a => a.Name));
        Assert.Equal(["Chess"], StoreCatalog.Search(apps, "games", Name, 10).Select(a => a.Name));
        Assert.Equal(["Krita"], StoreCatalog.Search(apps, "krita summ", Name, 10).Select(a => a.Name));
        Assert.Empty(StoreCatalog.Search(apps, "  ", Name, 10));
    }

    [Fact]
    public void Star_ratings_add_up_and_average()
    {
        var rating = new StarRating(1, 0, 0, 0, 1).Plus(new StarRating(0, 0, 2, 0, 0));

        Assert.Equal(4, rating.Total);
        Assert.Equal(3.0, rating.Average);
        Assert.Null(new StarRating(0, 0, 0, 0, 0).Average);
    }

    [Theory]
    [InlineData("https://dl.flathub.org/repo/flathub.flatpakrepo", null, "flathub")]
    [InlineData("  https://example.org/My%20Repo.FLATPAKREPO  ", null, "my-repo")]
    [InlineData("https://example.org/repo.flatpakrepo", "My Own Source!", "my-own-source")]
    [InlineData("https://example.org/-.flatpakrepo", null, "example-org")]
    public void Accepts_https_flatpakrepo_addresses(string address, string? name, string expectedName)
    {
        var (request, problem) = SourceAddress.Check(address, name);

        Assert.Null(problem);
        Assert.Equal(expectedName, request?.Name);
        Assert.StartsWith("https://", request?.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("flathub")]
    [InlineData("http://example.org/repo.flatpakrepo")]
    [InlineData("file:///etc/passwd.flatpakrepo")]
    [InlineData("https://example.org/repo")]
    [InlineData("https://example.org/repo.flatpakrepo.exe")]
    [InlineData("https://user:secret@example.org/repo.flatpakrepo")]
    [InlineData("https://example.org/re po.flatpakrepo")]
    [InlineData("https://example.org/repo.flatpakrepo\n--system")]
    [InlineData("--from https://example.org/repo.flatpakrepo")]
    [InlineData("https:///repo.flatpakrepo")]
    public void Refuses_other_addresses_with_a_reason(string? address)
    {
        var (request, problem) = SourceAddress.Check(address);

        Assert.Null(request);
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Theory]
    [InlineData("--system", "system")]
    [InlineData("a  b//c", "a-b-c")]
    [InlineData("ÄÖ", "")]
    public void Source_names_are_made_safe(string name, string expected)
    {
        Assert.Equal(expected, SourceAddress.SanitizeName(name));
    }
}

public class ThirdPartySummaryTests
{
    private static ThirdPartySummary Summary(ThirdPartyChoice choice, params (string Name, bool? On)[] sources) =>
        ThirdPartySummary.From(
            new(choice, sources.Select(s => (s.Name, "dnf")).ToList()),
            source => sources.First(s => s.Name == source.Name).On);

    [Fact]
    public void A_recorded_answer_decides_the_switch()
    {
        Assert.True(Summary(ThirdPartyChoice.Allowed, ("chrome", false), ("steam", false)).ShownOn);
        Assert.False(Summary(ThirdPartyChoice.Declined, ("chrome", true), ("steam", true)).ShownOn);
    }

    [Fact]
    public void Says_what_an_answer_would_change_and_what_is_already_so()
    {
        var summary = Summary(ThirdPartyChoice.NotChosen, ("chrome", false), ("steam", true), ("nvidia", null));

        var yes = summary.Effect(allow: true);
        Assert.Equal(["chrome", "nvidia"], yes.Changing);
        Assert.Equal(["steam"], yes.Already);

        var no = summary.Effect(allow: false);
        Assert.Equal(["steam"], no.Changing);
        Assert.Equal(["chrome", "nvidia"], no.Already);
    }

    [Fact]
    public void Without_a_recorded_answer_the_switch_shows_what_is_really_set()
    {
        var allOn = Summary(ThirdPartyChoice.NotChosen, ("chrome", true), ("steam", true));
        Assert.True(allOn.ShownOn);
        Assert.False(allOn.Mixed);

        var some = Summary(ThirdPartyChoice.NotChosen, ("chrome", false), ("steam", true), ("nvidia", null));
        Assert.False(some.ShownOn);
        Assert.True(some.Mixed);
        Assert.Equal(["steam"], some.On);
        Assert.Equal(["chrome", "nvidia"], some.Off);

        Assert.False(Summary(ThirdPartyChoice.NotChosen, ("chrome", false)).ShownOn);
        Assert.False(Summary(ThirdPartyChoice.NotChosen).ShownOn);
    }
}

public class StoreHelperTests
{
    [Fact]
    public void Picks_the_thumbnail_closest_to_the_shown_size()
    {
        var chosen = ScreenshotUrls.Choose(
        [
            ("https://dl.flathub.org/media/a/image-1_orig.png", "source", 1920),
            ("https://dl.flathub.org/media/a/image-1_1248x677@1.png", "thumbnail", 1248),
            ("https://dl.flathub.org/media/a/image-1_752x408@1.png", "thumbnail", 752),
            ("https://dl.flathub.org/media/a/image-1_224x121@1.png", "thumbnail", 224),
        ]);

        Assert.Equal("https://dl.flathub.org/media/a/image-1_752x408@1.png", chosen);
    }

    [Fact]
    public void Plain_http_is_upgraded_and_unknown_hosts_are_dropped()
    {
        var fedora = ScreenshotUrls.Choose(
        [
            ("http://tangramgames.dk/games/header.png", "source", null),
            ("http://dl.fedoraproject.org/pub/alt/screenshots/f44/624x351/x.png", "thumbnail", 624),
        ]);
        Assert.Equal("https://dl.fedoraproject.org/pub/alt/screenshots/f44/624x351/x.png", fedora);

        // Only the maker's own server offers this picture: nothing is chosen.
        Assert.Null(ScreenshotUrls.Choose([("https://example.org/shot.png", "source", 1000)]));
        // The original is used when an accepted host has no thumbnail.
        Assert.Equal(
            "https://dl.flathub.org/o.png",
            ScreenshotUrls.Choose([("https://dl.flathub.org/o.png", "source", 4000)]));
    }

    [Theory]
    [InlineData("https://dl.flathub.org/media/x.png", true)]
    [InlineData("http://dl.flathub.org/media/x.png", false)]
    [InlineData("https://dl.flathub.org.evil.example/x.png", false)]
    [InlineData("https://user@dl.flathub.org/x.png", false)]
    [InlineData("https://dl.flathub.org:8443/x.png", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Only_https_addresses_on_accepted_hosts_are_allowed(string? url, bool expected)
    {
        Assert.Equal(expected, ScreenshotUrls.IsAllowed(url));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1k")]
    [InlineData(2625, "2.6k")]
    [InlineData(34800, "35k")]
    [InlineData(501234, "501k")]
    [InlineData(1775099, "1.8M")]
    [InlineData(25000000, "25M")]
    public void Writes_big_counts_compactly(long value, string expected)
    {
        Assert.Equal(expected, CompactNumber.Format(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Guesses_languages_from_developer_tools()
    {
        var directory = Directory.CreateTempSubdirectory("yar-tools-").FullName;
        try
        {
            foreach (var tool in new[] { "dotnet", "node", "python3", "gcc" })
                File.WriteAllText(Path.Combine(directory, tool), "");

            Assert.Equal(["C#", "JavaScript", "TypeScript"], DevLanguages.Detect(["relative", directory, "/nonexistent"]));
            Assert.Empty(DevLanguages.Detect([]));
            Assert.Equal("csharp", DevLanguages.QueryFor("C#"));
            Assert.Null(DevLanguages.QueryFor("csharp; drop"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_category_lists_its_apps_most_installed_first()
    {
        CatalogEntry Entry(string id, string name, params string[] categories) => new(
            "flatpak", "s", id, name, null, null, null, null, null, null, null, categories, [], null, null, false, null)
        {
            Screenshots = id == "b" ? ["https://dl.flathub.org/b.png"] : [],
        };
        var apps = StoreCatalog.Merge(
        [
            Entry("a", "Alpha", "Game"), Entry("b", "Beta", "Game"), Entry("c", "Chess", "Game"), Entry("d", "Docs", "Office"),
        ]);

        var games = StoreCatalog.InCategory(apps, AppCategory.Games, app => app.Id == "c" ? 900 : app.Id == "b" ? 5 : null);

        Assert.Equal(["Chess", "Beta", "Alpha"], games.Select(a => a.Name));
        Assert.Equal(["https://dl.flathub.org/b.png"], games[1].Screenshots);
    }
}
