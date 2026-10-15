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

        await provider.InstallAsync(plan, new Collect<OperationProgress>(seen), Token);

        Assert.Equal(
            [new(OperationPhase.Waiting), new(OperationPhase.Downloading, 0.4), new(OperationPhase.Installing, null, null, CanCancel: false)],
            seen);
    }
}
