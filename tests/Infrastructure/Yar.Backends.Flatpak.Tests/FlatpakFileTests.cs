using Yar.Core;

namespace Yar.Backends.Flatpak.Tests;

public sealed class FlatpakFileTests : IDisposable
{
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
    private static IFileIntakeHandler Handler(FakeRunner runner) => new FlatpakProvider(runner, "/home/ada/.local/share/flatpak", "/usr/bin/flatpak");
#pragma warning restore CA1859

    [Fact]
    public async Task A_pointer_names_its_app_and_server_and_is_installed_for_the_user_only()
    {
        var installed = false;
        var runner = new FakeRunner(arguments =>
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
        var report = await Handler(new FakeRunner(_ => new(0, "", ""))).InspectAsync(await BasicsAsync(content), Token);

        Assert.NotNull(report.Blocked);
    }

    [Fact]
    public async Task A_bundle_is_installed_as_a_bundle_and_says_what_cannot_be_known_beforehand()
    {
        var installed = false;
        var runner = new FakeRunner(arguments =>
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
