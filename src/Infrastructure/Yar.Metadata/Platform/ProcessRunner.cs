using System.Diagnostics;
using System.Text;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>
/// Runs external tools without a shell. Arguments are passed as a list, never as a command
/// string, so file names with spaces, quotes or semicolons cannot change what is run.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxOutputChars = 16 * 1024 * 1024;

    public Task<ProcessResult> RunAsync(
        string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
        RunCoreAsync(absolutePath, arguments, timeout, onLine: null, ct);

    public Task<ProcessResult> RunAsync(
        string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string> onLine,
        CancellationToken ct) =>
        RunCoreAsync(absolutePath, arguments, timeout, onLine, ct);

    private static async Task<ProcessResult> RunCoreAsync(
        string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onLine,
        CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("An absolute path is required.", nameof(absolutePath));

        var info = new ProcessStartInfo(absolutePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        // Stable, parseable output regardless of the user's language.
        info.Environment["LC_ALL"] = "C.UTF-8";
        info.Environment.Remove("LANGUAGE");

        Process? started;
        try
        {
            started = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // The tool is missing or may not be run. Callers treat that like any other failed command.
            throw new InvalidOperationException($"Could not start {absolutePath}: {e.Message}", e);
        }
        using var process = started ?? throw new InvalidOperationException($"Could not start {absolutePath}.");
        process.StandardInput.Close();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var stdout = ReadCappedAsync(process, process.StandardOutput, onLine, timeoutSource.Token);
            var stderr = ReadCappedAsync(process, process.StandardError, onLine, timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
                throw;
            throw new TimeoutException($"{absolutePath} did not finish within {timeout.TotalSeconds:0} s.");
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private static async Task<string> ReadCappedAsync(
        Process process, StreamReader reader, Action<string>? onLine, CancellationToken ct)
    {
        var builder = new StringBuilder();
        // How much of the output has been passed on as whole lines already.
        var reported = 0;
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (builder.Length + read > MaxOutputChars)
            {
                // Stop the tool rather than let it block on a pipe nobody reads.
                Kill(process);
                throw new InvalidOperationException("The command produced more output than Yar accepts.");
            }
            builder.Append(buffer, 0, read);
            if (onLine is null)
                continue;
            // Tools that redraw one line end it with a carriage return; each redraw counts as a line.
            for (var i = builder.Length - read; i < builder.Length; i++)
            {
                if (builder[i] is not ('\n' or '\r'))
                    continue;
                if (i > reported)
                    onLine(builder.ToString(reported, i - reported));
                reported = i + 1;
            }
        }
        if (onLine is not null && builder.Length > reported)
            onLine(builder.ToString(reported, builder.Length - reported));
        return builder.ToString();
    }
}
