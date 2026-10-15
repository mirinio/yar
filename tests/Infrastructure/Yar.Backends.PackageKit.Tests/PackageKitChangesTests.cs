using Yar.Core;

namespace Yar.Backends.PackageKit.Tests;

public partial class PackageKitTests
{
    private const uint Installing = 12, Removing = 13;

    private static PkPackage Package(string id, uint info = 0, string summary = "") => PkPackage.Parse(id, summary, info)!;

    private static readonly StoreOffer XtermOffer = new("packagekit", "fedora", "xterm.desktop", "xterm", null, false, null);

    private static readonly IProgress<OperationProgress> Quiet = new Progress<OperationProgress>();

    private static AppInfo InstalledApp(string package) => new(
        "packagekit", $"{package}.x86_64", package, null, null, null, "1", "fedora", 1_000_000, null, [], null,
        new Dictionary<string, string> { ["package"] = package, ["arch"] = "x86_64", ["package-id"] = $"{package};0.9;x86_64;installed:fedora" });

    private static PackageKitProvider ChangesProvider(FakeClient client) => new(client, [], [], recheckWait: TimeSpan.Zero);

    [Fact]
    public async Task An_install_plan_lists_everything_that_comes_along_with_sizes()
    {
        var client = new FakeClient();
        client.Available.Add(Package("xterm;395-1.fc44;x86_64;fedora"));
        client.Simulated.Add(Package("xterm;395-1.fc44;x86_64;fedora", Installing));
        client.Simulated.Add(Package("libXaw;1.0.16;x86_64;fedora", Installing, "X Athena Widget Set"));
        client.OfferSizes["xterm;395-1.fc44;x86_64;fedora"] = new(900_000, 2_000_000);
        client.OfferSizes["libXaw;1.0.16;x86_64;fedora"] = new(200_000, 500_000);
        var provider = ChangesProvider(client);

        var plan = await provider.PlanInstallAsync(XtermOffer, provider.TargetsFor(XtermOffer, [])[0], "XTerm", Token);

        Assert.Null(plan.Blocked);
        Assert.Equal("xterm.x86_64", plan.Target);
        Assert.Equal(["XTerm", "libXaw"], plan.Items.Select(i => i.Name));
        Assert.Equal("X Athena Widget Set", plan.Items[1].Detail);
        Assert.Equal(1_100_000, plan.DownloadBytes);
        Assert.Equal(2_500_000, plan.DiskBytes);
        Assert.True(plan.NeedsPassword);
        Assert.Empty(plan.Warnings);

        await provider.InstallAsync(plan, Quiet, Token);

        Assert.Equal(["install xterm;395-1.fc44;x86_64;fedora"], client.Changes);
    }

    [Fact]
    public async Task What_no_source_offers_is_refused_and_a_missing_size_leaves_the_total_unknown()
    {
        var client = new FakeClient();
        var provider = ChangesProvider(client);

        var none = await provider.PlanInstallAsync(XtermOffer, provider.TargetsFor(XtermOffer, [])[0], "XTerm", Token);
        client.Available.Add(Package("xterm;395;x86_64;fedora"));
        client.Simulated.Add(Package("xterm;395;x86_64;fedora", Installing));
        var unsized = await provider.PlanInstallAsync(XtermOffer, provider.TargetsFor(XtermOffer, [])[0], "XTerm", Token);

        Assert.NotNull(none.Blocked);
        Assert.Null(unsized.Blocked);
        Assert.Null(unsized.DiskBytes);
        Assert.Empty(provider.TargetsFor(XtermOffer with { PackageName = "xterm; rm -rf" }, []));
    }

    [Fact]
    public async Task An_install_that_would_remove_something_vital_is_refused()
    {
        var client = new FakeClient();
        client.Available.Add(Package("xterm;395;x86_64;fedora"));
        client.Simulated.Add(Package("xterm;395;x86_64;fedora", Installing));
        client.Simulated.Add(Package("systemd-libs;257;x86_64;installed:fedora", Removing));
        var provider = ChangesProvider(client);

        var plan = await provider.PlanInstallAsync(XtermOffer, provider.TargetsFor(XtermOffer, [])[0], "XTerm", Token);

        Assert.Contains("systemd-libs", plan.Blocked);
    }

    [Fact]
    public async Task A_removal_plan_names_what_else_goes_and_asks_for_exactly_that()
    {
        var client = new FakeClient();
        client.Installed["gimp"] = "gimp;3.0;x86_64;installed:fedora";
        client.Simulated.Add(Package("gimp;3.0;x86_64;installed:fedora", Removing));
        client.Simulated.Add(Package("gimp-help;3.0;noarch;installed:fedora", Removing, "Help files"));
        client.Sizes["gimp-help;3.0;noarch;installed:fedora"] = 4_000_000;
        var provider = ChangesProvider(client);

        var plan = await provider.PlanRemovalAsync(InstalledApp("gimp"), Token);

        Assert.Null(plan.Blocked);
        Assert.Equal(["gimp", "gimp-help"], plan.Items.Select(i => i.Name));
        Assert.Equal(5_000_000, plan.DiskBytes);
        Assert.Contains("also removes 1 other package", Assert.Single(plan.Warnings));
        Assert.Null(plan.DataPath);

        await provider.RemoveAsync(plan, removeData: true, Quiet, Token);

        // The fresh ID, not the stale one remembered with the app.
        Assert.Equal(["remove gimp;3.0;x86_64;installed:fedora dependents=True"], client.Changes);
    }

    [Fact]
    public async Task A_removal_with_nothing_else_attached_does_not_allow_dependents()
    {
        var client = new FakeClient();
        client.Installed["xterm"] = "xterm;395;x86_64;installed:fedora";
        client.Simulated.Add(Package("xterm;395;x86_64;installed:fedora", Removing));
        var provider = ChangesProvider(client);

        var plan = await provider.PlanRemovalAsync(InstalledApp("xterm"), Token);
        await provider.RemoveAsync(plan, removeData: false, Quiet, Token);

        Assert.Empty(plan.Warnings);
        Assert.Equal(["remove xterm;395;x86_64;installed:fedora dependents=False"], client.Changes);
    }

    [Theory]
    [InlineData("plasma-workspace")]
    [InlineData("kernel-core")]
    [InlineData("glibc")]
    [InlineData("libadwaita")]
    public async Task A_removal_that_would_take_a_vital_part_along_is_refused(string vital)
    {
        var client = new FakeClient();
        client.Installed["gimp"] = "gimp;3.0;x86_64;installed:fedora";
        client.Simulated.Add(Package("gimp;3.0;x86_64;installed:fedora", Removing));
        client.Simulated.Add(Package($"{vital};1;x86_64;installed:fedora", Removing));
        var provider = ChangesProvider(client);

        var plan = await provider.PlanRemovalAsync(InstalledApp("gimp"), Token);

        Assert.Contains(vital, plan.Blocked);
    }

    [Fact]
    public async Task Vital_packages_are_not_removable_even_with_a_forged_plan()
    {
        var client = new FakeClient();
        var provider = ChangesProvider(client);
        var forged = new ChangePlan("packagekit", "systemd.x86_64", "systemd", [])
        {
            Technical = new Dictionary<string, string> { ["package"] = "systemd", ["package-id"] = "systemd;257;x86_64;installed:fedora" },
        };
        var mismatched = forged with
        {
            Technical = new Dictionary<string, string> { ["package"] = "xterm", ["package-id"] = "systemd;257;x86_64;installed:fedora" },
        };

        Assert.NotNull(provider.WhyNotRemovable(InstalledApp("systemd")));
        Assert.Null(provider.WhyNotRemovable(InstalledApp("systemd-ui-is-not-systemd") with
        {
            Technical = new Dictionary<string, string> { ["package"] = "gimp", ["arch"] = "x86_64" },
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RemoveAsync(forged, false, Quiet, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RemoveAsync(mismatched, false, Quiet, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InstallAsync(mismatched, Quiet, Token));
        Assert.Empty(client.Changes);
    }

    [Fact]
    public async Task Progress_is_passed_on_in_Yars_own_terms()
    {
        var client = new FakeClient();
        client.Available.Add(Package("xterm;395;x86_64;fedora"));
        client.Simulated.Add(Package("xterm;395;x86_64;fedora", Installing));
        var provider = ChangesProvider(client);
        var plan = await provider.PlanInstallAsync(XtermOffer, provider.TargetsFor(XtermOffer, [])[0], "XTerm", Token);
        var seen = new List<OperationProgress>();

        await provider.InstallAsync(plan, new Collect(seen), Token);

        Assert.Equal(
            [new(OperationPhase.Waiting), new(OperationPhase.Downloading, 0.4), new(OperationPhase.Installing, null, null, CanCancel: false)],
            seen);
    }

    private sealed class Collect(List<OperationProgress> into) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => into.Add(value);
    }
}

public sealed class PackageFileTests : IDisposable
{
    private sealed class Client(string backend) : IPackageKitClient
    {
        public List<PkPackage> Simulated { get; } = [];
        public string? SimulateFails { get; set; }
        public List<string> Installed { get; } = [];

        public Task<PkBackend?> GetBackendAsync(CancellationToken ct) => Task.FromResult<PkBackend?>(new(backend, "x"));

        public Task<IReadOnlyList<PkPackage>> SimulateInstallFilesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            SimulateFails is { } reason
                ? Task.FromException<IReadOnlyList<PkPackage>>(new PackageKitException(1, reason))
                : Task.FromResult<IReadOnlyList<PkPackage>>(Simulated);

        public Task InstallFilesAsync(IReadOnlyList<string> paths, IProgress<PkProgress> progress, CancellationToken ct)
        {
            Installed.AddRange(paths);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PkPackage>> SimulateUpdateAsync(IReadOnlyList<string> packageIds, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateAsync(IReadOnlyList<string> packageIds, IProgress<PkProgress> progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PkRepo>> GetRepoListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PkPackage>> ResolveInstalledAsync(IReadOnlyList<string> names, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PkPackage>> SearchInstalledFileAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task NotifyPackagesChangedAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SetRepoEnabledAsync(string repoId, bool enabled, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PkPackage>> GetUpdatesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<TimeSpan?> GetTimeSinceRefreshAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<OfferSize?> GetOfferSizeAsync(string packageName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, long>> GetSizesAsync(IReadOnlyList<string> packageIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PkPackage>> ResolveAvailableAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, OfferSize>> GetOfferSizesAsync(IReadOnlyList<string> packageIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PkPackage>> SimulateInstallAsync(IReadOnlyList<string> packageIds, CancellationToken ct) => throw new NotSupportedException();
        public Task InstallAsync(IReadOnlyList<string> packageIds, IProgress<PkProgress> progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PkPackage>> SimulateRemoveAsync(IReadOnlyList<string> packageIds, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(IReadOnlyList<string> packageIds, bool allowDependents, IProgress<PkProgress> progress, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Runner(string output) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add([absolutePath, .. arguments]);
            return Task.FromResult(new ProcessResult(output.Length > 0 ? 0 : 1, output, ""));
        }
    }

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"yar-pkg-{Guid.NewGuid():N}.rpm");

    public PackageFileTests() => File.WriteAllText(_file, "package bytes");

    public void Dispose() => File.Delete(_file);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<IntakeReport> BasicsAsync(string format) => new(
        _file, Path.GetFileName(_file), BundleKind.Package, "A package", 13,
        await HashCheck.ComputeAsync(_file, HashKind.Sha256, Token), "Thing")
    {
        Technical = new Dictionary<string, string> { ["format"] = format },
    };

    // The methods under test are only reachable through the interface.
#pragma warning disable CA1859
    private static IFileIntakeHandler Handler(Client client, Runner runner) =>
        new PackageKitProvider(client, [], [], recheckWait: TimeSpan.Zero, runner: runner);
#pragma warning restore CA1859

    [Fact]
    public async Task A_package_of_this_systems_kind_is_read_and_planned_with_a_plain_warning()
    {
        var client = new Client("dnf5");
        client.Simulated.Add(PkPackage.Parse("coolapp;2.1-3;x86_64;local", "", 12)!);
        client.Simulated.Add(PkPackage.Parse("libcool;1.0;x86_64;fedora", "Cool library", 12)!);
        var runner = new Runner("coolapp\n2.1-3\nx86_64\n4096000\nA cool app\n");
        var handler = Handler(client, runner);

        var report = await handler.InspectAsync(await BasicsAsync("rpm"), Token);
        var plan = await handler.PlanAsync(report, new IntakeChoices("coolapp", "/unused"), Token);
        var added = await handler.AddAsync(report, new IntakeChoices("coolapp", "/unused"), plan, new Progress<OperationProgress>(), Token);

        Assert.Null(report.Blocked);
        Assert.Equal("coolapp", report.SuggestedName);
        Assert.Equal("2.1-3", report.Version);
        Assert.Equal(4_096_000, report.InstalledBytes);
        Assert.True(report.IconComesWithIt);
        Assert.Contains(report.Checks, c => c.State == CheckState.Warning && c.Text.Contains("administrator rights"));
        Assert.Contains(runner.Calls[0], argument => argument == _file);
        Assert.Equal("--", runner.Calls[0][^2]);

        Assert.Equal("coolapp.x86_64", plan.Target);
        Assert.Equal(["coolapp", "libcool"], plan.Items.Select(i => i.Name));
        Assert.True(plan.NeedsPassword);
        Assert.Contains(plan.Warnings, w => w.Contains("administrator rights"));
        Assert.Contains(plan.Warnings, w => w.Contains("not compared with a hash"));
        Assert.Equal(new AddedApp("packagekit", "coolapp.x86_64", "coolapp"), added);
        Assert.Equal([_file], client.Installed);
    }

    [Theory]
    [InlineData("dnf5", "deb", "Debian and Ubuntu")]
    [InlineData("aptcc", "rpm", "RPM package")]
    public async Task A_package_for_the_other_kind_of_system_is_explained(string backend, string format, string said)
    {
        var report = await Handler(new Client(backend), new Runner("x\n1\nx86_64\n1\ny\n")).InspectAsync(await BasicsAsync(format), Token);

        Assert.Contains(said, report.Blocked);
        Assert.Contains("can't be installed here", report.Blocked);
    }

    [Fact]
    public async Task What_the_package_manager_cannot_install_or_read_is_refused()
    {
        var unfit = new Client("dnf5") { SimulateFails = "nothing provides libold.so.1" };

        var cannot = await Handler(unfit, new Runner("coolapp\n2\nx86_64\n1\ns\n")).InspectAsync(await BasicsAsync("rpm"), Token);
        var unreadable = await Handler(new Client("dnf5"), new Runner("")).InspectAsync(await BasicsAsync("rpm"), Token);
        var oddName = await Handler(new Client("dnf5"), new Runner("cool app; rm\n2\nx86_64\n1\ns\n")).InspectAsync(await BasicsAsync("rpm"), Token);

        Assert.Contains("libold.so.1", cannot.Blocked);
        Assert.NotNull(unreadable.Blocked);
        Assert.NotNull(oddName.Blocked);
    }

    [Fact]
    public async Task A_file_swapped_after_the_preview_is_not_installed()
    {
        var client = new Client("dnf5");
        client.Simulated.Add(PkPackage.Parse("coolapp;2.1-3;x86_64;local", "", 12)!);
        var handler = Handler(client, new Runner("coolapp\n2.1-3\nx86_64\n1\ns\n"));
        var report = await handler.InspectAsync(await BasicsAsync("rpm"), Token);
        var plan = await handler.PlanAsync(report, new IntakeChoices("coolapp", "/unused") { HashVerified = true }, Token);

        File.WriteAllText(_file, "something else entirely");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.AddAsync(report, new IntakeChoices("coolapp", "/unused"), plan, new Progress<OperationProgress>(), Token));
        Assert.Empty(client.Installed);
    }
}

public partial class PackageKitTests
{
    private static UpdateInfo Waiting(string package, string? appId = null) =>
        new("packagekit", $"{package}.x86_64", package, "1.0", "2.0", UpdateSeverity.Normal, "updates", appId);

    [Fact]
    public async Task An_update_plan_lists_what_comes_along_and_updates_exactly_what_was_asked()
    {
        var client = new FakeClient();
        client.Updates.Add(Package("gimp;2.0;x86_64;updates"));
        client.Updates.Add(Package("kernel-core;7.3;x86_64;updates"));
        client.Simulated.Add(Package("gimp;2.0;x86_64;updates", 11));
        client.Simulated.Add(Package("gimp-libs;2.0;x86_64;updates", 11));
        client.OfferSizes["gimp;2.0;x86_64;updates"] = new(30_000_000, null);
        client.OfferSizes["gimp-libs;2.0;x86_64;updates"] = new(5_000_000, null);
        var provider = ChangesProvider(client);

        var plan = await provider.PlanUpdateAsync([Waiting("gimp", "gimp.x86_64")], "GIMP", Token);
        await provider.UpdateAsync(plan, Quiet, Token);

        Assert.Null(plan.Blocked);
        Assert.Equal(["gimp", "gimp-libs"], plan.Items.Select(i => i.Name));
        Assert.Equal("1.0 → 2.0", plan.Items[0].Detail);
        Assert.Equal(35_000_000, plan.DownloadBytes);
        Assert.Equal(["update gimp;2.0;x86_64;updates"], client.Changes);
    }

    [Fact]
    public async Task Replacing_an_old_kernel_is_an_update_but_losing_a_vital_part_is_refused()
    {
        var client = new FakeClient();
        client.Updates.Add(Package("kernel-core;7.3;x86_64;updates"));
        client.Simulated.Add(Package("kernel-core;7.3;x86_64;updates", 12));
        client.Simulated.Add(Package("kernel-core;7.1;x86_64;installed:updates", Removing));
        var provider = ChangesProvider(client);

        var replaced = await provider.PlanUpdateAsync([Waiting("kernel-core")], "1 update", Token);
        client.Simulated.Add(Package("systemd-udev;257;x86_64;installed:fedora", Removing));
        var lost = await provider.PlanUpdateAsync([Waiting("kernel-core")], "1 update", Token);

        Assert.Null(replaced.Blocked);
        Assert.Empty(replaced.Warnings);
        Assert.Contains(replaced.Notes, note => note.Contains("Restart"));
        Assert.Contains("systemd-udev", lost.Blocked);
    }

    [Fact]
    public async Task Updates_that_are_gone_and_forged_plans_are_refused()
    {
        var client = new FakeClient();
        var provider = ChangesProvider(client);
        var forged = new ChangePlan("packagekit", "x", "x", [])
        {
            Technical = new Dictionary<string, string> { ["package-ids"] = "gimp;2.0;x86_64;updates\nnot a package id" },
        };

        Assert.NotNull((await provider.PlanUpdateAsync([Waiting("gimp")], "GIMP", Token)).Blocked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UpdateAsync(forged, Quiet, Token));
        Assert.Empty(client.Changes);
    }
}
