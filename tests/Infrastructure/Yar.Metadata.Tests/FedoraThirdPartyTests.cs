using Yar.Core;

namespace Yar.Metadata.Tests;

public class FedoraThirdPartyTests
{
    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, ProcessResult> respond) : IProcessRunner
    {
        public List<(string Program, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((absolutePath, arguments));
            return Task.FromResult(respond(absolutePath, arguments));
        }
    }

    private const string Tool = "/usr/bin/fedora-third-party";
    private const string Pkexec = "/usr/bin/pkexec";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, ThirdPartyChoice.Allowed)]
    [InlineData(1, ThirdPartyChoice.Declined)]
    [InlineData(2, ThirdPartyChoice.NotChosen)]
    public async Task Reads_the_choice_from_the_exit_code_and_lists_what_it_covers(int exitCode, ThirdPartyChoice expected)
    {
        var runner = new FakeRunner((_, arguments) => arguments[0] == "query"
            ? new(exitCode, "", "")
            : new(0, "name,type\ngoogle-chrome,dnf\ncopr:copr.fedorainfracloud.org:phracek:PyCharm,dnf\nflathub,flatpak\n", ""));

        var status = await new FedoraThirdParty(runner).QueryAsync(Token);

        Assert.Equal(expected, status.Choice);
        Assert.Equal(
            [("google-chrome", "dnf"), ("copr:copr.fedorainfracloud.org:phracek:PyCharm", "dnf"), ("flathub", "flatpak")],
            status.Sources);
        // Asking never needs the password prompt.
        Assert.All(runner.Calls, call => Assert.Equal(Tool, call.Program));
    }

    [Fact]
    public async Task An_unexpected_answer_is_an_error_not_a_guess()
    {
        var runner = new FakeRunner((_, _) => new(7, "", "boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new FedoraThirdParty(runner).QueryAsync(Token));
    }

    [Theory]
    [InlineData(true, "enable")]
    [InlineData(false, "disable")]
    public async Task Changes_the_choice_only_through_the_two_fixed_commands(bool allowed, string command)
    {
        var runner = new FakeRunner((_, _) => new(0, "", ""));

        await new FedoraThirdParty(runner).SetAllowedAsync(allowed, Token);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(Pkexec, call.Program);
        Assert.Equal([Tool, command], call.Arguments);
    }

    [Fact]
    public async Task A_closed_prompt_a_refusal_and_a_failure_are_told_apart()
    {
        Task Change(int exitCode) =>
            new FedoraThirdParty(new FakeRunner((_, _) => new(exitCode, "", "it broke"))).SetAllowedAsync(true, Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => Change(126));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Change(127));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Change(1));
        Assert.Contains("it broke", failure.Message);
    }

    [Fact]
    public void Is_only_offered_where_the_tool_exists()
    {
        Assert.False(new FedoraThirdParty(new FakeRunner((_, _) => new(0, "", "")), "/nonexistent/tool").IsAvailable);
    }
}
