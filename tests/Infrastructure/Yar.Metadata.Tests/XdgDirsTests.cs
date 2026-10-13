namespace Yar.Metadata.Tests;

public class XdgDirsTests
{
    private static XdgDirs Create(Dictionary<string, string>? environment = null) =>
        new("/home/ada", name => environment?.GetValueOrDefault(name));

    [Fact]
    public void Uses_spec_defaults_when_nothing_is_set()
    {
        var dirs = Create();

        Assert.Equal("/home/ada/.local/share", dirs.DataHome);
        Assert.Equal("/home/ada/.config", dirs.ConfigHome);
        Assert.Equal("/home/ada/.cache", dirs.CacheHome);
        Assert.Equal(["/usr/local/share", "/usr/share"], dirs.DataDirs);
        Assert.Equal(
            ["/home/ada/.local/share/applications", "/usr/local/share/applications", "/usr/share/applications"],
            dirs.ApplicationDirs);
    }

    [Fact]
    public void Honours_the_environment_and_ignores_invalid_entries()
    {
        var dirs = Create(new()
        {
            ["XDG_DATA_HOME"] = "/data/ada/",
            ["XDG_CONFIG_HOME"] = "relative/config",
            ["XDG_DATA_DIRS"] = "/var/lib/flatpak/exports/share/::relative:/usr/share:/usr/share/",
        });

        Assert.Equal("/data/ada", dirs.DataHome);
        Assert.Equal("/home/ada/.config", dirs.ConfigHome);
        Assert.Equal(["/var/lib/flatpak/exports/share", "/usr/share"], dirs.DataDirs);
        Assert.Equal("/data/ada/applications", dirs.UserApplicationsDir);
    }
}
