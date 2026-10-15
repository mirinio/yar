using Yar.Core;

namespace Yar.Backends.Flatpak.Tests;

public class FlatpakProviderTests
{
    private const string UserDir = "/home/ada/.local/share/flatpak";

    private static DesktopEntry Launcher(string path, string flatpakId, string name) => new(
        path, Path.GetFileName(path), "Application", name, null, $"About {name}", flatpakId, "run", null,
        false, false, [], new Dictionary<string, string> { ["X-Flatpak"] = flatpakId });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_system_and_user_remotes()
    {
        var runner = new FakeRunner(arguments => new(
            0, arguments.Contains("--system") ? FlatpakOutputTests.Remotes : "userhub\tMy Hub\thttps://example.org/\tno-gpg-verify\t-\t-\n", ""));
        var provider = new FlatpakProvider(runner, UserDir, "/usr/bin/flatpak");

        var sources = await provider.GetSourcesAsync(Token);

        Assert.Equal(["system:fedora", "system:fedora-testing", "system:flathub", "user:userhub"], sources.Select(s => s.Id));
        Assert.False(sources[1].Enabled);
        Assert.True(sources[2].Enabled);
        Assert.Equal("Flathub", sources[2].Name);
        Assert.Equal(SourceScope.User, sources[3].Scope);
        Assert.Equal("off", sources[3].Technical["signature-check"]);
        Assert.All(runner.Calls, call => Assert.Contains("--show-disabled", call));
    }

    [Fact]
    public async Task Claims_launchers_by_app_id_and_installation()
    {
        var runner = new FakeRunner(_ => new(0, FlatpakOutputTests.Apps, ""));
        var provider = new FlatpakProvider(runner, UserDir, "/usr/bin/flatpak");
        var system = "/var/lib/flatpak/exports/share/applications/";
        var user = UserDir + "/exports/share/applications/";
        var index = new DesktopEntryIndex(
        [
            Launcher(system + "org.kde.krita.desktop", "org.kde.krita", "Krita"),
            Launcher(user + "org.libreoffice.LibreOffice.calc.desktop", "org.libreoffice.LibreOffice", "Calc"),
            Launcher(user + "org.libreoffice.LibreOffice.desktop", "org.libreoffice.LibreOffice", "LibreOffice"),
            // Same app ID, but exported by the system installation: not this user-installed app's launcher.
            Launcher(system + "org.libreoffice.LibreOffice.desktop", "org.libreoffice.LibreOffice", "LibreOffice"),
            Launcher("/usr/share/applications/firefox.desktop", "", "Firefox"),
        ]);

        var apps = await provider.ClaimAppsAsync(index, Token);

        Assert.Equal(3, apps.Count);
        var krita = apps[0];
        Assert.Equal("system:flathub", krita.SourceId);
        Assert.Equal([system + "org.kde.krita.desktop"], krita.LauncherPaths);
        Assert.Equal("About Krita", krita.Summary);
        Assert.Equal("org.kde.krita", krita.ComponentId);

        var office = apps[1];
        Assert.Equal("user:flathub", office.SourceId);
        Assert.Equal(
            [user + "org.libreoffice.LibreOffice.desktop", user + "org.libreoffice.LibreOffice.calc.desktop"],
            office.LauncherPaths);

        var bare = apps[2];
        Assert.Equal("org.example.NoVersion", bare.Name);
        Assert.Empty(bare.LauncherPaths);
        Assert.Null(bare.SizeBytes);
    }

    [Fact]
    public async Task Finds_the_install_folder_per_installation()
    {
        var root = Directory.CreateTempSubdirectory("yar-flatpak-").FullName;
        try
        {
            var systemDir = Path.Combine(root, "system");
            var userDir = Path.Combine(root, "user");
            var kritaFiles = Path.Combine(systemDir, "app", "org.kde.krita", "current", "active", "files");
            Directory.CreateDirectory(kritaFiles);
            // Krita also exists in the user installation; the system-installed app must not point there.
            Directory.CreateDirectory(Path.Combine(userDir, "app", "org.kde.krita", "current", "active", "files"));
            var provider = new FlatpakProvider(
                new FakeRunner(_ => new(0, FlatpakOutputTests.Apps, "")), userDir, "/usr/bin/flatpak", systemDir);

            var apps = await provider.ClaimAppsAsync(DesktopEntryIndex.Empty, Token);

            Assert.Equal(kritaFiles, apps[0].InstallPath);
            Assert.Null(apps[1].InstallPath);
            Assert.Equal([systemDir, userDir], provider.Facts.Locations.Select(l => l.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Lists_updates_for_apps_and_runtimes()
    {
        var runner = new FakeRunner(arguments => new(0,
            arguments.Contains("--system") && arguments.Contains("--app") ? "org.kde.krita\tKrita\t5.3.0\tflathub\n"
            : arguments.Contains("--user") && arguments.Contains("--runtime") ? "org.kde.Platform\tKDE Platform\t\tflathub\n"
            : "", ""));
        var provider = new FlatpakProvider(runner, UserDir, "/usr/bin/flatpak");
        var installed = await new FlatpakProvider(
            new FakeRunner(_ => new(0, FlatpakOutputTests.Apps, "")), UserDir, "/usr/bin/flatpak")
            .ClaimAppsAsync(DesktopEntryIndex.Empty, Token);

        var check = await provider.GetUpdatesAsync(installed, Token);

        Assert.Equal(4, runner.Calls.Count);
        Assert.All(runner.Calls, call => Assert.Contains("--updates", call));
        Assert.Equal(2, check.Updates.Count);
        var krita = check.Updates.Single(u => u.AppId is not null);
        Assert.Equal("system:org.kde.krita/x86_64/stable", krita.AppId);
        Assert.Equal("5.2.9", krita.CurrentVersion);
        Assert.Equal("5.3.0", krita.NewVersion);
        Assert.Equal("system:flathub", krita.SourceId);
        var runtime = check.Updates.Single(u => u.AppId is null);
        Assert.Equal("KDE Platform", runtime.Name);
        Assert.Equal("user:flathub", runtime.SourceId);
    }

    private static SourceInfo Source(string remote, SourceScope scope) => new(
        FlatpakProvider.ProviderId, $"{(scope == SourceScope.User ? "user" : "system")}:{remote}", remote, null, null,
        true, scope, TrustLevel.Unknown, new Dictionary<string, string> { ["remote"] = remote });

    [Fact]
    public async Task Adds_a_source_only_for_the_user_and_only_from_a_checked_address()
    {
        var runner = new FakeRunner(_ => new(0, "", ""));
        var provider = new FlatpakProvider(runner, UserDir, "/usr/bin/flatpak");

        await provider.AddAsync(new("My Source", "https://example.org/repo.flatpakrepo"), Token);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(
            ["remote-add", "--user", "--if-not-exists", "my-source", "https://example.org/repo.flatpakrepo"],
            runner.Calls[0]);
        Assert.Equal(["update", "--user", "--appstream", "my-source"], runner.Calls[1]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.AddAsync(new("x", "http://example.org/repo.flatpakrepo"), Token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.AddAsync(new("x", "--system"), Token));
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task Removes_only_sources_the_user_added_for_themselves()
    {
        var runner = new FakeRunner(_ => new(0, "", ""));
        var provider = new FlatpakProvider(runner, UserDir, "/usr/bin/flatpak");

        Assert.True(provider.CanRemove(Source("mine", SourceScope.User)));
        Assert.False(provider.CanRemove(Source("flathub", SourceScope.System)));
        Assert.False(provider.CanRemove(Source("--system", SourceScope.User)));

        await provider.RemoveAsync(Source("mine", SourceScope.User), Token);
        Assert.Equal(["remote-delete", "--user", "mine"], Assert.Single(runner.Calls));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RemoveAsync(Source("flathub", SourceScope.System), Token));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Switches_sources_on_and_off_in_their_own_installation()
    {
        var runner = new FakeRunner(_ => new(0, "", ""));
        var provider = new FlatpakProvider(runner, UserDir, "/usr/bin/flatpak");

        Assert.True(provider.CanToggle(Source("flathub", SourceScope.System)));
        Assert.False(provider.CanToggle(Source("--enable", SourceScope.User)));

        await provider.SetEnabledAsync(Source("mine", SourceScope.User), false, Token);
        await provider.SetEnabledAsync(Source("flathub", SourceScope.System), true, Token);

        Assert.Equal(["remote-modify", "--user", "--disable", "mine"], runner.Calls[0]);
        Assert.Equal(["remote-modify", "--system", "--enable", "flathub"], runner.Calls[1]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SetEnabledAsync(Source("--enable", SourceScope.User), true, Token));
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task Asks_flatpak_for_an_installed_apps_permissions()
    {
        var runner = new FakeRunner(_ => new(0, "[Context]\nshared=network;\nfilesystems=home;\n", ""));
        var provider = new FlatpakProvider(runner, UserDir, "/usr/bin/flatpak");
        AppInfo App(string providerId, string reference, string installation) => new(
            providerId, "x", "X", null, null, null, null, null, null, null, [], "org.x.App",
            new Dictionary<string, string> { ["ref"] = reference, ["installation"] = installation });

        var raw = await provider.GetPermissionsAsync(App("flatpak", "org.x.App/x86_64/stable", "user"), Token);

        Assert.Equal(["home"], raw?.Filesystems);
        Assert.Equal(["info", "--show-permissions", "--user", "org.x.App/x86_64/stable"], Assert.Single(runner.Calls));
        Assert.Null(await provider.GetPermissionsAsync(App("packagekit", "org.x.App/x86_64/stable", "system"), Token));
        Assert.Null(await provider.GetPermissionsAsync(App("flatpak", "--show-location", "system"), Token));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Finds_data_of_apps_that_are_gone_and_where_installed_apps_keep_theirs()
    {
        var root = Directory.CreateTempSubdirectory("yar-leftover-").FullName;
        try
        {
            var data = Path.Combine(root, "var-app");
            Directory.CreateDirectory(Path.Combine(data, "org.kde.krita", "config"));
            Directory.CreateDirectory(Path.Combine(data, "com.gone.App", "cache"));
            File.WriteAllBytes(Path.Combine(data, "com.gone.App", "cache", "blob"), new byte[3000]);
            File.WriteAllBytes(Path.Combine(data, "com.gone.App", "settings"), new byte[500]);
            // A link in that folder could point anywhere; it is neither followed nor reported.
            var elsewhere = Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;
            File.WriteAllBytes(Path.Combine(elsewhere, "huge"), new byte[9000]);
            Directory.CreateSymbolicLink(Path.Combine(data, "com.linked.App"), elsewhere);
            Directory.CreateSymbolicLink(Path.Combine(data, "com.gone.App", "link"), elsewhere);

            var provider = new FlatpakProvider(
                new FakeRunner(_ => new(0, FlatpakOutputTests.Apps, "")), UserDir, "/usr/bin/flatpak", appDataDir: data);
            var installed = await provider.ClaimAppsAsync(DesktopEntryIndex.Empty, Token);

            Assert.Equal(Path.Combine(data, "org.kde.krita"), installed[0].DataPath);
            Assert.Null(installed[1].DataPath);

            var leftover = Assert.Single(await provider.FindLeftoversAsync(installed, Token));
            Assert.Equal("com.gone.App", leftover.Id);
            Assert.Equal(Path.Combine(data, "com.gone.App"), leftover.Path);
            Assert.Equal(3500, leftover.SizeBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_data_folder_there_are_no_leftovers()
    {
        var provider = new FlatpakProvider(new FakeRunner(_ => new(0, "", "")), UserDir, "/usr/bin/flatpak");

        Assert.Empty(await provider.FindLeftoversAsync([], Token));
    }

    [Fact]
    public async Task A_refused_change_reports_what_flatpak_said()
    {
        var provider = new FlatpakProvider(
            new FakeRunner(_ => new(1, "", "error: Can't remove remote 'mine' with installed ref app/x\n")), UserDir, "/usr/bin/flatpak");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RemoveAsync(Source("mine", SourceScope.User), Token));

        Assert.Contains("installed ref", error.Message);
    }

    [Fact]
    public async Task One_failed_update_question_does_not_hide_the_other_answers()
    {
        var partly = new FlatpakProvider(new FakeRunner(arguments =>
            arguments.Contains("--user") ? new(1, "", "error: no user installation\n")
            : arguments.Contains("--app") ? new(0, "org.kde.krita\tKrita\t5.3.0\tflathub\n", "")
            : new(0, "", "")), UserDir, "/usr/bin/flatpak");

        Assert.Single((await partly.GetUpdatesAsync([], Token)).Updates);

        var never = new FlatpakProvider(new FakeRunner(_ => new(1, "", "error: offline\n")), UserDir, "/usr/bin/flatpak");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => never.GetUpdatesAsync([], Token));
        Assert.Contains("offline", error.Message);
    }

    [Fact]
    public async Task Flatpak_is_set_up_once_before_anything_else_is_asked()
    {
        var runner = new FakeRunner(_ => new(0, "", ""));
        var provider = new FlatpakProvider(runner, UserDir, "/usr/bin/flatpak");

        await Task.WhenAll(
            provider.GetSourcesAsync(Token), provider.ClaimAppsAsync(DesktopEntryIndex.Empty, Token),
            provider.GetUpdatesAsync([], Token));

        Assert.Equal(1, runner.SetUpCalls);
        Assert.Equal(7, runner.Calls.Count);
    }

    [Fact]
    public async Task A_failing_command_is_an_error_not_an_empty_list()
    {
        var provider = new FlatpakProvider(new FakeRunner(_ => new(1, "", "error: boom\n")), UserDir, "/usr/bin/flatpak");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ClaimAppsAsync(DesktopEntryIndex.Empty, Token));

        Assert.Contains("boom", error.Message);
    }
}
