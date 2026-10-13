using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Yar.Metadata.Tests;

public class AppStreamTests
{
    private const string Catalog = """
        <?xml version="1.0" encoding="UTF-8"?>
        <components origin="fedora" version="0.8">
          <component type="desktop">
            <id>org.kde.konsole.desktop</id>
            <pkgname>konsole</pkgname>
            <name>Konsole</name>
            <name xml:lang="de">Konsole (de)</name>
            <summary>Terminal</summary>
            <summary xml:lang="de">Terminal-Emulator</summary>
            <summary xml:lang="pt-BR">Terminal brasileiro</summary>
            <description><p>Not a name</p><ul><li>nested</li></ul></description>
            <releases><release version="1"><description><p>ignored</p></description></release></releases>
          </component>
          <component type="desktop-application">
            <id>org.kde.kmail2</id>
            <pkgname>kmail</pkgname>
            <name>KMail</name>
            <launchable type="desktop-id">org.kde.kmail2.desktop</launchable>
            <launchable type="url">https://example.org</launchable>
          </component>
          <component type="font"><id>some.font</id><name>A Font</name></component>
          <component type="desktop"><id>no.name.desktop</id></component>
          <component type="desktop"><name>No ID</name></component>
        </components>
        """;

    private static IReadOnlyList<AppStreamComponent> Read(string xml, string culture = "") =>
        AppStreamXml.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)), new CultureInfo(culture));

    [Fact]
    public void Reads_desktop_applications_from_a_catalog()
    {
        var components = Read(Catalog);

        Assert.Equal(["org.kde.konsole.desktop", "org.kde.kmail2"], components.Select(c => c.Id));
        Assert.Equal("Konsole", components[0].Name);
        Assert.Equal("Terminal", components[0].Summary);
        Assert.Equal("konsole", components[0].PackageName);
        Assert.Equal(["org.kde.konsole.desktop"], components[0].DesktopIds);
        Assert.Equal(["org.kde.kmail2.desktop"], components[1].DesktopIds);
        Assert.Null(components[1].Summary);
    }

    [Theory]
    [InlineData("de-AT", "Konsole (de)", "Terminal-Emulator")]
    [InlineData("pt-BR", "Konsole", "Terminal brasileiro")]
    [InlineData("fr-FR", "Konsole", "Terminal")]
    public void Picks_translations(string culture, string name, string summary)
    {
        var konsole = Read(Catalog, culture)[0];

        Assert.Equal(name, konsole.Name);
        Assert.Equal(summary, konsole.Summary);
    }

    [Fact]
    public void Reads_a_single_metainfo_component()
    {
        var component = Assert.Single(Read("""
            <component type="desktop-application">
              <id>org.example.App</id>
              <name>Example</name>
              <summary>An example</summary>
            </component>
            """));

        Assert.Equal(["org.example.App.desktop"], component.DesktopIds);
        Assert.Null(component.PackageName);
    }

    [Fact]
    public void Documents_with_a_doctype_are_refused()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE components [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <components><component type="desktop"><id>x.desktop</id><name>&xxe;</name></component></components>
            """;

        Assert.Throws<System.Xml.XmlException>(() => Read(xml));
    }

    [Fact]
    public void Reads_gzip_files_and_survives_damaged_ones()
    {
        var directory = Directory.CreateTempSubdirectory("yar-appstream-").FullName;
        try
        {
            var good = Path.Combine(directory, "good.xml.gz");
            using (var gzip = new GZipStream(File.Create(good), CompressionLevel.Fastest))
                gzip.Write(Encoding.UTF8.GetBytes(Catalog));
            var truncated = Path.Combine(directory, "truncated.xml");
            File.WriteAllText(truncated, Catalog[..Catalog.IndexOf("<component type=\"desktop-application\">", StringComparison.Ordinal)] + "<component type=\"desktop\"><id>cut");
            var garbage = Path.Combine(directory, "garbage.xml.gz");
            File.WriteAllText(garbage, "this is not gzip");

            Assert.Equal(2, AppStreamXml.ReadFile(good, CultureInfo.InvariantCulture).Count);
            Assert.Equal("Konsole", Assert.Single(AppStreamXml.ReadFile(truncated, CultureInfo.InvariantCulture)).Name);
            Assert.Empty(AppStreamXml.ReadFile(garbage));
            Assert.Empty(AppStreamXml.ReadFile(Path.Combine(directory, "missing.xml")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class MemoryCache : Core.ICacheStore
    {
        public Dictionary<string, string> Entries { get; } = [];
        public string? Read(string name) => Entries.GetValueOrDefault(name);
        public void Write(string name, string content) => Entries[name] = content;
    }

    [Fact]
    public void Unchanged_files_are_not_parsed_again()
    {
        var directory = Directory.CreateTempSubdirectory("yar-appstream-cache-").FullName;
        try
        {
            var a = Path.Combine(directory, "a.xml");
            var b = Path.Combine(directory, "b.xml");
            File.WriteAllText(a, "a");
            File.WriteAllText(b, "b");
            var cache = new MemoryCache();
            var parsed = new List<string>();
            IReadOnlyList<AppStreamComponent> Read(string path, CultureInfo? culture)
            {
                parsed.Add(Path.GetFileName(path));
                return [new($"{Path.GetFileName(path)}.app", "Name", "Summary", "pkg", [$"{Path.GetFileName(path)}.desktop"])];
            }
            ComponentIndex Load(string culture = "") => ComponentIndex.Load([a, b], new CultureInfo(culture), cache, Read);

            Assert.Equal(2, Load().Count);
            Assert.Equal(["a.xml", "b.xml"], parsed);

            parsed.Clear();
            var warm = Load();
            Assert.Empty(parsed);
            Assert.Equal("Name", warm.Find("a.xml.desktop", "pkg")?.Name);
            Assert.Equal(["a.xml.desktop"], warm.Find("a.xml.desktop")?.DesktopIds);

            File.WriteAllText(b, "b changed");
            Load();
            Assert.Equal(["b.xml"], parsed);

            // Another language has its own saved index.
            parsed.Clear();
            Load("de-DE");
            Assert.Equal(["a.xml", "b.xml"], parsed);

            // A damaged saved index is ignored.
            parsed.Clear();
            cache.Entries["appstream-c.json"] = "garbage";
            Assert.Equal(2, Load().Count);
            Assert.Equal(["a.xml", "b.xml"], parsed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_index_prefers_the_component_of_the_owning_package()
    {
        var index = new ComponentIndex();
        index.Add([
            new("firefox.desktop", "Firefox (vendor)", null, "firefox-vendor", ["firefox.desktop"]),
            new("firefox.desktop", "Firefox", null, "firefox", ["firefox.desktop"]),
        ]);

        Assert.Equal("Firefox", index.Find("firefox.desktop", "firefox")?.Name);
        Assert.Equal("Firefox (vendor)", index.Find("firefox.desktop")?.Name);
        Assert.Null(index.Find("other.desktop"));
        Assert.Equal(2, index.Count);
    }
}
