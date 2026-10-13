namespace Yar.Core.Tests;

public class PermissionAndAttentionTests
{
    private static RawPermissions Raw(
        string[]? shared = null, string[]? sockets = null, string[]? devices = null, string[]? filesystems = null,
        string[]? sessionBus = null, string[]? systemBus = null) =>
        new(shared ?? [], sockets ?? [], devices ?? [], filesystems ?? [], sessionBus ?? [], systemBus ?? []);

    [Fact]
    public void An_app_that_asks_for_nothing_is_well_separated()
    {
        var report = FlatpakPermissions.Describe(RawPermissions.None);

        Assert.Equal(PermissionWeight.Minor, report.Verdict);
        Assert.Empty(report.Notes);
    }

    [Theory]
    [InlineData("host", PermissionWeight.Broad, "Can read and change all the files on this computer")]
    [InlineData("host:ro", PermissionWeight.Broad, "Can read all the files on this computer")]
    [InlineData("home", PermissionWeight.Broad, "Can read and change everything in your home folder")]
    [InlineData("xdg-config", PermissionWeight.Broad, "Can read and change the settings and data of your other apps")]
    [InlineData("xdg-download", PermissionWeight.Some, "Can read and change your Downloads folder")]
    [InlineData("xdg-documents:ro", PermissionWeight.Some, "Can read your Documents folder")]
    [InlineData("~/.gnupg", PermissionWeight.Some, "Can read and change one specific folder")]
    [InlineData("xdg-run/gvfs", PermissionWeight.Minor, "Can reach a specific system service")]
    public void Describes_file_access(string filesystem, PermissionWeight weight, string text)
    {
        var note = Assert.Single(FlatpakPermissions.Describe(Raw(filesystems: [filesystem])).Notes);

        Assert.Equal(weight, note.Weight);
        Assert.Equal(text, note.Text);
    }

    [Fact]
    public void Access_that_was_taken_away_is_not_listed()
    {
        Assert.Empty(FlatpakPermissions.Describe(Raw(filesystems: ["!home", "!host"])).Notes);
    }

    [Fact]
    public void The_widest_access_decides_the_verdict_and_comes_first()
    {
        // Krita as Flathub lists it.
        var krita = FlatpakPermissions.Describe(Raw(
            shared: ["ipc", "network"], sockets: ["pulseaudio", "x11"], devices: ["dri"],
            filesystems: ["host", "xdg-run/gvfs"], sessionBus: ["com.canonical.AppMenu.Registrar"]));

        Assert.Equal(PermissionWeight.Broad, krita.Verdict);
        Assert.Equal("Can read and change all the files on this computer", krita.Notes[0].Text);
        Assert.Contains(krita.Notes, n => n is { Weight: PermissionWeight.Some, Text: "Can use the internet" });
        Assert.Contains(krita.Notes, n => n.Text.StartsWith("Can see and control other windows", StringComparison.Ordinal));
        Assert.Contains(krita.Notes, n => n is { Weight: PermissionWeight.Minor, Detail: "com.canonical.AppMenu.Registrar" });
        Assert.Equal(krita.Notes.OrderByDescending(n => n.Weight).Select(n => n.Weight), krita.Notes.Select(n => n.Weight));

        var modest = FlatpakPermissions.Describe(Raw(shared: ["network"], sockets: ["wayland", "fallback-x11"], devices: ["dri"]));
        Assert.Equal(PermissionWeight.Some, modest.Verdict);
        // Wayland and fallback-x11 both mean "can show its own windows": said once.
        Assert.Single(modest.Notes, n => n.Text == "Can show its own windows");
    }

    [Theory]
    [InlineData("org.freedesktop.Flatpak", PermissionWeight.Broad)]
    [InlineData("org.freedesktop.secrets", PermissionWeight.Some)]
    [InlineData("org.kde.StatusNotifierWatcher", PermissionWeight.Minor)]
    public void Weighs_which_programs_it_may_talk_to(string name, PermissionWeight weight)
    {
        Assert.Equal(weight, FlatpakPermissions.Describe(Raw(sessionBus: [name])).Verdict);
    }

    [Fact]
    public void Unknown_values_are_shown_not_dropped()
    {
        var report = FlatpakPermissions.Describe(Raw(sockets: ["brand-new-socket"], devices: ["all", "quantum"]));

        Assert.Contains(report.Notes, n => n.Detail == "brand-new-socket");
        Assert.Contains(report.Notes, n => n.Detail == "quantum");
        Assert.Equal(PermissionWeight.Broad, report.Verdict);
    }

    private static AppInfo App(string provider, string name, Dictionary<string, string>? technical = null, string? launcher = null) => new(
        provider, name, name, null, null, null, null, null, 1234, null, launcher is null ? [] : [launcher], null,
        technical ?? [])
    {
        InstallPath = "/home/ada/Applications/" + name,
    };

    private static CatalogSnapshot Snapshot(
        AppInfo[] apps, DuplicateGroup[]? duplicates = null, LeftoverData[]? leftovers = null) =>
        new([], [], apps, duplicates ?? [], [], [], [])
        {
            Leftovers = leftovers ?? [],
        };

    [Fact]
    public void A_tidy_system_needs_no_attention()
    {
        Assert.Empty(AttentionFinder.Find(Snapshot([App("flatpak", "Krita"), App("manual", "Tool", launcher: "/a.desktop")])));
    }

    [Fact]
    public void Lists_duplicates_broken_launchers_loose_apps_and_leftovers()
    {
        var native = App("packagekit", "Firefox");
        var flatpak = App("flatpak", "Firefox");
        var broken = App("manual", "Gone", new() { ["broken"] = "true" }, "/home/ada/.local/share/applications/gone.desktop");
        var loose = App("manual", "Loose", new() { ["in-menu"] = "false" });

        var items = AttentionFinder.Find(Snapshot(
            [native, flatpak, broken, loose],
            [new DuplicateGroup("org.mozilla.firefox", [native, flatpak])],
            [
                new LeftoverData("flatpak", "com.small.App", "/home/ada/.var/app/com.small.App", 2_000_000),
                new LeftoverData("flatpak", "com.empty.App", "/home/ada/.var/app/com.empty.App", 2187),
                new LeftoverData("flatpak", "com.unmeasured.App", "/home/ada/.var/app/com.unmeasured.App", null),
                new LeftoverData("flatpak", "com.big.App", "/home/ada/.var/app/com.big.App", 900_000_000),
            ]));

        Assert.Equal(
            [AttentionKind.Duplicate, AttentionKind.BrokenLauncher, AttentionKind.NotInMenu,
                AttentionKind.LeftoverData, AttentionKind.LeftoverData, AttentionKind.LeftoverData],
            items.Select(i => i.Kind));
        Assert.Equal(2, items[0].Apps.Count);
        Assert.Equal("/home/ada/.local/share/applications/gone.desktop", items[1].Path);
        Assert.Equal("/home/ada/Applications/Loose", items[2].Path);
        Assert.Equal(1234, items[2].SizeBytes);
        // Biggest first; one that could not be measured counts as possibly big; a near-empty one is left out.
        Assert.Equal(["com.unmeasured.App", "com.big.App", "com.small.App"], items.Skip(3).Select(i => i.Title));
    }
}
