using Yar.Core;

namespace Yar.Metadata.Tests;

public class AnnotationTests
{
    private static SourceInfo Source(string provider, string id, string? url = null) => new(
        provider, id, $"original {id}", null, url, true, SourceScope.System, TrustLevel.Unknown,
        new Dictionary<string, string>());

    private static DesktopEntry Launcher(string path, string icon, string exec = "run", params string[] categories) => new(
        path, Path.GetFileName(path), "Application", "Launcher name", null, null, icon, exec, null,
        false, false, categories, new Dictionary<string, string>());

    private static AppInfo App(string provider, string name, string[] launchers, string? package = null) => new(
        provider, name, name, "provider summary", "provider-icon", null, null, null, null, null, launchers, null,
        package is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["package"] = package });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_bundled_table_describes_well_known_sources()
    {
        var known = KnownSources.LoadBundled("fedora");

        var fedora = known.Describe(Source("packagekit", "fedora"));
        Assert.Equal(TrustLevel.Distro, fedora.Trust);
        Assert.Equal("Fedora", fedora.Name);
        Assert.NotNull(fedora.Description);

        var flathub = known.Describe(Source("flatpak", "system:flathub", "https://dl.flathub.org/repo/"));
        Assert.Equal(TrustLevel.WellKnown, flathub.Trust);

        var registry = known.Describe(Source("flatpak", "system:fedora", "oci+https://registry.fedoraproject.org"));
        Assert.Equal(TrustLevel.Distro, registry.Trust);
        Assert.Equal("original system:fedora", registry.Name);

        var copr = known.Describe(Source("packagekit", "copr:copr.fedorainfracloud.org:ada:tool"));
        Assert.Equal(TrustLevel.ThirdParty, copr.Trust);

        var debug = known.Describe(Source("packagekit", "updates-debuginfo"));
        Assert.Equal("true", debug.Technical["for-developers"]);
        Assert.Equal(TrustLevel.Unknown, debug.Trust);
    }

    [Fact]
    public void Unlisted_sources_stay_unknown()
    {
        var known = KnownSources.LoadBundled("fedora");
        var vendor = Source("packagekit", "some-vendor", "https://dl.flathub.org.evil.example/repo");

        Assert.Same(vendor, known.Describe(vendor));
        // A lookalike host on a Flatpak remote is not Flathub.
        Assert.Equal(TrustLevel.Unknown, known.Describe(Source("flatpak", "user:x", "https://dl.flathub.org.evil.example/")).Trust);
    }

    [Fact]
    public void Distro_rules_do_not_apply_on_other_distros()
    {
        var updates = Source("packagekit", "updates");

        Assert.Equal(TrustLevel.Unknown, KnownSources.LoadBundled("opensuse-tumbleweed").Describe(updates).Trust);
        Assert.Equal(TrustLevel.Unknown, KnownSources.LoadBundled(null).Describe(updates).Trust);
    }

    [Fact]
    public async Task Apps_get_tags_and_the_program_their_launcher_starts()
    {
        var annotator = new CatalogAnnotator(KnownSources.Parse("[]", null), () => new ComponentIndex(), ["/usr/bin", "/bin"]);
        var index = new DesktopEntryIndex(
        [
            Launcher("/a/game.desktop", "g", "sh -c play", "Qt", "Game"),
            Launcher("/a/boxed.desktop", "b", "sh run boxed", "Graphics"),
            Launcher("/a/gone.desktop", "x", "/nonexistent/program"),
        ]);
        AppInfo[] apps =
        [
            App("packagekit", "Game", ["/a/game.desktop"]),
            App("flatpak", "Boxed", ["/a/boxed.desktop"]),
            App("packagekit", "Gone", ["/a/gone.desktop"]),
            App("manual", "Loose AppImage", []),
        ];

        var (_, annotated) = await annotator.AnnotateAsync([], apps, index, Token);

        Assert.Equal([AppCategory.Games], annotated[0].Categories);
        Assert.EndsWith("/sh", annotated[0].Technical["program"]);
        Assert.Equal([AppCategory.Graphics], annotated[1].Categories);
        Assert.False(annotated[1].Technical.ContainsKey("program"));
        Assert.False(annotated[2].Technical.ContainsKey("program"));
        Assert.Equal([AppCategory.Other], annotated[3].Categories);
    }

    [Fact]
    public void Known_sources_carry_a_homepage_unless_the_source_names_its_own()
    {
        var known = KnownSources.LoadBundled("fedora");

        Assert.Equal("https://flathub.org/", known.Describe(Source("flatpak", "system:flathub", "https://dl.flathub.org/repo/")).Technical["homepage"]);

        var own = Source("flatpak", "system:flathub", "https://dl.flathub.org/repo/") with
        {
            Technical = new Dictionary<string, string> { ["homepage"] = "https://own.example/" },
        };
        Assert.Equal("https://own.example/", known.Describe(own).Technical["homepage"]);
    }

    [Fact]
    public void Reads_the_distribution_homepage()
    {
        Assert.Equal("https://fedoraproject.org/", OsRelease.Parse("ID=fedora\nHOME_URL=\"https://fedoraproject.org/\"\n").HomeUrl);
    }

    [Theory]
    [InlineData("ID=fedora\nNAME=\"Fedora Linux\"\n", "fedora", "Fedora Linux")]
    [InlineData("# comment\nNAME='Ubuntu'\nID=ubuntu\nID_LIKE=debian\n", "ubuntu", "Ubuntu")]
    [InlineData("", null, null)]
    public void Reads_os_release(string content, string? id, string? name)
    {
        Assert.Equal(new OsRelease(id, name), OsRelease.Parse(content));
    }

    [Fact]
    public async Task Apps_take_name_and_main_launcher_from_appstream()
    {
        var components = new ComponentIndex();
        components.Add([new("org.kde.kmail2", "KMail", "Mail client", "kmail", ["org.kde.kmail2.desktop"])]);
        var annotator = new CatalogAnnotator(KnownSources.Parse("[]", null), () => components, []);
        var index = new DesktopEntryIndex(
        [
            Launcher("/a/org.kde.ktnef.desktop", "ktnef"),
            Launcher("/a/org.kde.kmail2.desktop", "kmail"),
        ]);
        AppInfo[] apps =
        [
            App("packagekit", "KTnef", ["/a/org.kde.ktnef.desktop", "/a/org.kde.kmail2.desktop"], "kmail"),
            App("packagekit", "Unknown Tool", ["/a/tool.desktop"]),
            App("manual", "Mine", ["/a/org.kde.kmail2.desktop"]),
        ];

        var (_, annotated) = await annotator.AnnotateAsync([], apps, index, Token);

        Assert.Equal("KMail", annotated[0].Name);
        Assert.Equal("Mail client", annotated[0].Summary);
        Assert.Equal("org.kde.kmail2", annotated[0].ComponentId);
        Assert.Equal("kmail", annotated[0].IconName);
        Assert.Equal(["/a/org.kde.kmail2.desktop", "/a/org.kde.ktnef.desktop"], annotated[0].LauncherPaths);
        Assert.Equal("Unknown Tool", annotated[1].Name);
        Assert.Equal("Mine", annotated[2].Name);
        Assert.Null(annotated[2].ComponentId);
    }
}
