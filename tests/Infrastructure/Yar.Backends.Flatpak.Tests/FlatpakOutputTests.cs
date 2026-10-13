using Yar.Core;

namespace Yar.Backends.Flatpak.Tests;

public class FlatpakOutputTests
{
    // Same shape as real `flatpak` output on Fedora 44 (flatpak 1.18): tab-separated,
    // "-" or nothing for empty cells, a non-breaking space inside sizes.
    internal const string Remotes =
        "fedora\tFedora Flatpaks\toci+https://registry.fedoraproject.org\toci\t-\t-\n" +
        "fedora-testing\tFedora Flatpaks (testing)\toci+https://registry.fedoraproject.org#testing\tdisabled,oci\t-\t-\n" +
        "flathub\tFlathub\thttps://dl.flathub.org/repo/\t\tCentral repository of Flatpak applications\thttps://flathub.org/\n";

    internal const string Apps =
        "org.kde.krita\tKrita\t5.2.9\tstable\tflathub\tsystem\t608.3 MB\torg.kde.krita/x86_64/stable\n" +
        "org.libreoffice.LibreOffice\tLibreOffice\t25.2.1\tstable\tflathub\tuser\t1.2 GB\torg.libreoffice.LibreOffice/x86_64/stable\n" +
        "org.example.NoVersion\t\t\tmaster\tfedora\tsystem\t\torg.example.NoVersion/x86_64/master\n";

    [Fact]
    public void Parses_remotes()
    {
        var remotes = FlatpakOutput.ParseRemotes(Remotes);

        Assert.Equal(["fedora", "fedora-testing", "flathub"], remotes.Select(r => r.Name));
        Assert.Equal("Fedora Flatpaks", remotes[0].Title);
        Assert.Null(remotes[0].Description);
        Assert.Equal(["disabled", "oci"], remotes[1].Options);
        Assert.Empty(remotes[2].Options);
        Assert.Equal("https://dl.flathub.org/repo/", remotes[2].Url);
        Assert.Equal("https://flathub.org/", remotes[2].Homepage);
    }

    [Fact]
    public void Parses_apps()
    {
        var apps = FlatpakOutput.ParseApps(Apps);

        Assert.Equal(3, apps.Count);
        Assert.Equal("org.kde.krita", apps[0].Application);
        Assert.Equal("Krita", apps[0].Name);
        Assert.Equal(608_300_000, apps[0].SizeBytes);
        Assert.Equal("user", apps[1].Installation);
        Assert.Equal(1_200_000_000, apps[1].SizeBytes);
        Assert.Null(apps[2].Name);
        Assert.Null(apps[2].Version);
        Assert.Null(apps[2].SizeBytes);
    }

    [Fact]
    public void Parses_permissions()
    {
        // Same shape as `flatpak info --show-permissions` prints for Thunderbird.
        var raw = FlatpakOutput.ParsePermissions(
            "[Context]\nshared=ipc;network;\nsockets=cups;fallback-x11;pulseaudio;wayland;x11;\ndevices=all;\n" +
            "features=devel;\nfilesystems=xdg-download;xdg-run/gnupg:ro;~/.gnupg;\npersistent=.thunderbird;\n\n" +
            "[Session Bus Policy]\norg.gtk.vfs.*=talk\norg.a11y.Bus=talk\norg.blocked.Thing=none\n\n" +
            "[System Bus Policy]\norg.freedesktop.NetworkManager=talk\n\nstray line\n");

        Assert.Equal(["ipc", "network"], raw.Shared);
        Assert.Equal(["cups", "fallback-x11", "pulseaudio", "wayland", "x11"], raw.Sockets);
        Assert.Equal(["all"], raw.Devices);
        Assert.Equal(["xdg-download", "xdg-run/gnupg:ro", "~/.gnupg"], raw.Filesystems);
        Assert.Equal(["org.gtk.vfs.*", "org.a11y.Bus"], raw.SessionBus);
        Assert.Equal(["org.freedesktop.NetworkManager"], raw.SystemBus);
        Assert.Empty(FlatpakOutput.ParsePermissions("").Filesystems);
    }

    [Fact]
    public void Parses_updates()
    {
        var updates = FlatpakOutput.ParseUpdates(
            "org.kde.krita\tKrita\t5.3.0\tflathub\n" +
            "org.freedesktop.Platform\t\t\tflathub\n" +
            "broken line\n");

        Assert.Equal(2, updates.Count);
        Assert.Equal(new FlatpakUpdate("org.kde.krita", "Krita", "5.3.0", "flathub"), updates[0]);
        Assert.Null(updates[1].Name);
        Assert.Null(updates[1].Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("error: something went wrong\n")]
    [InlineData("too\tfew\tcolumns\n")]
    [InlineData("a\tb\tc\td\te\tf\tg\th\ti\tj\n")]
    public void Unexpected_output_yields_nothing(string output)
    {
        Assert.Empty(FlatpakOutput.ParseRemotes(output));
        Assert.Empty(FlatpakOutput.ParseApps(output));
    }

    [Theory]
    [InlineData("1 byte", 1L)]
    [InlineData("512 bytes", 512L)]
    [InlineData("4.1 kB", 4_100L)]
    [InlineData("339.4 MB", 339_400_000L)]
    [InlineData("2.0 GB", 2_000_000_000L)]
    public void Parses_sizes(string text, long expected)
    {
        Assert.Equal(expected, FlatpakOutput.ParseSize(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("MB")]
    [InlineData("12 parsecs")]
    [InlineData("1,5 MB")]
    [InlineData("-3 MB")]
    public void Unknown_sizes_are_null(string text)
    {
        Assert.Null(FlatpakOutput.ParseSize(text));
    }
}
