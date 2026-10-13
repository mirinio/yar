using System.Globalization;

namespace Yar.Metadata.Tests;

public sealed class DesktopEntryScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("yar-scan-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string dataDir, string relativePath, string body)
    {
        var path = Path.Combine(_root, dataDir, "applications", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[Desktop Entry]\nType=Application\n" + body + "\n");
        return path;
    }

    private Core.DesktopEntryIndex Scan(params string[] dataDirs) =>
        new DesktopEntryScanner(
            dataDirs.Select(d => Path.Combine(_root, d, "applications")).ToList(),
            CultureInfo.InvariantCulture).Scan();

    [Fact]
    public void Finds_visible_applications_and_builds_desktop_ids()
    {
        Write("system", "krita.desktop", "Name=Krita");
        Write("system", "kde/okular.desktop", "Name=Okular");
        Write("system", "hidden.desktop", "Name=Hidden\nNoDisplay=true");
        Write("system", "broken.desktop", "this is not valid");
        File.WriteAllText(Path.Combine(_root, "system", "applications", "mimeinfo.cache"), "[MIME Cache]\n");

        var index = Scan("system", "does-not-exist");

        Assert.Equal(["krita.desktop", "kde-okular.desktop"], index.Entries.Select(e => e.DesktopId));
    }

    [Fact]
    public void Launchers_meant_for_other_desktops_or_missing_programs_are_left_out()
    {
        Write("system", "everywhere.desktop", "Name=Everywhere");
        Write("system", "kde-only.desktop", "Name=KDE only\nOnlyShowIn=KDE;");
        Write("system", "gnome-only.desktop", "Name=GNOME only\nOnlyShowIn=GNOME;Unity;");
        Write("system", "not-kde.desktop", "Name=Not KDE\nNotShowIn=XFCE;KDE");
        Write("system", "has-program.desktop", "Name=Has program\nTryExec=sh");
        Write("system", "lost-program.desktop", "Name=Lost program\nTryExec=yar-no-such-program");
        Write("system", "empty.desktop", "");
        File.WriteAllText(Path.Combine(_root, "system", "applications", "empty.desktop"), "");
        IReadOnlyList<string> dirs = [Path.Combine(_root, "system", "applications")];

        var onKde = new DesktopEntryScanner(dirs, CultureInfo.InvariantCulture, ["KDE"], ["/bin", "/usr/bin"]).Scan();
        Assert.Equal(["Everywhere", "Has program", "KDE only"], onKde.Entries.Select(e => e.Name));

        var onUbuntu = new DesktopEntryScanner(dirs, CultureInfo.InvariantCulture, ["ubuntu", "GNOME"], ["/bin", "/usr/bin"]).Scan();
        Assert.Equal(["Everywhere", "GNOME only", "Has program", "Not KDE"], onUbuntu.Entries.Select(e => e.Name));

        // Without knowing the desktop or where programs live, nothing is left out on those grounds.
        Assert.Equal(6, new DesktopEntryScanner(dirs, CultureInfo.InvariantCulture).Scan().Entries.Count);
    }

    [Fact]
    public void Earlier_directories_shadow_later_ones()
    {
        var userPath = Write("user", "app.desktop", "Name=User Version");
        Write("system", "app.desktop", "Name=System Version");

        var entry = Assert.Single(Scan("user", "system").Entries);

        Assert.Equal("User Version", entry.Name);
        Assert.Equal(userPath, entry.Path);
    }

    [Fact]
    public void A_hidden_user_entry_hides_the_system_launcher()
    {
        Write("user", "app.desktop", "Name=App\nHidden=true");
        Write("system", "app.desktop", "Name=App");

        Assert.Empty(Scan("user", "system").Entries);
    }
}
