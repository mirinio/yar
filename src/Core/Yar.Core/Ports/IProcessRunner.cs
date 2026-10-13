namespace Yar.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// As above, and passes on each line the program prints while it is still running, from
    /// either of its outputs. For long work whose progress should be shown.
    /// </summary>
    async Task<ProcessResult> RunAsync(
        string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string> onLine,
        CancellationToken ct)
    {
        // A runner that cannot follow along still gives every line, only all at the end.
        var result = await RunAsync(absolutePath, arguments, timeout, ct).ConfigureAwait(false);
        foreach (var line in (result.StdOut + "\n" + result.StdErr).Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            onLine(line);
        return result;
    }
}
