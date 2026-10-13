namespace Yar.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}
