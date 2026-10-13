using Yar.Core;

namespace Yar.Metadata.Tests;

/// <summary>The files that make Yar an app the desktop knows: they must agree with each other and with the code.</summary>
public class AppFilesTests
{
    private static string DataDir()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yar.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "data");
    }

    [Fact]
    public void The_menu_entry_is_one_yar_itself_would_show_and_is_named_after_the_app()
    {
        var path = Path.Combine(DataDir(), $"{AppIdentity.Id}.desktop");

        var entry = DesktopEntryParser.ParseFile(path, Path.GetFileName(path), System.Globalization.CultureInfo.InvariantCulture);

        Assert.NotNull(entry);
        Assert.True(entry.IsVisibleApplication);
        Assert.Equal("Yar", entry.Name);
        Assert.Equal(AppIdentity.Id, entry.Icon);
        Assert.Equal(["yar"], DesktopExec.Split(entry.Exec));
        Assert.Equal([AppCategory.System], AppCategories.FromDesktopCategories(entry.Categories));
    }

    [Fact]
    public void The_store_description_names_the_same_app_and_menu_entry()
    {
        var path = Path.Combine(DataDir(), $"{AppIdentity.Id}.metainfo.xml");

        var component = Assert.Single(AppStreamXml.ReadFile(path, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(AppIdentity.Id, component.Id);
        Assert.Equal("Yar", component.Name);
        Assert.Equal([$"{AppIdentity.Id}.desktop"], component.DesktopIds);
    }
}
