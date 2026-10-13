namespace Yar.Metadata.Tests;

public class ProcessRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ProcessRunner _runner = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("plain")]
    [InlineData("two words")]
    [InlineData("semi;colon && echo injected")]
    [InlineData("$(echo injected) `echo injected`")]
    [InlineData("quote\"and'apostrophe")]
    [InlineData("line\nbreak")]
    [InlineData("-n")]
    public async Task Arguments_reach_the_program_unchanged(string argument)
    {
        var result = await _runner.RunAsync("/usr/bin/printf", ["%s", argument], Timeout, Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(argument, result.StdOut);
    }

    [Fact]
    public async Task Reports_exit_code_and_stderr()
    {
        var result = await _runner.RunAsync("/usr/bin/ls", ["/nonexistent-yar-test-path"], Timeout, Token);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("nonexistent-yar-test-path", result.StdErr);
    }

    [Fact]
    public async Task Relative_program_paths_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _runner.RunAsync("printf", ["x"], Timeout, Token));
    }

    [Fact]
    public async Task A_missing_program_is_an_ordinary_failure()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _runner.RunAsync("/nonexistent/yar-test-tool", [], Timeout, Token));

        Assert.Contains("/nonexistent/yar-test-tool", error.Message);
    }

    [Fact]
    public async Task A_program_that_runs_too_long_is_stopped()
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => _runner.RunAsync("/usr/bin/sleep", ["30"], TimeSpan.FromMilliseconds(200), Token));
    }

    [Fact]
    public async Task Lines_are_passed_on_while_the_program_runs()
    {
        var lines = new List<string>();

        var result = await _runner.RunAsync(
            "/usr/bin/printf", ["one\\ntwo 10%%\\rtwo 90%%\\n\\nlast"], Timeout, line => { lock (lines) lines.Add(line); }, Token);

        Assert.Equal(["one", "two 10%", "two 90%", "last"], lines);
        Assert.Equal("one\ntwo 10%\rtwo 90%\n\nlast", result.StdOut);
    }
}
