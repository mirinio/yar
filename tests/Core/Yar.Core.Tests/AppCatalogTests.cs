namespace Yar.Core.Tests;

public class AppCatalogTests
{
    private static DesktopEntry Launcher(string path) => new(
        path, Path.GetFileName(path), "Application", Path.GetFileNameWithoutExtension(path),
        null, null, null, null, null, false, false, [], new Dictionary<string, string>());

    private static AppInfo App(string provider, string id, string? launcher = null, string? componentId = null,
        string? name = null) => new(
        provider, id, name ?? id, null, null, null, null, null, null, null,
        launcher is null ? [] : [launcher], componentId, new Dictionary<string, string>());

    private static SourceInfo Source(string provider, string id) => new(
        provider, id, id, null, null, true, SourceScope.System, TrustLevel.Unknown,
        new Dictionary<string, string>());

    private class FakeProvider(string id) : ISoftwareProvider
    {
        public string Id => id;
        public ProviderFacts Facts => new(id, null, []);
        public ProviderStatus Status { get; init; } = ProviderStatus.Available;
        public Func<IReadOnlyList<SourceInfo>> Sources { get; init; } = () => [];
        public Func<DesktopEntryIndex, IReadOnlyList<AppInfo>> Claim { get; init; } = _ => [];
        public List<string> Seen { get; } = [];

        public Task<ProviderStatus> ProbeAsync(CancellationToken ct) => Task.FromResult(Status);

        public Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken ct) => Task.FromResult(Sources());

        public Task<IReadOnlyList<AppInfo>> ClaimAppsAsync(DesktopEntryIndex unclaimed, CancellationToken ct)
        {
            Seen.AddRange(unclaimed.Entries.Select(e => e.Path));
            return Task.FromResult(Claim(unclaimed));
        }
    }

    private sealed class UpdatingProvider(string id, Func<IReadOnlyList<AppInfo>, UpdateCheck> check)
        : FakeProvider(id), IUpdateProvider
    {
        public Task<UpdateCheck> GetUpdatesAsync(IReadOnlyList<AppInfo> installed, CancellationToken ct) =>
            Task.FromResult(check(installed));
    }

    private static AppCatalog Catalog(IEnumerable<ISoftwareProvider> providers, params string[] launchers) =>
        new(providers, _ => Task.FromResult(new DesktopEntryIndex(launchers.Select(Launcher).ToList())));

    private static Task<CatalogSnapshot> Load(AppCatalog catalog) =>
        catalog.LoadAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Later_providers_only_see_what_earlier_ones_left()
    {
        var first = new FakeProvider("first") { Claim = _ => [App("first", "a", "/a.desktop")] };
        var second = new FakeProvider("second")
        {
            Claim = index => index.Entries.Select(e => App("second", e.DesktopId, e.Path)).ToList(),
        };

        var snapshot = await Load(Catalog([first, second], "/a.desktop", "/b.desktop"));

        Assert.Equal(["/a.desktop", "/b.desktop"], first.Seen);
        Assert.Equal(["/b.desktop"], second.Seen);
        Assert.Equal(["first:a", "second:b.desktop"], snapshot.Apps.Select(a => $"{a.ProviderId}:{a.Id}"));
    }

    [Fact]
    public async Task Providers_that_are_not_installed_are_skipped()
    {
        var missing = new FakeProvider("snap")
        {
            Status = ProviderStatus.NotInstalled,
            Sources = () => throw new InvalidOperationException("must not be called"),
            Claim = _ => throw new InvalidOperationException("must not be called"),
        };

        var snapshot = await Load(Catalog([missing], "/a.desktop"));

        Assert.Equal(ProviderAvailability.NotInstalled, Assert.Single(snapshot.Providers).Status.Availability);
        Assert.Empty(snapshot.Apps);
    }

    [Fact]
    public async Task A_failing_provider_does_not_fail_the_load()
    {
        var broken = new FakeProvider("broken") { Claim = _ => throw new IOException("bus went away") };
        var healthy = new FakeProvider("healthy")
        {
            Sources = () => [Source("healthy", "repo")],
            Claim = index => index.Entries.Select(e => App("healthy", e.DesktopId, e.Path)).ToList(),
        };

        var snapshot = await Load(Catalog([broken, healthy], "/a.desktop"));

        var report = snapshot.Providers.Single(p => p.ProviderId == "broken");
        Assert.Equal(ProviderAvailability.Error, report.Status.Availability);
        Assert.Equal("bus went away", report.Status.Reason);
        Assert.Equal("healthy", Assert.Single(snapshot.Apps).ProviderId);
        Assert.Equal("repo", Assert.Single(snapshot.Sources).Id);
    }

    [Fact]
    public async Task A_failing_source_listing_is_reported_as_an_error()
    {
        var broken = new FakeProvider("broken") { Sources = () => throw new IOException("no sources") };

        var snapshot = await Load(Catalog([broken]));

        Assert.Equal("no sources", Assert.Single(snapshot.Providers).Status.Reason);
        Assert.Empty(snapshot.Sources);
    }

    [Fact]
    public async Task Finds_the_same_app_installed_from_two_providers()
    {
        var native = new FakeProvider("packagekit")
        {
            Claim = _ =>
            [
                App("packagekit", "firefox", componentId: "org.mozilla.firefox.desktop"),
                App("packagekit", "gimp", name: "GIMP"),
                App("packagekit", "kate", componentId: "org.kde.kate"),
            ],
        };
        var flatpak = new FakeProvider("flatpak")
        {
            Claim = _ =>
            [
                App("flatpak", "org.mozilla.firefox", componentId: "org.mozilla.firefox"),
                App("flatpak", "org.gimp.GIMP", name: "Gimp"),
            ],
        };

        var snapshot = await Load(Catalog([native, flatpak]));

        Assert.Equal(2, snapshot.Duplicates.Count);
        Assert.All(snapshot.Duplicates, group => Assert.Equal(2, group.Apps.Count));
        Assert.DoesNotContain(snapshot.Duplicates, group => group.Apps.Any(a => a.Id == "kate"));
    }

    [Fact]
    public async Task Collects_updates_and_gives_each_provider_only_its_own_apps()
    {
        var refreshed = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var native = new UpdatingProvider("packagekit", installed => new(
            installed.Select(a => new UpdateInfo("packagekit", a.Id, a.Name, "1", "2", UpdateSeverity.Security, null, a.Id)).ToList(),
            refreshed))
        {
            Claim = _ => [App("packagekit", "firefox")],
        };
        var other = new FakeProvider("manual") { Claim = _ => [App("manual", "tool")] };

        var snapshot = await Load(Catalog([native, other]));

        var update = Assert.Single(snapshot.Updates);
        Assert.Equal("firefox", update.AppId);
        Assert.Equal(refreshed, snapshot.Providers[0].UpdatesRefreshedAt);
        Assert.Null(snapshot.Providers[0].UpdateCheckError);
        Assert.Equal(["packagekit", "manual"], snapshot.Facts.Select(f => f.ProviderId));
    }

    [Fact]
    public async Task A_failing_update_check_keeps_the_apps()
    {
        var provider = new UpdatingProvider("flatpak", _ => throw new IOException("no network"))
        {
            Claim = _ => [App("flatpak", "krita")],
        };

        var snapshot = await Load(Catalog([provider]));

        Assert.Single(snapshot.Apps);
        Assert.Empty(snapshot.Updates);
        var report = Assert.Single(snapshot.Providers);
        Assert.Equal(ProviderAvailability.Available, report.Status.Availability);
        Assert.Equal("no network", report.UpdateCheckError);
    }

    [Theory]
    [InlineData(new[] { "Qt", "KDE", "Game", "ArcadeGame" }, new[] { AppCategory.Games })]
    [InlineData(new[] { "Network", "WebBrowser", "GTK" }, new[] { AppCategory.Internet })]
    [InlineData(new[] { "AudioVideo", "Audio", "Player", "Utility" }, new[] { AppCategory.SoundAndVideo, AppCategory.Utilities })]
    [InlineData(new[] { "Settings", "System" }, new[] { AppCategory.Settings, AppCategory.System })]
    [InlineData(new[] { "X-Wine", "Qt" }, new[] { AppCategory.Other })]
    [InlineData(new string[0], new[] { AppCategory.Other })]
    public void Maps_menu_categories_to_tags(string[] desktopCategories, AppCategory[] expected)
    {
        Assert.Equal(expected, AppCategories.FromDesktopCategories(desktopCategories));
    }

    [Fact]
    public void Only_manual_apps_count_as_self_installed()
    {
        Assert.True(App("manual", "x").IsSelfInstalled);
        Assert.False(App("flatpak", "x").IsSelfInstalled);
    }

    private sealed class MemoryCache : ICacheStore
    {
        public Dictionary<string, string> Entries { get; } = [];
        public bool Broken { get; init; }
        public string? Read(string name) => Broken ? throw new IOException("disk gone") : Entries.GetValueOrDefault(name);
        public void Write(string name, string content)
        {
            if (Broken)
                throw new IOException("disk full");
            Entries[name] = content;
        }
    }

    private static AppCatalog CachedCatalog(ISoftwareProvider provider, ICacheStore cache) =>
        new([provider], _ => Task.FromResult(new DesktopEntryIndex([Launcher("/a.desktop")])), cache: cache);

    private static UpdatingProvider RichProvider() => new("packagekit", installed => new(
        [new UpdateInfo("packagekit", "firefox", "Firefox", "1", "2", UpdateSeverity.Security, "updates", "firefox")],
        new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero)))
    {
        Sources = () => [Source("packagekit", "updates")],
        Claim = _ =>
        [
            App("packagekit", "firefox", "/a.desktop", "org.mozilla.firefox") with
            {
                Categories = [AppCategory.Internet],
                InstallPath = "/opt/firefox",
            },
        ],
    };

    [Fact]
    public async Task A_load_is_saved_and_can_be_shown_again_without_asking_anyone()
    {
        var cache = new MemoryCache();
        var loaded = await Load(CachedCatalog(RichProvider(), cache));

        var untouched = new FakeProvider("packagekit")
        {
            Sources = () => throw new InvalidOperationException("must not be called"),
            Claim = _ => throw new InvalidOperationException("must not be called"),
        };
        var restored = CachedCatalog(untouched, cache).LoadCached();

        Assert.NotNull(restored);
        Assert.True(SnapshotCache.SameContent(loaded, restored));
        var app = Assert.Single(restored.Apps);
        Assert.Equal([AppCategory.Internet], app.Categories);
        Assert.Equal("/opt/firefox", app.InstallPath);
        Assert.Equal(["/a.desktop"], app.LauncherPaths);
        Assert.Equal(UpdateSeverity.Security, Assert.Single(restored.Updates).Severity);
        Assert.Equal(loaded.Providers[0].UpdatesRefreshedAt, restored.Providers[0].UpdatesRefreshedAt);
        Assert.Equal(TrustLevel.Unknown, Assert.Single(restored.Sources).Trust);
        Assert.Empty(untouched.Seen);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"formatVersion\":999,\"savedAt\":\"2026-01-01T00:00:00Z\",\"snapshot\":null}")]
    [InlineData("{\"formatVersion\":2,\"savedAt\":\"2026-01-01T00:00:00Z\",\"snapshot\":{\"apps\":null}}")]
    // A snapshot saved by an older Yar, before the format changed.
    [InlineData("{\"formatVersion\":1,\"savedAt\":\"2026-01-01T00:00:00Z\",\"snapshot\":{\"providers\":[],\"sources\":[],\"apps\":[],\"duplicates\":[],\"unclaimedLaunchers\":[],\"updates\":[],\"facts\":[]}}")]
    [InlineData("{\"formatVersion\":2,\"savedAt\":\"2026-01-01T00:00:00Z\",\"snapshot\":{\"providers\":[],\"sources\":[],\"apps\":[{\"name\":\"x\"}],\"duplicates\":[],\"unclaimedLaunchers\":[],\"updates\":[],\"facts\":[]}}")]
    public void A_saved_snapshot_that_looks_wrong_counts_as_none(string content)
    {
        var cache = new MemoryCache { Entries = { [SnapshotCache.EntryName] = content } };

        Assert.Null(CachedCatalog(new FakeProvider("x"), cache).LoadCached());
    }

    [Fact]
    public async Task A_truncated_saved_snapshot_counts_as_none()
    {
        var cache = new MemoryCache();
        await Load(CachedCatalog(RichProvider(), cache));
        var full = cache.Entries[SnapshotCache.EntryName];
        cache.Entries[SnapshotCache.EntryName] = full[..(full.Length / 2)];

        Assert.Null(CachedCatalog(new FakeProvider("x"), cache).LoadCached());
    }

    [Fact]
    public async Task A_load_with_a_failed_provider_does_not_replace_the_saved_one()
    {
        var cache = new MemoryCache();
        await Load(CachedCatalog(RichProvider(), cache));
        var saved = cache.Entries[SnapshotCache.EntryName];

        var failing = new FakeProvider("packagekit") { Claim = _ => throw new TimeoutException("no answer") };
        var degraded = await Load(CachedCatalog(failing, cache));

        Assert.Empty(degraded.Apps);
        Assert.Equal(ProviderAvailability.Error, degraded.Providers[0].Status.Availability);
        // What the next start opens on is still the last complete picture.
        Assert.Equal(saved, cache.Entries[SnapshotCache.EntryName]);
        Assert.Single(CachedCatalog(failing, cache).LoadCached()!.Apps);
    }

    [Fact]
    public async Task A_broken_cache_never_fails_a_load()
    {
        var catalog = CachedCatalog(RichProvider(), new MemoryCache { Broken = true });

        Assert.Null(catalog.LoadCached());
        Assert.Single((await Load(catalog)).Apps);
    }

    [Fact]
    public async Task Phase_one_does_not_ask_about_updates_and_phase_two_adds_them()
    {
        var asked = 0;
        var provider = new UpdatingProvider("packagekit", _ =>
        {
            asked++;
            return new([new UpdateInfo("packagekit", "x", "X", null, "2", UpdateSeverity.Normal, null, null)]);
        })
        {
            Claim = _ => [App("packagekit", "firefox")],
        };
        var catalog = Catalog([provider]);

        var apps = await catalog.LoadAppsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, asked);
        Assert.Empty(apps.Updates);
        Assert.Single(apps.Apps);

        var complete = await catalog.LoadUpdatesAsync(apps, TestContext.Current.CancellationToken);
        Assert.Equal(1, asked);
        Assert.Single(complete.Updates);
        Assert.Same(apps.Apps, complete.Apps);
        Assert.False(SnapshotCache.SameContent(apps, complete));
        Assert.Contains(complete.Timings, t => t.Stage == "claim:packagekit");
        Assert.Contains(complete.Timings, t => t.Stage == "updates:packagekit");
    }

    private sealed class LeftoverProvider(string id, Func<IReadOnlyList<AppInfo>, IReadOnlyList<LeftoverData>> find)
        : FakeProvider(id), ILeftoverProvider
    {
        public Task<IReadOnlyList<LeftoverData>> FindLeftoversAsync(IReadOnlyList<AppInfo> installed, CancellationToken ct) =>
            Task.FromResult(find(installed));
    }

    [Fact]
    public async Task Leftovers_are_collected_in_phase_two_saved_and_never_fail_a_load()
    {
        var cache = new MemoryCache();
        var seen = new List<string>();
        var flatpak = new LeftoverProvider("flatpak", installed =>
        {
            seen.AddRange(installed.Select(a => a.Id));
            return [new LeftoverData("flatpak", "com.gone.App", "/home/ada/.var/app/com.gone.App", 42)];
        })
        {
            Claim = _ => [App("flatpak", "krita")],
        };
        var failing = new LeftoverProvider("snap", _ => throw new IOException("unreadable"));
        var catalog = new AppCatalog(
            [flatpak, failing], _ => Task.FromResult(DesktopEntryIndex.Empty), cache: cache);

        var apps = await catalog.LoadAppsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(apps.Leftovers);
        Assert.Empty(seen);

        var complete = await catalog.LoadUpdatesAsync(apps, TestContext.Current.CancellationToken);
        Assert.Equal(["krita"], seen);
        Assert.Equal(42, Assert.Single(complete.Leftovers).SizeBytes);
        Assert.All(complete.Providers, p => Assert.Equal(ProviderAvailability.Available, p.Status.Availability));
        Assert.Equal("com.gone.App", Assert.Single(catalog.LoadCached()!.Leftovers).Id);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var source = new CancellationTokenSource();
        var provider = new FakeProvider("slow")
        {
            Claim = _ =>
            {
                source.Cancel();
                throw new OperationCanceledException(source.Token);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Catalog([provider]).LoadAsync(source.Token));
    }
}
