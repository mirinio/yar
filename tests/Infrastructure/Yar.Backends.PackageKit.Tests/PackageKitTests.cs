using Yar.Core;

namespace Yar.Backends.PackageKit.Tests;

public partial class PackageKitTests
{
    private sealed class FakeClient : IPackageKitClient
    {
        public PkBackend? Backend { get; init; } = new("dnf5", "fedora;44;x86_64");
        public List<PkRepo> Repos { get; } = [];
        public Dictionary<string, string> Owners { get; } = [];
        public Dictionary<string, long> Sizes { get; } = [];
        public List<string> Searched { get; } = [];
        public int Nudges { get; private set; }
        public Dictionary<string, string> OwnersAfterNudge { get; } = [];

        public Task NotifyPackagesChangedAsync(CancellationToken ct)
        {
            Nudges++;
            foreach (var (path, id) in OwnersAfterNudge)
                Owners[path] = id;
            return Task.CompletedTask;
        }
        public Dictionary<string, OfferSize> OfferSizes { get; } = [];
        public List<string> SizeQuestions { get; } = [];

        public Task<OfferSize?> GetOfferSizeAsync(string packageName, CancellationToken ct)
        {
            SizeQuestions.Add(packageName);
            return Task.FromResult(OfferSizes.GetValueOrDefault(packageName));
        }
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

        public List<PkPackage> Available { get; } = [];
        public List<PkPackage> Simulated { get; } = [];
        public List<string> Changes { get; } = [];

        public Task<IReadOnlyList<PkPackage>> ResolveAvailableAsync(string name, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PkPackage>>(Available.Where(p => p.Name == name).ToList());

        public Task<IReadOnlyDictionary<string, OfferSize>> GetOfferSizesAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, OfferSize>>(
                ids.Where(id => OfferSizes.ContainsKey(id)).ToDictionary(id => id, id => OfferSizes[id]));

        public Task<IReadOnlyList<PkPackage>> SimulateInstallAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PkPackage>>(Simulated);

        public Task<IReadOnlyList<PkPackage>> SimulateRemoveAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PkPackage>>(Simulated);

        public Task InstallAsync(IReadOnlyList<string> ids, IProgress<PkProgress> progress, CancellationToken ct)
        {
            progress.Report(new(PkProgress.Download, 40, AllowCancel: true));
            progress.Report(new(PkProgress.Install, null, AllowCancel: false));
            Changes.Add("install " + string.Join(",", ids));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(IReadOnlyList<string> ids, bool allowDependents, IProgress<PkProgress> progress, CancellationToken ct)
        {
            Changes.Add($"remove {string.Join(",", ids)} dependents={allowDependents}");
            return Task.CompletedTask;
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
        var provider = new PackageKitProvider(client, [], [Dir], recheckWait: TimeSpan.Zero);
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

        // Four launchers in the system folder are asked about; the one nobody owns again after a nudge.
        Assert.Equal(4, client.Searched.Distinct().Count());
        Assert.Equal(1, client.Nudges);
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

            // First run: nothing remembered, every launcher is asked about. The one nobody owns is asked
            // about again a few times after telling the daemon to look, before "nobody" is believed.
            var cold = Client("6.5");
            var first = await new PackageKitProvider(cold, [], [directory], cache: cache, recheckWait: TimeSpan.Zero).ClaimAppsAsync(index, Token);
            Assert.Equal(3, cold.Searched.Distinct().Count());
            Assert.Equal(5, cold.Searched.Count(path => path == unowned));
            Assert.Equal(1, cold.Nudges);
            Assert.Empty(cold.Resolved);

            // Second run: one confirming request, no searches, no size lookups. The confirmed "nobody" is remembered.
            var warm = Client("6.5");
            var second = await new PackageKitProvider(warm, [], [directory], cache: cache, recheckWait: TimeSpan.Zero).ClaimAppsAsync(index, Token);
            Assert.Empty(warm.Searched);
            Assert.Equal(0, warm.Nudges);
            Assert.Equal(["kmail", "tabby-terminal"], Assert.Single(warm.Resolved).Order());
            Assert.Empty(Assert.Single(warm.Sized));
            Assert.Equal(first.Select(a => (a.Id, a.Version, a.SizeBytes, a.SourceId)), second.Select(a => (a.Id, a.Version, a.SizeBytes, a.SourceId)));
            Assert.Equal(2, second.Count);

            // The package was updated without its launcher changing: the new version and size are picked up.
            var updated = Client("6.6");
            var third = await new PackageKitProvider(updated, [], [directory], cache: cache, recheckWait: TimeSpan.Zero).ClaimAppsAsync(index, Token);
            Assert.Empty(updated.Searched);
            var mail = third.Single(a => a.Id == "kmail.x86_64");
            Assert.Equal("6.6", mail.Version);
            Assert.Equal(100, mail.SizeBytes);
            Assert.Equal(["kmail;6.6;x86_64;installed:updates"], Assert.Single(updated.Sized));

            // A launcher that changed on disk is asked about again; the others are not.
            File.WriteAllText(tabby, "[Desktop Entry]\nName=Changed");
            var changed = Client("6.6");
            await new PackageKitProvider(changed, [], [directory], cache: cache, recheckWait: TimeSpan.Zero).ClaimAppsAsync(index, Token);
            Assert.Equal([tabby], changed.Searched);

            // A remembered owner that is no longer installed is not trusted.
            var removed = Client("6.6");
            removed.Installed.Remove("kmail");
            removed.Owners.Remove(kmail);
            var afterRemoval = await new PackageKitProvider(removed, [], [directory], cache: cache, recheckWait: TimeSpan.Zero).ClaimAppsAsync(index, Token);
            Assert.Contains(kmail, removed.Searched);
            Assert.DoesNotContain(afterRemoval, a => a.Id == "kmail.x86_64");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_package_installed_behind_the_daemons_back_is_found_after_telling_it_to_look_again()
    {
        var launcher = $"{Dir}/xterm.desktop";
        // The daemon only knows about the new package once it has been told to look again.
        var client = new FakeClient { OwnersAfterNudge = { [launcher] = "xterm;400;x86_64;installed:fedora" } };

        var apps = await new PackageKitProvider(client, [], [Dir], recheckWait: TimeSpan.Zero)
            .ClaimAppsAsync(new DesktopEntryIndex([Launcher(launcher, "XTerm")]), Token);

        Assert.Equal("xterm.x86_64", Assert.Single(apps).Id);
        Assert.Equal(1, client.Nudges);
        // Found on the first look after the nudge; no further asking.
        Assert.Equal(2, client.Searched.Count);
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
        Assert.Equal(0, client.Nudges);
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
    public async Task Says_how_big_a_package_on_offer_is_and_only_asks_about_plain_names()
    {
        var client = new FakeClient { OfferSizes = { ["krita"] = new(92_520_618, 221_796_243) } };
        var provider = new PackageKitProvider(client, [], [Dir]);
        StoreOffer Offer(string provider, string? package) => new(provider, "fedora", "org.kde.krita", package, null, false, null);

        Assert.Equal(new OfferSize(92_520_618, 221_796_243), await provider.GetSizeAsync(Offer("packagekit", "krita"), Token));
        Assert.Null(await provider.GetSizeAsync(Offer("packagekit", "unknown-package"), Token));
        Assert.Null(await provider.GetSizeAsync(Offer("flatpak", "krita"), Token));
        Assert.Null(await provider.GetSizeAsync(Offer("packagekit", "krita; rm -rf"), Token));
        Assert.Null(await provider.GetSizeAsync(Offer("packagekit", null), Token));
        Assert.Equal(["krita", "unknown-package"], client.SizeQuestions);
    }

    private const string InRelease =
        "-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA512\n\nOrigin: Ubuntu\nLabel: Ubuntu\nSuite: noble-updates\n" +
        "Version: 24.04\nCodename: noble\nDate: Mon, 13 Oct 2026 05:58:48 UTC\nArchitectures: amd64 arm64\n" +
        "Components: main restricted universe multiverse\nDescription: Ubuntu Noble Updates\nSHA256:\n abc 123 main/x\n";

    [Fact]
    public void Builds_apt_repositories_with_the_ids_packagekit_uses_for_origins()
    {
        var repos = AptListsReader.Parse(InRelease, "/var/lib/apt/lists/archive.ubuntu.com_ubuntu_dists_noble-updates_InRelease");

        Assert.Equal(
            ["ubuntu-noble-updates-main", "ubuntu-noble-updates-restricted", "ubuntu-noble-updates-universe", "ubuntu-noble-updates-multiverse"],
            repos.Select(r => r.Repo.Id));
        Assert.Equal("Ubuntu noble-updates (main)", repos[0].Repo.Description);
        Assert.Equal("archive.ubuntu.com", repos[0].Detail.Url);
        Assert.Empty(AptListsReader.Parse("Origin: Ubuntu\nSuite: noble\n", "/x/y_InRelease"));
        Assert.Empty(AptListsReader.Parse("not a release file", "/x/y_Release"));
        Assert.Empty(new AptListsReader("/nonexistent/apt/lists").Read());
    }

    private sealed class FakeFallback(params (PkRepo, RepoDetail)[] repos) : IRepoListFallback
    {
        public IReadOnlyList<(PkRepo Repo, RepoDetail Detail)> Read() => repos;
    }

    [Fact]
    public async Task Falls_back_to_the_package_managers_own_list_only_when_packagekit_reports_none()
    {
        var fallback = new FakeFallback(
            (new PkRepo("ubuntu-noble-main", "Ubuntu noble (main)", true), new RepoDetail("", "archive.ubuntu.com", null, "/lists/a_InRelease")));

        var silent = new PackageKitProvider(new FakeClient(), [], [Dir], repoFallbacks: [fallback]);
        var source = Assert.Single(await silent.GetSourcesAsync(Token));
        Assert.Equal("ubuntu-noble-main", source.Id);
        Assert.Equal("Ubuntu noble (main)", source.Name);
        Assert.Equal("archive.ubuntu.com", source.Url);
        // PackageKit does not know this repository, so it cannot be switched from here.
        Assert.False(silent.CanToggle(source));

        var talkative = new PackageKitProvider(
            new FakeClient { Repos = { new("fedora", "Fedora", true) } }, [], [Dir], repoFallbacks: [fallback]);
        var reported = Assert.Single(await talkative.GetSourcesAsync(Token));
        Assert.Equal("fedora", reported.Id);
        Assert.True(talkative.CanToggle(reported));
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
