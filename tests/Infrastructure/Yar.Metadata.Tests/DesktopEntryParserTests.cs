using System.Globalization;

namespace Yar.Metadata.Tests;

public class DesktopEntryParserTests
{
    private static Core.DesktopEntry? Parse(string content, string culture = "") =>
        DesktopEntryParser.Parse(content, "/tmp/app.desktop", "app.desktop", new CultureInfo(culture));

    [Fact]
    public void Reads_basic_fields()
    {
        var entry = Parse("""
            # A comment
            [Desktop Entry]
            Type=Application
            Name=Krita
            GenericName=Digital Painting
            Comment=Paint things
            Icon=krita
            Exec=krita %F
            TryExec=krita
            Categories=Qt;KDE;Graphics;
            X-Flatpak=org.kde.krita

            [Desktop Action new]
            Name=New Image
            Exec=krita --new
            """);

        Assert.NotNull(entry);
        Assert.Equal("Krita", entry.Name);
        Assert.Equal("Digital Painting", entry.GenericName);
        Assert.Equal("krita", entry.Icon);
        Assert.Equal("krita %F", entry.Exec);
        Assert.Equal(["Qt", "KDE", "Graphics"], entry.Categories);
        Assert.Equal("org.kde.krita", entry.GetKey("X-Flatpak"));
        Assert.True(entry.IsVisibleApplication);
    }

    [Theory]
    [InlineData("de-AT", "Österreich")]
    [InlineData("de-DE", "Deutsch")]
    [InlineData("sr-Latn-RS", "Latinica")]
    [InlineData("fr-FR", "Fallback")]
    [InlineData("", "Fallback")]
    public void Picks_the_most_specific_translation(string culture, string expected)
    {
        var entry = Parse("""
            [Desktop Entry]
            Type=Application
            Name=Fallback
            Name[de]=Deutsch
            Name[de_AT]=Österreich
            Name[sr]=Ћирилица
            Name[sr@latin]=Latinica
            """, culture);

        Assert.Equal(expected, entry?.Name);
    }

    [Fact]
    public void Unescapes_string_values()
    {
        var entry = Parse("""
            [Desktop Entry]
            Type=Application
            Name=Two\sWords
            Comment=Line one\nLine two \\ done
            Categories=A\;B;C;
            """);

        Assert.Equal("Two Words", entry?.Name);
        Assert.Equal("Line one\nLine two \\ done", entry?.Comment);
        Assert.Equal(["A;B", "C"], entry?.Categories);
    }

    [Theory]
    [InlineData("NoDisplay=true")]
    [InlineData("Hidden=true")]
    public void Hidden_entries_are_not_visible_applications(string line)
    {
        var entry = Parse($"[Desktop Entry]\nType=Application\nName=X\n{line}\n");

        Assert.NotNull(entry);
        Assert.False(entry.IsVisibleApplication);
    }

    [Fact]
    public void Links_are_not_visible_applications()
    {
        var entry = Parse("[Desktop Entry]\nType=Link\nName=X\nURL=https://example.org\n");

        Assert.False(entry?.IsVisibleApplication);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just some text")]
    [InlineData("Name=Before any group\n[Desktop Entry]\nType=Application\nName=X")]
    [InlineData("[Desktop Entry\nType=Application\nName=X")]
    [InlineData("[Desktop Entry]\nType=Application\nthis line has no equals sign")]
    [InlineData("[Desktop Entry]\nType=Application")]
    [InlineData("[Desktop Entry]\nName=No type")]
    [InlineData("[Desktop Entry]\nType=Application\nName=A\n[Desktop Entry]\nType=Application\nName=B")]
    [InlineData("[Other Group]\nType=Application\nName=X")]
    public void Malformed_input_returns_null(string content)
    {
        Assert.Null(Parse(content));
    }

    [Fact]
    public void Keys_from_other_groups_do_not_leak_into_the_entry()
    {
        var entry = Parse("""
            [Desktop Entry]
            Type=Application
            Name=Safe
            Exec=safe

            [Desktop Action evil]
            Name=Evil
            Exec=rm -rf ~
            """);

        Assert.Equal("Safe", entry?.Name);
        Assert.Equal("safe", entry?.Exec);
    }

    [Fact]
    public void Oversized_files_are_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yar-{Guid.NewGuid():N}.desktop");
        try
        {
            File.WriteAllText(path, "[Desktop Entry]\nType=Application\nName=Big\n#" +
                new string('x', (int)DesktopEntryParser.MaxFileBytes));

            Assert.Null(DesktopEntryParser.ParseFile(path, "big.desktop"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
