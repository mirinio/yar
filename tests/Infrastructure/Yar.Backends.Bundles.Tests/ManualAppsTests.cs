using Yar.Core;

namespace Yar.Backends.Bundles.Tests;

public sealed class ManualAppsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("yar-manual-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string LauncherDir => Path.Combine(_root, "applications");
    private string AppDir => Path.Combine(_root, "Applications");

    private static string WriteFile(string directory, string name, byte[] header)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var content = new byte[8192];
        header.CopyTo(content, 0);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] AppImageHeader(byte type) => [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, (byte)'A', (byte)'I', type];

    private DesktopEntry Launcher(string name, string? exec, string? icon = null, string? directory = null) => new(
        Path.Combine(directory ?? LauncherDir, name + ".desktop"), name + ".desktop", "Application", name,
        null, null, icon, exec, null, false, false, [], new Dictionary<string, string>());

    private ManualAppsProvider Provider() => new([LauncherDir], [AppDir, Path.Combine(_root, "missing")], [AppDir]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Recognises_appimage_headers(byte type)
    {
        Assert.Equal(type, AppImageFile.DetectType(WriteFile(AppDir, "x.AppImage", AppImageHeader(type))));
    }

    [Fact]
    public void Other_files_are_not_appimages()
    {
        byte[] plainElf = [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0];

        Assert.Null(AppImageFile.DetectType(WriteFile(AppDir, "tool", plainElf)));
        Assert.Null(AppImageFile.DetectType(WriteFile(AppDir, "fake.AppImage", "#!/bin/sh\necho AI"u8.ToArray())));
        Assert.Null(AppImageFile.DetectType(WriteFile(AppDir, "odd", AppImageHeader(3))));
        Assert.Null(AppImageFile.DetectType(Path.Combine(AppDir, "missing")));
        Assert.Null(AppImageFile.DetectType(AppImageHeader(2).AsSpan(0, 10)));

        var tiny = Path.Combine(AppDir, "tiny.AppImage");
        File.WriteAllBytes(tiny, AppImageHeader(2));
        Assert.Null(AppImageFile.DetectType(tiny));
    }

    [Fact]
    public async Task Claims_only_launchers_in_its_directories()
    {
        var mine = Launcher("mine", "/bin/sh");
        var system = Launcher("system", "/bin/sh", directory: "/usr/share/applications");
        // A directory that merely starts with the same characters must not match.
        var lookalike = Launcher("lookalike", "/bin/sh", directory: LauncherDir + "-other");

        var apps = await Provider().ClaimAppsAsync(new DesktopEntryIndex([mine, system, lookalike]), Token);

        var app = Assert.Single(apps);
        Assert.Equal([mine.Path], app.LauncherPaths);
        Assert.Equal(ManualAppsProvider.SourceId, app.SourceId);
        Assert.Equal("/bin/sh", app.Technical["program"]);
        Assert.False(app.Technical.ContainsKey("broken"));
        // /bin/sh is a shared system tool, not this app's own folder.
        Assert.Null(app.InstallPath);
    }

    [Fact]
    public async Task Flags_launchers_whose_program_is_gone()
    {
        var apps = await Provider().ClaimAppsAsync(
            new DesktopEntryIndex([Launcher("gone", "/opt/removed/app %U", icon: "/opt/removed/icon.png")]), Token);

        var app = Assert.Single(apps);
        Assert.Equal("true", app.Technical["broken"]);
        Assert.Equal("/opt/removed/icon.png", app.IconPath);
        Assert.Null(app.IconName);
    }

    [Fact]
    public async Task An_appimage_with_a_launcher_is_listed_once()
    {
        var integrated = WriteFile(AppDir, "Krita.AppImage", AppImageHeader(2));
        var loose = WriteFile(AppDir, "My Tool-1.2.AppImage", AppImageHeader(2));
        WriteFile(AppDir, "notes.txt", "hello"u8.ToArray());

        var apps = await Provider().ClaimAppsAsync(
            new DesktopEntryIndex([Launcher("krita", $"\"{integrated}\" %F", icon: "krita")]), Token);

        Assert.Equal(2, apps.Count);
        Assert.Equal("krita", apps[0].Name);
        Assert.Equal("2", apps[0].Technical["appimage-type"]);
        Assert.Equal(8192, apps[0].SizeBytes);
        Assert.Equal("My Tool-1.2", apps[1].Name);
        Assert.Equal(loose, apps[1].Id);
        Assert.Empty(apps[1].LauncherPaths);
        Assert.Equal("false", apps[1].Technical["in-menu"]);
        Assert.Equal(integrated, apps[0].InstallPath);
        Assert.Equal(loose, apps[1].InstallPath);
    }
}
