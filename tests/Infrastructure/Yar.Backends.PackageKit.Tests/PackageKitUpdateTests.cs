using Yar.Core;

namespace Yar.Backends.PackageKit.Tests;

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
