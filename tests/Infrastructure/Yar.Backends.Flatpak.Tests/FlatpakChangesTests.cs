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

    private sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult> respond) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            if (arguments is ["remotes"])
                return Task.FromResult(new ProcessResult(0, "", ""));
            lock (Calls)
                Calls.Add(arguments);
            return Task.FromResult(respond(arguments));
        }
    }

    private sealed class FakeTrash : ITrash
    {
        public List<string> Moved { get; } = [];
        public string? WhyNot(string path) => null;
        public bool Holds(TrashedItem item) => true;
        public void Restore(TrashedItem item) { }

        public TrashedItem Move(string path)
        {
            Moved.Add(path);
            return new(path, "/t/files/x", "/t/info/x.trashinfo");
        }
    }

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
        var seen = new List<string?>();

        await provider.InstallAsync(plan, new Collect(seen), Token);

        Assert.Equal([null, "part 1: org.gnome.Platform", "part 2: org.gnome.Calculator"], seen);
    }

    private sealed class Collect(List<string?> into) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => into.Add(value.Detail);
    }
}

public sealed class FlatpakFileTests : IDisposable
{
    private sealed class Runner(Func<IReadOnlyList<string>, ProcessResult> respond) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            if (arguments is not ["remotes"])
                Calls.Add(arguments);
            return Task.FromResult(respond(arguments));
        }
    }

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"yar-ref-{Guid.NewGuid():N}.flatpakref");

    public void Dispose() => File.Delete(_file);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<IntakeReport> BasicsAsync(string content, string format = "flatpakref")
    {
        File.WriteAllText(_file, content);
        return new(_file, Path.GetFileName(_file), BundleKind.FlatpakFile, "A pointer", new FileInfo(_file).Length,
            await HashCheck.ComputeAsync(_file, HashKind.Sha256, Token), "Thing")
        {
            Technical = new Dictionary<string, string> { ["format"] = format },
        };
    }

    // The methods under test are only reachable through the interface.
#pragma warning disable CA1859
    private static IFileIntakeHandler Handler(Runner runner) => new FlatpakProvider(runner, "/home/ada/.local/share/flatpak", "/usr/bin/flatpak");
#pragma warning restore CA1859

    [Fact]
    public async Task A_pointer_names_its_app_and_server_and_is_installed_for_the_user_only()
    {
        var installed = false;
        var runner = new Runner(arguments =>
        {
            if (arguments[0] == "install")
                installed = true;
            return new(0, arguments[0] == "list" && installed
                ? "org.cool.App\tCool App\t1.0\tstable\tcool-origin\tuser\t5.0 MB\torg.cool.App/x86_64/stable\n" : "", "");
        });
        var handler = Handler(runner);

        var report = await handler.InspectAsync(
            await BasicsAsync("[Flatpak Ref]\nTitle=Cool App\nName=org.cool.App\nBranch=stable\nUrl=https://cool.example.org/repo/\nIsRuntime=false\n"), Token);
        var plan = await handler.PlanAsync(report, new IntakeChoices("Cool App", "/unused"), Token);
        var added = await handler.AddAsync(report, new IntakeChoices("Cool App", "/unused"), plan, new Progress<OperationProgress>(), Token);

        Assert.Null(report.Blocked);
        Assert.Equal("Cool App", report.SuggestedName);
        Assert.Contains(report.Checks, c => c.State == CheckState.Warning && c.Text.Contains("adds cool.example.org to your sources"));
        Assert.Null(plan.Blocked);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(["install", "--user", "--noninteractive", "--from", _file]));
        Assert.Equal(new AddedApp("flatpak", "user:org.cool.App/x86_64/stable", "Cool App"), added);
    }

    [Theory]
    [InlineData("[Flatpak Ref]\nName=org.cool.App\nUrl=http://cool.example.org/repo/\n")]
    [InlineData("[Flatpak Ref]\nName=--system\nUrl=https://cool.example.org/repo/\n")]
    [InlineData("[Flatpak Ref]\nUrl=https://cool.example.org/repo/\n")]
    [InlineData("[Flatpak Ref]\nName=org.cool.Platform\nUrl=https://cool.example.org/repo/\nIsRuntime=true\n")]
    public async Task A_pointer_without_an_app_or_a_protected_server_is_refused(string content)
    {
        var report = await Handler(new Runner(_ => new(0, "", ""))).InspectAsync(await BasicsAsync(content), Token);

        Assert.NotNull(report.Blocked);
    }

    [Fact]
    public async Task A_bundle_is_installed_as_a_bundle_and_says_what_cannot_be_known_beforehand()
    {
        var installed = false;
        var runner = new Runner(arguments =>
        {
            installed |= arguments[0] == "install";
            return new(0, arguments[0] == "list" && installed ? "org.b.App\tB\t1\tstable\tb-origin\tuser\t1 MB\torg.b.App/x86_64/stable\n" : "", "");
        });
        var handler = Handler(runner);

        var report = await handler.InspectAsync(await BasicsAsync("binary", "flatpak"), Token);
        var plan = await handler.PlanAsync(report, new IntakeChoices("B", "/unused"), Token);
        await handler.AddAsync(report, new IntakeChoices("B", "/unused"), plan, new Progress<OperationProgress>(), Token);

        Assert.Contains(report.Checks, c => c.Text.Contains("can't look inside"));
        Assert.Contains(plan.Warnings, w => w.Contains("not compared with a hash"));
        Assert.Contains(runner.Calls, call => call.SequenceEqual(["install", "--user", "--noninteractive", "--bundle", _file]));
    }
}

public class FlatpakUpdateTests
{
    private sealed class Runner : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            if (arguments is not ["remotes"])
                Calls.Add(arguments);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static UpdateInfo Waiting(string id, string? appId, string? current = "1", string? next = "2") =>
        new("flatpak", id, id.Split(':')[1], current, next, UpdateSeverity.Normal, null, appId);

    [Fact]
    public async Task Updates_are_run_per_installation_for_the_named_apps_only()
    {
        var runner = new Runner();
        var provider = new FlatpakProvider(runner, "/home/ada/.local/share/flatpak", "/usr/bin/flatpak");

        var plan = await provider.PlanUpdateAsync(
        [
            Waiting("system:org.kde.krita", "system:org.kde.krita/x86_64/stable", "5.2", "5.2"),
            Waiting("system:org.gnome.Platform", null, null, null),
            Waiting("user:org.gimp.GIMP", "user:org.gimp.GIMP/x86_64/stable"),
            Waiting("user:--evil", null),
        ], "3 updates", Token);
        await provider.UpdateAsync(plan, new Progress<OperationProgress>(), Token);

        Assert.Null(plan.Blocked);
        Assert.Equal(3, plan.Items.Count);
        Assert.Contains(plan.Items, item => item.Detail == "5.2, a newer build");
        Assert.Contains(plan.Items, item => item.Detail == "A building block apps share");
        Assert.Equal(
        [
            ["update", "--system", "--noninteractive", "org.kde.krita", "org.gnome.Platform"],
            ["update", "--user", "--noninteractive", "org.gimp.GIMP"],
        ], runner.Calls);
    }

    [Theory]
    [InlineData("user", "--system")]
    [InlineData("user", "org.x.App/x86_64/stable")]
    [InlineData("elsewhere", "org.x.App")]
    public async Task A_plan_that_names_anything_but_plain_IDs_is_not_carried_out(string key, string value)
    {
        var runner = new Runner();
        var provider = new FlatpakProvider(runner, "/home/ada/.local/share/flatpak", "/usr/bin/flatpak");
        var forged = new ChangePlan("flatpak", "x", "x", []) { Technical = new Dictionary<string, string> { [key] = value } };

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UpdateAsync(forged, new Progress<OperationProgress>(), Token));
        Assert.Empty(runner.Calls);
    }
}
