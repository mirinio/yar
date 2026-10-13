using Yar.Core;

namespace Yar.Metadata.Tests;

public class DesktopPinnerTests
{
    private sealed class Runner(Func<IReadOnlyList<string>, ProcessResult> respond) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(arguments);
            return Task.FromResult(respond(arguments));
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task On_GNOME_the_launcher_is_added_to_the_list_and_the_rest_is_written_back_as_it_was()
    {
        var runner = new Runner(arguments => new(0, arguments[0] == "get" ? "['firefox.desktop', 'org.gnome.Nautilus.desktop']\n" : "", ""));
        var pinner = new DesktopPinner(runner, ["ubuntu", "GNOME"]);

        await pinner.PinAsync("yar-cool-tool.desktop", Token);

        Assert.Null(pinner.WhyNot);
        Assert.Equal(
            ["set", "org.gnome.shell", "favorite-apps", "['firefox.desktop', 'org.gnome.Nautilus.desktop', 'yar-cool-tool.desktop']"],
            runner.Calls[^1]);
    }

    [Fact]
    public async Task On_GNOME_unpinning_takes_only_that_one_out_and_does_nothing_when_it_is_not_there()
    {
        var runner = new Runner(arguments => new(0, arguments[0] == "get" ? "['a.desktop', 'yar-x.desktop']" : "", ""));
        var pinner = new DesktopPinner(runner, ["GNOME"]);

        await pinner.UnpinAsync("yar-x.desktop", Token);
        await pinner.UnpinAsync("yar-other.desktop", Token);
        await pinner.PinAsync("a.desktop", Token);

        Assert.Equal(["set", "org.gnome.shell", "favorite-apps", "['a.desktop']"], Assert.Single(runner.Calls, call => call[0] == "set"));
    }

    [Theory]
    [InlineData("@as []", 0)]
    [InlineData("[]", 0)]
    [InlineData("['a.desktop']", 1)]
    [InlineData("['it''s.desktop']", null)]
    [InlineData("['a.desktop', broken]", null)]
    [InlineData("nonsense", null)]
    [InlineData("", null)]
    public void A_list_that_cannot_be_written_back_unchanged_is_not_touched(string printed, int? count)
    {
        Assert.Equal(count, DesktopPinner.ParseStringList(printed)?.Count);
    }

    [Fact]
    public async Task On_GNOME_an_unreadable_list_is_left_alone()
    {
        var runner = new Runner(_ => new(0, "['odd\\\\name.desktop']", ""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new DesktopPinner(runner, ["GNOME"]).PinAsync("yar-x.desktop", Token));

        Assert.DoesNotContain(runner.Calls, call => call[0] == "set");
    }

    [Fact]
    public async Task On_Plasma_the_shell_is_asked_to_add_it_and_its_answer_decides()
    {
        var answer = "('yar-pin found=1 changed=1 unreadable=0\\n',)";
        var runner = new Runner(_ => new(0, answer, ""));
        var pinner = new DesktopPinner(runner, ["KDE"]);

        await pinner.PinAsync("yar-cool-tool.desktop", Token);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(["call", "--session", "--dest", "org.kde.plasmashell"], call.Take(4));
        Assert.Contains("\"applications:yar-cool-tool.desktop\"", call[^1]);
        Assert.Contains("var add = true;", call[^1]);

        answer = "('yar-pin found=0 changed=0 unreadable=0\\n',)";
        await Assert.ThrowsAsync<InvalidOperationException>(() => pinner.PinAsync("yar-cool-tool.desktop", Token));
        answer = "('yar-pin found=1 changed=0 unreadable=1\\n',)";
        await Assert.ThrowsAsync<InvalidOperationException>(() => pinner.PinAsync("yar-cool-tool.desktop", Token));
        answer = "Error: scripting is locked";
        await Assert.ThrowsAsync<InvalidOperationException>(() => pinner.PinAsync("yar-cool-tool.desktop", Token));
        // Already pinned is fine.
        answer = "('yar-pin found=1 changed=0 unreadable=0\\n',)";
        await pinner.PinAsync("yar-cool-tool.desktop", Token);
    }

    [Theory]
    [InlineData("x\";evil();\".desktop")]
    [InlineData("../x.desktop")]
    [InlineData("a b.desktop")]
    [InlineData("x'.desktop")]
    [InlineData("plain.txt")]
    public async Task Names_that_could_change_the_request_are_refused(string id)
    {
        var runner = new Runner(_ => new(0, "[]", ""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new DesktopPinner(runner, ["KDE"]).PinAsync(id, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DesktopPinner(runner, ["GNOME"]).PinAsync(id, Token));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Other_desktops_say_why_not()
    {
        var pinner = new DesktopPinner(new Runner(_ => new(0, "", "")), ["XFCE"]);

        Assert.NotNull(pinner.WhyNot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pinner.PinAsync("yar-x.desktop", Token));
    }
}
