using System.Globalization;
using System.IO.Compression;
using System.Text;
using Yar.Core;

namespace Yar.Metadata.Tests;

public sealed class Dep11YamlTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("yar-dep11-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Same layout as Ubuntu 24.04's catalog files.
    private const string Catalog = """
        ---
        File: DEP-11
        Version: '1.0'
        Origin: ubuntu-noble-universe
        MediaBaseUrl: https://appstream.ubuntu.com/media/noble
        Priority: 40
        ---
        Type: desktop-application
        ID: org.kde.kcalc.desktop
        Package: kcalc
        ProjectLicense: GPL-2.0+
        Name:
          pt-BR: KCalc (pt)
          C: KCalc
          de: KCalc (de)
        Summary:
          C: Scientific Calculator
          de: Wissenschaftlicher Rechner
        Description:
          C: >-
            <p>KCalc has everything you would expect from a calculator, plus:</p>

            <ul>
              <li>Trigonometric functions &amp; logic</li>
              <li>A results stack</li>
            </ul>
          de: <p>Nicht dieser Text.</p>
        Developer:
          name:
            C: KDE
        Categories:
        - Utility
        - Calculator
        Url:
          homepage: https://apps.kde.org/kcalc/
        Icon:
          cached:
          - name: kcalc_kcalc.png
            width: 48
            height: 48
          - name: kcalc_kcalc.png
            width: 64
            height: 64
          - name: ../../../etc/passwd
            width: 64
            height: 64
          stock: accessories-calculator
        Launchable:
          desktop-id:
          - org.kde.kcalc.desktop
        Provides:
          ids:
          - kcalc.desktop
        Screenshots:
        - thumbnails:
          - url: org/kde/kcalc.desktop/second_752x423.png
            width: 752
            height: 423
          source-image:
            url: org/kde/kcalc.desktop/second_orig.png
            width: 1920
            height: 1080
        - default: true
          thumbnails:
          - url: org/kde/kcalc.desktop/first_624x351.png
            width: 624
            height: 351
        - source-image:
            url: https://elsewhere.example/shot.png
        ---
        Type: font
        ID: some.font
        Name:
          C: A Font
        ---
        Type: desktop-application
        ID: no.name.desktop
        ---
        Type: desktop-application
        ID: plain.desktop
        Name:
          C: Plain
        """;

    private static IReadOnlyList<CatalogEntry> Read(string yaml, string culture = "", string iconRoot = "/nonexistent") =>
        Dep11Yaml.Read(new StringReader(yaml), "packagekit", iconRoot, new CultureInfo(culture));

    [Fact]
    public void Reads_apps_and_takes_their_source_from_the_catalog_header()
    {
        var iconDir = Path.Combine(_root, "ubuntu-noble-universe", "64x64");
        Directory.CreateDirectory(iconDir);
        File.WriteAllText(Path.Combine(iconDir, "kcalc_kcalc.png"), "png");

        var entries = Read(Catalog, iconRoot: _root);

        Assert.Equal(["org.kde.kcalc.desktop", "plain.desktop"], entries.Select(e => e.ComponentId));
        var kcalc = entries[0];
        Assert.Equal("packagekit", kcalc.ProviderId);
        Assert.Equal("ubuntu-noble-universe", kcalc.SourceId);
        Assert.Equal("KCalc", kcalc.Name);
        Assert.Equal("Scientific Calculator", kcalc.Summary);
        Assert.Equal(
            "KCalc has everything you would expect from a calculator, plus:\n\n• Trigonometric functions & logic\n• A results stack",
            kcalc.Description);
        Assert.Equal("KDE", kcalc.Developer);
        Assert.Equal("GPL-2.0+", kcalc.License);
        Assert.Equal("https://apps.kde.org/kcalc/", kcalc.Homepage);
        Assert.Equal("kcalc", kcalc.PackageName);
        Assert.Equal(["Utility", "Calculator"], kcalc.DesktopCategories);
        Assert.Equal(["org.kde.kcalc.desktop", "kcalc.desktop"], kcalc.OtherIds);
        Assert.Equal(Path.Combine(iconDir, "kcalc_kcalc.png"), kcalc.IconPath);
        Assert.Equal("accessories-calculator", kcalc.IconName);
        // The default picture first, addresses completed from the catalog's media server,
        // and the one on an unknown server left out.
        Assert.Equal(
            [
                "https://appstream.ubuntu.com/media/noble/org/kde/kcalc.desktop/first_624x351.png",
                "https://appstream.ubuntu.com/media/noble/org/kde/kcalc.desktop/second_752x423.png",
            ],
            kcalc.Screenshots);
        Assert.False(kcalc.MakerVerified);
        Assert.Null(entries[1].Description);
        Assert.Null(entries[1].IconPath);
    }

    [Theory]
    [InlineData("de-AT", "KCalc (de)", "Wissenschaftlicher Rechner")]
    [InlineData("pt-BR", "KCalc (pt)", "Scientific Calculator")]
    [InlineData("fr-FR", "KCalc", "Scientific Calculator")]
    public void Picks_the_users_language(string culture, string name, string summary)
    {
        var kcalc = Read(Catalog, culture)[0];

        Assert.Equal(name, kcalc.Name);
        Assert.Equal(summary, kcalc.Summary);
    }

    [Fact]
    public void Reads_gzip_files_through_links_and_survives_damage()
    {
        var real = Path.Combine(_root, "real.yml.gz");
        using (var gzip = new GZipStream(File.Create(real), CompressionLevel.Fastest))
            gzip.Write(Encoding.UTF8.GetBytes(Catalog));
        var link = Path.Combine(_root, "link.yml.gz");
        File.CreateSymbolicLink(link, real);
        var truncated = Path.Combine(_root, "cut.yml");
        File.WriteAllText(truncated, Catalog[..Catalog.IndexOf("Type: font", StringComparison.Ordinal)] + "Type: desktop-application\nName:\n  C: [unclosed");
        var garbage = Path.Combine(_root, "garbage.yml.gz");
        File.WriteAllText(garbage, "not gzip");

        Assert.Equal(2, Dep11Yaml.ReadFile(link, "packagekit", _root, CultureInfo.InvariantCulture).Count);
        Assert.Equal("KCalc", Assert.Single(Dep11Yaml.ReadFile(truncated, "packagekit", _root, CultureInfo.InvariantCulture)).Name);
        Assert.Empty(Dep11Yaml.ReadFile(garbage, "packagekit", _root));
        Assert.Empty(Dep11Yaml.ReadFile(Path.Combine(_root, "missing.yml"), "packagekit", _root));
    }

    [Fact]
    public void Entries_before_a_header_are_ignored()
    {
        Assert.Empty(Read("---\nType: desktop-application\nID: a.desktop\nName:\n  C: A\n"));
    }

    [Fact]
    public void The_store_reader_and_the_name_index_both_understand_these_catalogs()
    {
        var file = Path.Combine(_root, "catalog.yml");
        File.WriteAllText(file, Catalog);

        var store = new StoreCatalogReader(new XdgDirs(_root, _ => null), null, CultureInfo.InvariantCulture)
            .Read([new(file, "packagekit", null, _root)], TestContext.Current.CancellationToken);
        Assert.Equal(["KCalc", "Plain"], store.Select(e => e.Name));

        var merged = StoreCatalog.Merge(store);
        Assert.Equal([AppCategory.Utilities], merged.Single(a => a.Name == "KCalc").Categories);
        Assert.Contains("kcalc", merged.Single(a => a.Name == "KCalc").KnownIds);
    }
}
