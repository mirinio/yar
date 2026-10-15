using Yar.Core;

namespace Yar.Backends.Flatpak.Tests;

public class FlatpakUpdateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static UpdateInfo Waiting(string id, string? appId, string? current = "1", string? next = "2") =>
        new("flatpak", id, id.Split(':')[1], current, next, UpdateSeverity.Normal, null, appId);

    [Fact]
    public async Task Updates_are_run_per_installation_for_the_named_apps_only()
    {
        var runner = new FakeRunner();
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
        var runner = new FakeRunner();
        var provider = new FlatpakProvider(runner, "/home/ada/.local/share/flatpak", "/usr/bin/flatpak");
        var forged = new ChangePlan("flatpak", "x", "x", []) { Technical = new Dictionary<string, string> { [key] = value } };

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.UpdateAsync(forged, new Progress<OperationProgress>(), Token));
        Assert.Empty(runner.Calls);
    }
}
