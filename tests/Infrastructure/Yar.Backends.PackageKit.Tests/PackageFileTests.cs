using Yar.Core;

namespace Yar.Backends.PackageKit.Tests;

public sealed class PackageFileTests : IDisposable
{
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
    private static IFileIntakeHandler Handler(FakeClient client, FakeProcessRunner runner) =>
        new PackageKitProvider(client, [], [], recheckWait: TimeSpan.Zero, runner: runner);
#pragma warning restore CA1859

    [Fact]
    public async Task A_package_of_this_systems_kind_is_read_and_planned_with_a_plain_warning()
    {
        var client = new FakeClient();
        client.Simulated.Add(PkPackage.Parse("coolapp;2.1-3;x86_64;local", "", 12)!);
        client.Simulated.Add(PkPackage.Parse("libcool;1.0;x86_64;fedora", "Cool library", 12)!);
        var runner = new FakeProcessRunner("coolapp\n2.1-3\nx86_64\n4096000\nA cool app\n");
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
        Assert.Equal([$"install-files {_file}"], client.Changes);
    }

    [Theory]
    [InlineData("dnf5", "deb", "Debian and Ubuntu")]
    [InlineData("aptcc", "rpm", "RPM package")]
    public async Task A_package_for_the_other_kind_of_system_is_explained(string backend, string format, string said)
    {
        var report = await Handler(new FakeClient { Backend = new(backend, "x") }, new FakeProcessRunner("x\n1\nx86_64\n1\ny\n")).InspectAsync(await BasicsAsync(format), Token);

        Assert.Contains(said, report.Blocked);
        Assert.Contains("can't be installed here", report.Blocked);
    }

    [Fact]
    public async Task What_the_package_manager_cannot_install_or_read_is_refused()
    {
        var unfit = new FakeClient { SimulateFails = "nothing provides libold.so.1" };

        var cannot = await Handler(unfit, new FakeProcessRunner("coolapp\n2\nx86_64\n1\ns\n")).InspectAsync(await BasicsAsync("rpm"), Token);
        var unreadable = await Handler(new FakeClient(), new FakeProcessRunner("")).InspectAsync(await BasicsAsync("rpm"), Token);
        var oddName = await Handler(new FakeClient(), new FakeProcessRunner("cool app; rm\n2\nx86_64\n1\ns\n")).InspectAsync(await BasicsAsync("rpm"), Token);

        Assert.Contains("libold.so.1", cannot.Blocked);
        Assert.NotNull(unreadable.Blocked);
        Assert.NotNull(oddName.Blocked);
    }

    [Fact]
    public async Task A_file_swapped_after_the_preview_is_not_installed()
    {
        var client = new FakeClient();
        client.Simulated.Add(PkPackage.Parse("coolapp;2.1-3;x86_64;local", "", 12)!);
        var handler = Handler(client, new FakeProcessRunner("coolapp\n2.1-3\nx86_64\n1\ns\n"));
        var report = await handler.InspectAsync(await BasicsAsync("rpm"), Token);
        var plan = await handler.PlanAsync(report, new IntakeChoices("coolapp", "/unused") { HashVerified = true }, Token);

        File.WriteAllText(_file, "something else entirely");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.AddAsync(report, new IntakeChoices("coolapp", "/unused"), plan, new Progress<OperationProgress>(), Token));
        Assert.Empty(client.Changes);
    }
}
