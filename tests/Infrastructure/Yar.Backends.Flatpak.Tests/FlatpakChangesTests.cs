using Yar.Core;

namespace Yar.Backends.Flatpak.Tests;

public class FlatpakChangesTests
{
    private const string UserDir = "/home/ada/.local/share/flatpak";
    private const string DataDir = "/home/ada/.var/app";

    private const string Info =
        "\nCalculator - Sums: quick and easy\n\n" +
        "            ID: org.gnome.Calculator\n" +
        "           Ref: app/org.gnome.Calculator/x86_64/stable\n" +
        "       Version: 51.1\n" +
        " Download Size: 2.0 MB\n" +
        "Installed Size: 5.1 MB\n" +
        "       Runtime: org.gnome.Platform/x86_64/51\n" +
        "       Subject: Merge: something (abc)\n";

    private const string RuntimeInfo = " Download Size: 400.0 MB\nInstalled Size: 1.1 GB\n";

    private readonly FakeTrash _trash = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private FlatpakProvider Provider(FakeRunner runner) =>
        new(runner, UserDir, "/usr/bin/flatpak", appDataDir: DataDir, trash: _trash);

    private static FakeRunner Answering(string runtimes = "", string metadata = "[Context]\nshared=network;\n") => new(arguments =>
        arguments[0] switch
        {
            "remote-info" when arguments.Contains("--show-metadata") => new(0, metadata, ""),
            "remote-info" when arguments[^1].StartsWith("runtime/", StringComparison.Ordinal) => new(0, RuntimeInfo, ""),
            "remote-info" => new(0, Info, ""),
            "list" => new(0, runtimes, ""),
            _ => new(0, "", ""),
        });

    private static SourceInfo Source(string remote, SourceScope scope, bool enabled = true) => new(
        "flatpak", $"{(scope == SourceScope.User ? "user" : "system")}:{remote}", remote, null, null, enabled, scope,
        TrustLevel.WellKnown, new Dictionary<string, string> { ["remote"] = remote });

    private static StoreOffer Offer(string source = "system:flathub", string? bundle = "app/org.gnome.Calculator/x86_64/stable") =>
        new("flatpak", source, "org.gnome.Calculator.desktop", bundle, null, false, null);

    private static AppInfo Installed(string installation = "user", string? dataPath = DataDir + "/org.gnome.Calculator") => new(
        "flatpak", $"{installation}:org.gnome.Calculator/x86_64/stable", "Calculator", null, null, null, "51.1",
        $"{installation}:flathub", 5_100_000, null, [], "org.gnome.Calculator",
        new Dictionary<string, string> { ["ref"] = "org.gnome.Calculator/x86_64/stable", ["installation"] = installation })
    {
        DataPath = dataPath,
    };

    private static readonly IProgress<OperationProgress> Quiet = new Progress<OperationProgress>();

    [Fact]
    public void An_app_can_go_wherever_its_source_is_set_up_with_the_users_own_place_first()
    {
        var provider = Provider(Answering());

        var both = provider.TargetsFor(Offer(), [Source("flathub", SourceScope.System), Source("flathub", SourceScope.User)]);
        var systemOnly = provider.TargetsFor(Offer(), [Source("flathub", SourceScope.System)]);
        var switchedOff = provider.TargetsFor(Offer(), [Source("flathub", SourceScope.System, enabled: false)]);

        Assert.Equal(["user", "system"], both.Select(t => t.Id));
        Assert.Equal("system", Assert.Single(systemOnly).Id);
        Assert.Contains("add this source for yourself", systemOnly[0].Explanation);
        Assert.Empty(switchedOff);
        Assert.Empty(provider.TargetsFor(Offer(source: "system:--evil"), [Source("--evil", SourceScope.System)]));
    }

    [Fact]
    public async Task The_plan_lists_the_app_and_a_missing_runtime_with_their_sizes()
    {
        var runner = Answering();
        var plan = await Provider(runner).PlanInstallAsync(Offer(), new("user", "", "", false), "Calculator", Token);

        Assert.Null(plan.Blocked);
        Assert.Equal("user:org.gnome.Calculator/x86_64/stable", plan.Target);
        Assert.Equal(["Calculator", "org.gnome.Platform 51"], plan.Items.Select(i => i.Name));
        Assert.Equal(5_100_000, plan.Items[0].Bytes);
        Assert.Equal(402_000_000, plan.DownloadBytes);
        Assert.Equal(1_105_100_000, plan.DiskBytes);
        Assert.Empty(plan.Warnings);
        Assert.Equal("user:flathub", plan.Origin);
        Assert.Equal(["remote-info", "--user", "flathub", "app/org.gnome.Calculator/x86_64/stable"], runner.Calls[0]);
    }

    [Fact]
    public async Task A_runtime_that_is_already_there_is_not_counted_and_broad_access_is_a_warning()
    {
        var runner = Answering(
            runtimes: "org.gnome.Platform\t51\n", metadata: "[Context]\nfilesystems=host;\n");

        var plan = await Provider(runner).PlanInstallAsync(Offer(), new("system", "", "", false), "Calculator", Token);

        Assert.Equal("Calculator", Assert.Single(plan.Items).Name);
        Assert.Equal(2_000_000, plan.DownloadBytes);
        Assert.Contains("broad access", Assert.Single(plan.Warnings));
    }

    [Fact]
    public async Task Installing_runs_one_exact_command()
    {
        var runner = Answering();
        var provider = Provider(runner);
        var plan = await provider.PlanInstallAsync(Offer(), new("user", "", "", false), "Calculator", Token);
        runner.Calls.Clear();

        await provider.InstallAsync(plan, Quiet, Token);

        Assert.Equal(
            ["install", "--user", "--noninteractive", "flathub", "app/org.gnome.Calculator/x86_64/stable"],
            Assert.Single(runner.Calls));
    }

    [Theory]
    [InlineData("--system", "flathub", "user")]
    [InlineData("app/org.x/x86_64/stable", "--from", "user")]
    [InlineData("app/org.x/x86_64/stable", "flathub", "--installation=/tmp")]
    [InlineData("runtime/org.x/x86_64/stable", "flathub", "user")]
    [InlineData("app/org.x/x86_64/stable extra", "flathub", "user")]
    public async Task A_plan_that_names_anything_but_one_app_is_not_carried_out(string reference, string remote, string installation)
    {
        var runner = Answering();
        var plan = new ChangePlan("flatpak", "x", "X", [])
        {
            Technical = new Dictionary<string, string> { ["ref"] = reference, ["remote"] = remote, ["installation"] = installation },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(runner).InstallAsync(plan, Quiet, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(runner).RemoveAsync(plan, true, Quiet, Token));
        Assert.Empty(runner.Calls);
        Assert.Empty(_trash.Moved);
    }

    [Fact]
    public async Task A_failed_install_reports_what_flatpak_said()
    {
        var runner = new FakeRunner(arguments => arguments[0] == "install"
            ? new(1, "", "Looking for matches…\nerror: No space left on device\n")
            : Answering().RunAsync("/usr/bin/flatpak", arguments, TimeSpan.Zero, default).Result);
        var provider = Provider(runner);
        var plan = await provider.PlanInstallAsync(Offer(), new("user", "", "", false), "Calculator", Token);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InstallAsync(plan, Quiet, Token));

        Assert.Equal("No space left on device", error.Message);
    }

    [Fact]
    public async Task Removing_keeps_the_data_unless_asked_and_never_erases_it()
    {
        var runner = Answering();
        var provider = Provider(runner);
        var plan = await provider.PlanRemovalAsync(Installed(), Token);

        var kept = await provider.RemoveAsync(plan, removeData: false, Quiet, Token);
        var trashed = await provider.RemoveAsync(plan, removeData: true, Quiet, Token);

        Assert.Equal(DataDir + "/org.gnome.Calculator", plan.DataPath);
        Assert.Equal("flathub", plan.Technical["remote"]);
        Assert.Empty(kept.Trashed);
        Assert.Equal(DataDir + "/org.gnome.Calculator", Assert.Single(trashed.Trashed).OriginalPath);
        Assert.All(runner.Calls, call => Assert.Equal(
            ["uninstall", "--user", "--noninteractive", "app/org.gnome.Calculator/x86_64/stable"], call));
        Assert.DoesNotContain(runner.Calls, call => call.Contains("--delete-data"));
    }

    [Fact]
    public async Task Data_that_is_not_the_apps_own_folder_is_left_alone()
    {
        var provider = Provider(Answering());
        var plan = await provider.PlanRemovalAsync(Installed(), Token) with { DataPath = "/home/ada/Documents" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RemoveAsync(plan, removeData: true, Quiet, Token));

        Assert.Empty(_trash.Moved);
    }

    [Fact]
    public async Task A_removed_app_can_be_planned_again_from_its_record()
    {
        var provider = Provider(Answering());
        var removal = await provider.PlanRemovalAsync(Installed("system"), Token);
        var record = ChangeRecord.Begin(ChangeKind.RemoveApp, "flatpak", removal.Target, removal.Name) with { Undo = removal.Technical };

        var again = await provider.PlanReinstallAsync(record, Token);

        Assert.Null(again.Blocked);
        Assert.Equal("system:org.gnome.Calculator/x86_64/stable", again.Target);
        Assert.Equal("system", again.Technical["installation"]);
    }

    [Theory]
    [InlineData("Installing runtime/org.gnome.Platform.Locale/x86_64/50", "org.gnome.Platform.Locale")]
    [InlineData("Installing app/com.github.tchx84.Flatseal/x86_64/stable", "com.github.tchx84.Flatseal")]
    [InlineData("Uninstalling app/com.github.tchx84.Flatseal/x86_64/stable", "com.github.tchx84.Flatseal")]
    [InlineData("bwrap: Creating new namespace failed: Operation not permitted", null)]
    [InlineData("Installing something else entirely", null)]
    [InlineData("", null)]
    public void The_part_being_worked_on_is_read_from_what_flatpak_prints(string line, string? part)
    {
        Assert.Equal(part, FlatpakOutput.ParseStep(line));
    }

    [Fact]
    public async Task Progress_names_each_part_as_flatpak_reaches_it()
    {
        var runner = new FakeRunner(arguments => arguments[0] == "install"
            ? new(0, "Installing runtime/org.gnome.Platform/x86_64/50\nInstalling app/org.gnome.Calculator/x86_64/stable\n", "")
            : Answering().RunAsync("/usr/bin/flatpak", arguments, TimeSpan.Zero, default).Result);
        var provider = Provider(runner);
        var plan = await provider.PlanInstallAsync(Offer(), new("user", "", "", false), "Calculator", Token);
        var seen = new List<OperationProgress>();

        await provider.InstallAsync(plan, new Collect<OperationProgress>(seen), Token);

        Assert.Equal([null, "part 1: org.gnome.Platform", "part 2: org.gnome.Calculator"], seen.Select(p => p.Detail));
    }
}
