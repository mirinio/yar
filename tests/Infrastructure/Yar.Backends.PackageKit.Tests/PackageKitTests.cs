using Yar.Core;

namespace Yar.Backends.PackageKit.Tests;

public class PackageKitTests
{
    private sealed class FakeClient : IPackageKitClient
    {
        public PkBackend? Backend { get; init; } = new("dnf5", "fedora;44;x86_64");
        public List<PkRepo> Repos { get; } = [];
        public Dictionary<string, string> Owners { get; } = [];
        public Dictionary<string, long> Sizes { get; } = [];
        public List<string> Searched { get; } = [];
        public List<(string RepoId, bool Enabled)> Switched { get; } = [];

        public Task SetRepoEnabledAsync(string repoId, bool enabled, CancellationToken ct)
        {
            Switched.Add((repoId, enabled));
            return Task.CompletedTask;
        }
        public List<IReadOnlyList<string>> Resolved { get; } = [];
        public List<IReadOnlyList<string>> Sized { get; } = [];
        public Dictionary<string, string> Installed { get; } = [];

        public Task<IReadOnlyList<PkPackage>> ResolveInstalledAsync(IReadOnlyList<string> names, CancellationToken ct)
        {
            Resolved.Add(names);
            return Task.FromResult<IReadOnlyList<PkPackage>>(Installed
                .Where(pair => names.Contains(pair.Key))
                .Select(pair => PkPackage.Parse(pair.Value, "Package summary")!).ToList());
        }
        public List<PkPackage> Updates { get; } = [];
        public TimeSpan? SinceRefresh { get; init; }

        public Task<IReadOnlyList<PkPackage>> GetUpdatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PkPackage>>(Updates);

        public Task<TimeSpan?> GetTimeSinceRefreshAsync(CancellationToken ct) => Task.FromResult(SinceRefresh);

        public Task<PkBackend?> GetBackendAsync(CancellationToken ct) => Task.FromResult(Backend);

        public Task<IReadOnlyList<PkRepo>> GetRepoListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PkRepo>>(Repos);

        public Task<IReadOnlyList<PkPackage>> SearchInstalledFileAsync(string path, CancellationToken ct)
        {
            lock (Searched) Searched.Add(path);
            return Task.FromResult<IReadOnlyList<PkPackage>>(
                Owners.TryGetValue(path, out var id) ? [PkPackage.Parse(id, "Package summary")!] : []);
        }

        public Task<IReadOnlyDictionary<string, long>> GetSizesAsync(IReadOnlyList<string> ids, CancellationToken ct)
        {
            Sized.Add(ids);
            return Task.FromResult<IReadOnlyDictionary<string, long>>(
                Sizes.Where(pair => ids.Any(id => id.StartsWith(pair.Key[..pair.Key.LastIndexOf(';')], StringComparison.Ordinal)))
                    .ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }

    private sealed class FakeDetails(params RepoDetail[] details) : IRepoDetailReader
    {
        public IReadOnlyDictionary<string, RepoDetail> Read() => details.ToDictionary(d => d.RepoId);
    }

    private const string Dir = "/usr/share/applications";

    private static DesktopEntry Launcher(string path, string name, string? comment = null) => new(
        path, Path.GetFileName(path), "Application", name, null, comment, "icon-" + name, "run", null,
        false, false, [], new Dictionary<string, string>());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("konsole;26.08.1-1.fc44;x86_64;installed:updates", "updates")]
    [InlineData("bash;5.3.9-3.fc44;x86_64;installed:<unknown>", null)]
    [InlineData("zypper;1.14;x86_64;installed", null)]
    [InlineData("pkg;1.0;x86_64;installed:@System", null)]
    [InlineData("pkg;1.0;x86_64;installed:", null)]
    [InlineData("pkg;1.0;x86_64;", null)]
    public void Reads_the_origin_repository_from_a_package_id(string id, string? expected)
    {
        var package = PkPackage.Parse(id, "");

        Assert.NotNull(package);
        Assert.Equal(expected, package.OriginRepo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just-a-name")]
    [InlineData(";1.0;x86_64;installed")]
    [InlineData("a;b;c;d;e")]
    public void Malformed_package_ids_are_rejected(string id)
    {
        Assert.Null(PkPackage.Parse(id, ""));
    }

    [Fact]
    public async Task Lists_repositories_with_details_from_their_files()
    {
        var client = new FakeClient
        {
            Repos =
            {
                new("fedora", "Fedora 44 - x86_64", true),
                new("copr:copr.fedorainfracloud.org:ada:tool", "", false),
            },
        };
        var provider = new PackageKitProvider(
            client, [new FakeDetails(new RepoDetail("fedora", "https://example.org/fedora", true, "/etc/yum.repos.d/fedora.repo"))], [Dir]);

        var sources = await provider.GetSourcesAsync(Token);

        Assert.Equal("Fedora 44 - x86_64", sources[0].Name);
        Assert.Equal("https://example.org/fedora", sources[0].Url);
        Assert.Equal("on", sources[0].Technical["signature-check"]);
        Assert.Equal("dnf5", sources[0].Technical["backend"]);
        Assert.Equal("copr:copr.fedorainfracloud.org:ada:tool", sources[1].Name);
        Assert.False(sources[1].Enabled);
        Assert.Null(sources[1].Url);
        Assert.Equal("copr", sources[1].Technical["flavor"]);
    }

    [Fact]
    public async Task Groups_launchers_by_owning_package()
    {
        var client = new FakeClient
        {
            Owners =
            {
                [$"{Dir}/org.kde.ktnef.desktop"] = "kmail;6.5;x86_64;installed:updates",
                [$"{Dir}/org.kde.kmail2.desktop"] = "kmail;6.5;x86_64;installed:updates",
                [$"{Dir}/tabby.desktop"] = "tabby-terminal;1.0;x86_64;installed:<unknown>",
            },
            // dnf5 answers with a different data field than the one it was asked about.
            Sizes = { ["kmail;6.5;x86_64;updates"] = 123456 },
        };
        var provider = new PackageKitProvider(client, [], [Dir]);
        var index = new DesktopEntryIndex(
        [
            Launcher($"{Dir}/org.kde.ktnef.desktop", "KTnef"),
            Launcher($"{Dir}/org.kde.kmail2.desktop", "KMail", "Mail client"),
            Launcher($"{Dir}/tabby.desktop", "Tabby"),
            Launcher($"{Dir}/unowned.desktop", "Unowned"),
            Launcher("/home/ada/.local/share/applications/mine.desktop", "Mine"),
            Launcher("/usr/share/applications-extra/lookalike.desktop", "Lookalike"),
        ]);

        var apps = await provider.ClaimAppsAsync(index, Token);

        Assert.Equal(4, client.Searched.Count);
        Assert.DoesNotContain(client.Searched, path => path.Contains("mine") || path.Contains("lookalike"));

        var mail = apps.Single(a => a.Id == "kmail.x86_64");
        Assert.Equal("KMail", mail.Name);
        Assert.Equal("Mail client", mail.Summary);
        Assert.Equal([$"{Dir}/org.kde.kmail2.desktop", $"{Dir}/org.kde.ktnef.desktop"], mail.LauncherPaths);
        Assert.Equal("updates", mail.SourceId);
        Assert.Equal(123456, mail.SizeBytes);
        Assert.Equal("6.5", mail.Version);

        var tabby = apps.Single(a => a.Id == "tabby-terminal.x86_64");
        Assert.Null(tabby.SourceId);
        Assert.Null(tabby.SizeBytes);
        Assert.Equal("Package summary", tabby.Summary);
        Assert.Equal(2, apps.Count);
    }

    private sealed class MemoryCache : ICacheStore
    {
        public Dictionary<string, string> Entries { get; } = [];
        public string? Read(string name) => Entries.GetValueOrDefault(name);
        public void Write(string name, string content) => Entries[name] = content;
    }

    [Fact]
    public async Task Remembers_launcher_owners_between_runs()
    {
        var directory = Directory.CreateTempSubdirectory("yar-owners-").FullName;
        try
        {
            string Create(string name)
            {
                var path = Path.Combine(directory, name);
                File.WriteAllText(path, "[Desktop Entry]");
                return path;
            }
            var kmail = Create("kmail.desktop");
            var tabby = Create("tabby.desktop");
            var unowned = Create("unowned.desktop");
            var index = new DesktopEntryIndex([Launcher(kmail, "KMail"), Launcher(tabby, "Tabby"), Launcher(unowned, "Unowned")]);
            var cache = new MemoryCache();

            FakeClient Client(string kmailVersion) => new()
            {
                Owners =
                {
                    [kmail] = $"kmail;{kmailVersion};x86_64;installed:updates",
                    [tabby] = "tabby-terminal;1.0;x86_64;installed:<unknown>",
                },
                Installed =
                {
                    ["kmail"] = $"kmail;{kmailVersion};x86_64;installed:updates",
                    ["tabby-terminal"] = "tabby-terminal;1.0;x86_64;installed:<unknown>",
                },
                Sizes = { [$"kmail;{kmailVersion};x86_64;updates"] = 100, ["tabby-terminal;1.0;x86_64;x"] = 200 },
            };

            // First run: nothing remembered, every launcher is asked about.
            var cold = Client("6.5");
            var first = await new PackageKitProvider(cold, [], [directory], cache: cache).ClaimAppsAsync(index, Token);
            Assert.Equal(3, cold.Searched.Count);
            Assert.Empty(cold.Resolved);

            // Second run: one confirming request, no searches, no size lookups.
            var warm = Client("6.5");
            var second = await new PackageKitProvider(warm, [], [directory], cache: cache).ClaimAppsAsync(index, Token);
            Assert.Empty(warm.Searched);
            Assert.Equal(["kmail", "tabby-terminal"], Assert.Single(warm.Resolved).Order());
            Assert.Empty(Assert.Single(warm.Sized));
            Assert.Equal(first.Select(a => (a.Id, a.Version, a.SizeBytes, a.SourceId)), second.Select(a => (a.Id, a.Version, a.SizeBytes, a.SourceId)));
            Assert.Equal(2, second.Count);

            // The package was updated without its launcher changing: the new version and size are picked up.
            var updated = Client("6.6");
            var third = await new PackageKitProvider(updated, [], [directory], cache: cache).ClaimAppsAsync(index, Token);
            Assert.Empty(updated.Searched);
            var mail = third.Single(a => a.Id == "kmail.x86_64");
            Assert.Equal("6.6", mail.Version);
            Assert.Equal(100, mail.SizeBytes);
            Assert.Equal(["kmail;6.6;x86_64;installed:updates"], Assert.Single(updated.Sized));

            // A launcher that changed on disk is asked about again; the others are not.
            File.WriteAllText(tabby, "[Desktop Entry]\nName=Changed");
            var changed = Client("6.6");
            await new PackageKitProvider(changed, [], [directory], cache: cache).ClaimAppsAsync(index, Token);
            Assert.Equal([tabby], changed.Searched);

            // A remembered owner that is no longer installed is not trusted.
            var removed = Client("6.6");
            removed.Installed.Remove("kmail");
            removed.Owners.Remove(kmail);
            var afterRemoval = await new PackageKitProvider(removed, [], [directory], cache: cache).ClaimAppsAsync(index, Token);
            Assert.Equal([kmail], removed.Searched);
            Assert.DoesNotContain(afterRemoval, a => a.Id == "kmail.x86_64");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_damaged_owner_cache_is_ignored()
    {
        var cache = new MemoryCache { Entries = { ["packagekit-owners.json"] = "{ not json" } };
        var client = new FakeClient { Owners = { [$"{Dir}/tabby.desktop"] = "tabby-terminal;1.0;x86_64;installed" } };

        var apps = await new PackageKitProvider(client, [], [Dir], cache: cache)
            .ClaimAppsAsync(new DesktopEntryIndex([Launcher($"{Dir}/tabby.desktop", "Tabby")]), Token);

        Assert.Single(apps);
        Assert.Single(client.Searched);
    }

    [Theory]
    [InlineData(8u, UpdateSeverity.Security)]
    [InlineData(6u, UpdateSeverity.BugFix)]
    [InlineData(7u, UpdateSeverity.BugFix)]
    [InlineData(4u, UpdateSeverity.Enhancement)]
    [InlineData(5u, UpdateSeverity.Normal)]
    [InlineData(2u, UpdateSeverity.Normal)]
    [InlineData(999u, UpdateSeverity.Normal)]
    public void Maps_update_kinds(uint info, UpdateSeverity expected)
    {
        Assert.Equal(expected, PackageKitProvider.SeverityOf(info));
    }

    [Fact]
    public async Task Links_updates_to_installed_apps_by_name_and_architecture()
    {
        var client = new FakeClient
        {
            SinceRefresh = TimeSpan.FromHours(2),
            Updates =
            {
                PkPackage.Parse("firefox;157.0;x86_64;updates", "Browser", 8)!,
                PkPackage.Parse("firefox;157.0;i686;updates", "Browser", 8)!,
                PkPackage.Parse("glibc;2.43;x86_64;updates", "C library", 6)!,
            },
        };
        var provider = new PackageKitProvider(client, [], [Dir]);
        AppInfo[] installed =
        [
            new(PackageKitProvider.ProviderId, "firefox.x86_64", "Firefox", null, null, null, "156.0", "updates",
                null, null, [], null, new Dictionary<string, string>()),
        ];

        var check = await provider.GetUpdatesAsync(installed, Token);

        Assert.Equal(3, check.Updates.Count);
        var app = check.Updates[0];
        Assert.Equal("firefox.x86_64", app.AppId);
        Assert.Equal("Firefox", app.Name);
        Assert.Equal("156.0", app.CurrentVersion);
        Assert.Equal("157.0", app.NewVersion);
        Assert.Equal(UpdateSeverity.Security, app.Severity);
        Assert.Equal("updates", app.SourceId);
        Assert.Null(check.Updates[1].AppId);
        Assert.Null(check.Updates[2].AppId);
        Assert.Equal("glibc", check.Updates[2].Name);
        Assert.InRange(DateTimeOffset.UtcNow - check.LastRefreshed!.Value, TimeSpan.FromHours(1.9), TimeSpan.FromHours(2.1));
    }

    [Fact]
    public async Task Switches_configured_repositories_but_cannot_add_or_remove_them()
    {
        var client = new FakeClient();
        var provider = new PackageKitProvider(client, [], [Dir]);
        var repo = new SourceInfo(PackageKitProvider.ProviderId, "updates-testing", "Test updates", null, null, false,
            SourceScope.System, TrustLevel.Distro, new Dictionary<string, string>());
        var foreign = repo with { ProviderId = "flatpak" };

        Assert.True(provider.CanToggle(repo));
        Assert.False(provider.CanToggle(foreign));
        Assert.False(provider.CanAdd);
        Assert.False(provider.CanRemove(repo));

        await provider.SetEnabledAsync(repo, true, Token);
        Assert.Equal([("updates-testing", true)], client.Switched);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SetEnabledAsync(foreign, true, Token));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.AddAsync(new("x", "https://x/y.repo"), Token));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.RemoveAsync(repo, Token));
    }

    [Fact]
    public async Task Reports_not_installed_without_a_daemon()
    {
        var provider = new PackageKitProvider(new FakeClient { Backend = null }, [], [Dir]);

        Assert.Equal(ProviderStatus.NotInstalled, await provider.ProbeAsync(Token));
    }

    [Fact]
    public void Parses_repo_files()
    {
        var details = RepoFileReader.Parse("""
            # comment
            ignored=before any section
            [fedora]
            name=Fedora $releasever - $basearch
            #baseurl=http://download.example/
            metalink=https://mirrors.fedoraproject.org/metalink?repo=fedora-$releasever&arch=$basearch
            enabled=1
            gpgcheck=1

            [vendor]
            name = Vendor
            baseurl = https://a.example/repo
                      https://b.example/repo
            gpgcheck = 0

            [bare]
            name=No url or gpgcheck
            """, "/etc/yum.repos.d/test.repo");

        Assert.Equal(["fedora", "vendor", "bare"], details.Select(d => d.RepoId));
        Assert.StartsWith("https://mirrors.fedoraproject.org/metalink", details[0].Url);
        Assert.True(details[0].SignatureCheck);
        Assert.Equal("https://a.example/repo", details[1].Url);
        Assert.False(details[1].SignatureCheck);
        Assert.Null(details[2].Url);
        Assert.Null(details[2].SignatureCheck);
        Assert.All(details, d => Assert.Equal("/etc/yum.repos.d/test.repo", d.FilePath));
    }

    [Fact]
    public void Missing_repo_directories_yield_nothing()
    {
        Assert.Empty(new RepoFileReader(["/nonexistent/yar-repos.d"]).Read());
    }
}
