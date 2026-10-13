using System.Globalization;
using Yar.Core;

namespace Yar.Metadata.Tests;

public sealed class StoreCatalogReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("yar-store-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Flathub = """
        <?xml version="1.0" encoding="UTF-8"?>
        <components version="0.8" origin="flatpak">
          <component type="desktop-application">
            <id>org.kde.krita</id>
            <name>Krita</name>
            <name xml:lang="de">Krita (de)</name>
            <summary>Digital Painting</summary>
            <project_license>GPL-3.0-only</project_license>
            <description>
              <p>Krita is a   great choice
                 for concept art.</p>
              <p xml:lang="de">Nicht dieser Absatz.</p>
              <ul><li>Brushes</li><li>Layers</li></ul>
            </description>
            <developer id="org.krita"><name>Krita Foundation</name></developer>
            <launchable type="desktop-id">org.kde.krita.desktop</launchable>
            <icon height="64" type="cached" width="64">org.kde.krita.png</icon>
            <icon height="128" type="cached" width="128">org.kde.krita.png</icon>
            <icon height="64" type="cached" width="64">../../../../etc/passwd</icon>
            <icon type="stock">org.kde.krita</icon>
            <url type="bugtracker">https://bugs.example/</url>
            <url type="homepage">https://www.krita.org/</url>
            <categories><category>Graphics</category><category>2DGraphics</category></categories>
            <provides><binary>krita</binary><id>krita.desktop</id></provides>
            <screenshots>
              <screenshot>
                <image type="source" width="1920">https://dl.flathub.org/media/krita/second_orig.png</image>
                <image type="thumbnail" width="752">https://dl.flathub.org/media/krita/second_752.png</image>
              </screenshot>
              <screenshot type="default">
                <caption>Main window</caption>
                <image type="source" width="1920">https://dl.flathub.org/media/krita/first_orig.png</image>
                <image type="thumbnail" width="624">http://dl.flathub.org/media/krita/first_624.png</image>
              </screenshot>
              <screenshot><image type="source">https://krita.example/only-on-makers-site.png</image></screenshot>
            </screenshots>
            <releases><release version="5.2.9" timestamp="1"/><release version="5.2.8"/></releases>
            <bundle type="flatpak">app/org.kde.krita/x86_64/stable</bundle>
            <custom>
              <value key="flathub::verification::verified">true</value>
              <value key="flathub::verification::website">krita.org</value>
            </custom>
          </component>
          <component type="desktop-application"><id>org.example.Plain</id><name>Plain</name></component>
          <component type="runtime"><id>org.kde.Platform</id><name>KDE Platform</name></component>
          <component type="desktop-application"><name>No ID</name></component>
        </components>
        """;

    private StoreCatalogReader.CatalogFile Write(string name, string content, string provider, string? sourceId)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return new(path, provider, sourceId, Path.Combine(_root, "icons"));
    }

    private sealed class MemoryCache : ICacheStore
    {
        public Dictionary<string, string> Entries { get; } = [];
        public int Writes { get; private set; }
        public string? Read(string name) => Entries.GetValueOrDefault(name);
        public void Write(string name, string content)
        {
            Writes++;
            Entries[name] = content;
        }
    }

    private StoreCatalogReader Reader(ICacheStore? cache = null, string culture = "") =>
        new(new XdgDirs(_root, _ => null), cache, new CultureInfo(culture));

    [Fact]
    public void Reads_what_the_store_shows_about_an_app()
    {
        Directory.CreateDirectory(Path.Combine(_root, "icons", "64x64"));
        File.WriteAllText(Path.Combine(_root, "icons", "64x64", "org.kde.krita.png"), "png");

        var entries = Reader().Read([Write("flathub.xml", Flathub, "flatpak", "system:flathub")], TestContext.Current.CancellationToken);

        Assert.Equal(["org.kde.krita", "org.example.Plain"], entries.Select(e => e.ComponentId));
        var krita = entries[0];
        Assert.Equal("flatpak", krita.ProviderId);
        Assert.Equal("system:flathub", krita.SourceId);
        Assert.Equal("Krita", krita.Name);
        Assert.Equal("Krita is a great choice for concept art.\n\n• Brushes\n• Layers", krita.Description);
        Assert.Equal("Krita Foundation", krita.Developer);
        Assert.Equal("GPL-3.0-only", krita.License);
        Assert.Equal("https://www.krita.org/", krita.Homepage);
        Assert.Equal(Path.Combine(_root, "icons", "64x64", "org.kde.krita.png"), krita.IconPath);
        Assert.Equal("org.kde.krita", krita.IconName);
        Assert.Equal(["Graphics", "2DGraphics"], krita.DesktopCategories);
        Assert.Equal(["krita.desktop", "org.kde.krita.desktop"], krita.OtherIds);
        Assert.Equal("app/org.kde.krita/x86_64/stable", krita.PackageName);
        Assert.Equal("5.2.9", krita.Version);
        Assert.True(krita.MakerVerified);
        Assert.Equal("krita.org", krita.VerifiedBy);
        // The default picture first; plain http upgraded; the one on an unknown server left out.
        Assert.Equal(
            ["https://dl.flathub.org/media/krita/first_624.png", "https://dl.flathub.org/media/krita/second_752.png"],
            krita.Screenshots);

        var plain = entries[1];
        Assert.False(plain.MakerVerified);
        Assert.Null(plain.Description);
        Assert.Null(plain.IconPath);
    }

    [Fact]
    public void Distribution_catalogs_take_their_source_from_the_catalog_itself()
    {
        var fedora = """
            <components origin="fedora" version="0.8">
              <component type="desktop"><id>firefox.desktop</id><pkgname>firefox</pkgname><name>Firefox</name>
                <icon type="cached" height="64" width="64">firefox.png</icon></component>
            </components>
            """;
        Directory.CreateDirectory(Path.Combine(_root, "icons", "fedora", "64x64"));
        File.WriteAllText(Path.Combine(_root, "icons", "fedora", "64x64", "firefox.png"), "png");

        var entry = Assert.Single(Reader().Read([Write("fedora.xml", fedora, "packagekit", null)], TestContext.Current.CancellationToken));

        Assert.Equal("fedora", entry.SourceId);
        Assert.Equal("firefox", entry.PackageName);
        Assert.EndsWith("icons/fedora/64x64/firefox.png", entry.IconPath);
    }

    [Fact]
    public void A_catalog_cannot_steer_icon_lookups_out_of_its_icon_folder()
    {
        // The icon exists where a hostile origin would lead, outside the icon root.
        var outside = Path.Combine(_root, "outside", "64x64");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "app.png"), "png");
        var iconRoot = Path.Combine(_root, "icons");
        Directory.CreateDirectory(iconRoot);
        var xml = """
            <components origin="../outside">
              <component type="desktop"><id>app.desktop</id><name>App</name>
                <icon type="cached" height="64" width="64">app.png</icon></component>
            </components>
            """;
        var file = Path.Combine(_root, "hostile.xml");
        File.WriteAllText(file, xml);

        var entry = Assert.Single(Reader().Read([new(file, "packagekit", null, iconRoot)], TestContext.Current.CancellationToken));

        Assert.Null(entry.IconPath);
    }

    [Fact]
    public void Picks_the_users_language_for_names()
    {
        var entries = Reader(culture: "de-DE").Read([Write("flathub.xml", Flathub, "flatpak", "system:flathub")], TestContext.Current.CancellationToken);

        Assert.Equal("Krita (de)", entries[0].Name);
    }

    [Fact]
    public void Unchanged_catalogs_come_from_the_saved_copy()
    {
        var cache = new MemoryCache();
        var file = Write("flathub.xml", Flathub, "flatpak", "system:flathub");

        var first = Reader(cache).Read([file], TestContext.Current.CancellationToken);
        Assert.Equal(1, cache.Writes);

        // Replace the content without changing size or time: a reader that parsed again would notice.
        var stamp = File.GetLastWriteTimeUtc(file.Path);
        File.WriteAllText(file.Path, Flathub.Replace("Krita Foundation", "Someone Else!!!!"));
        File.SetLastWriteTimeUtc(file.Path, stamp);
        var second = Reader(cache).Read([file], TestContext.Current.CancellationToken);

        Assert.Equal("Krita Foundation", second[0].Developer);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(1, cache.Writes);

        File.SetLastWriteTimeUtc(file.Path, stamp.AddSeconds(5));
        Assert.Equal("Someone Else!!!!", Reader(cache).Read([file], TestContext.Current.CancellationToken)[0].Developer);
    }

    [Fact]
    public void Damaged_and_hostile_catalogs_do_no_harm()
    {
        var truncated = Write("cut.xml", Flathub[..Flathub.IndexOf("<component type=\"runtime\"", StringComparison.Ordinal)] + "<component type=\"desktop\"><id>cut", "flatpak", "s");
        var entity = Write("xxe.xml", "<?xml version=\"1.0\"?><!DOCTYPE c [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><components><component type=\"desktop\"><id>a</id><name>&x;</name></component></components>", "flatpak", "s");
        var missing = new StoreCatalogReader.CatalogFile(Path.Combine(_root, "missing.xml"), "flatpak", "s", _root);
        var cache = new MemoryCache { Entries = { ["store-c.json"] = "{ garbage" } };

        var entries = Reader(cache).Read([truncated, entity, missing], TestContext.Current.CancellationToken);

        Assert.Equal(["org.kde.krita", "org.example.Plain"], entries.Select(e => e.ComponentId));
    }

    [Fact]
    public void The_bundled_popular_sources_are_well_formed()
    {
        var sources = PopularSources.LoadBundled();

        Assert.NotEmpty(sources);
        Assert.All(sources, s => Assert.StartsWith("https://", s.Homepage));
        Assert.All(sources.Where(s => s.Url is not null), s =>
        {
            var (request, problem) = SourceAddress.Check(s.Url, s.RemoteName);
            Assert.Null(problem);
            Assert.Equal(s.RemoteName, request!.Name);
        });

        var flathub = sources.First(s => s.RemoteName == "flathub");
        var existing = new SourceInfo("flatpak", "system:flathub", "Flathub", null, null, true, SourceScope.System,
            TrustLevel.WellKnown, new Dictionary<string, string> { ["remote"] = "flathub" });
        Assert.Same(existing, PopularSources.FindExisting(flathub, [existing]));
        Assert.Null(PopularSources.FindExisting(sources.First(s => s.RemoteName == "flathub-beta"), [existing]));
    }
}
