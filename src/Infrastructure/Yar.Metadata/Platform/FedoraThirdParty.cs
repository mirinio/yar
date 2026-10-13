using Yar.Core;

namespace Yar.Metadata;

/// <summary>
/// Fedora's "third-party repositories" opt-in, through Fedora's own <c>fedora-third-party</c> tool.
/// Changing the choice has to happen as root; Fedora provides a polkit rule for exactly this
/// command, so it is started through <c>pkexec</c>, which shows the system's password prompt.
/// Only the two fixed commands "enable" and "disable" are ever run that way.
/// </summary>
public sealed class FedoraThirdParty(
    IProcessRunner runner, string toolPath = "/usr/bin/fedora-third-party", string pkexecPath = "/usr/bin/pkexec")
    : IThirdPartyPolicy
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    // Long enough to type a password, and for the tool to add the Flatpak source.
    private static readonly TimeSpan ChangeTimeout = TimeSpan.FromMinutes(5);

    public bool IsAvailable => File.Exists(toolPath) && File.Exists(pkexecPath);

    public async Task<ThirdPartyStatus> QueryAsync(CancellationToken ct)
    {
        // The tool answers through its exit code: 0 allowed, 1 declined, 2 not chosen yet.
        var query = await runner.RunAsync(toolPath, ["query", "--quiet"], QueryTimeout, ct).ConfigureAwait(false);
        var choice = query.ExitCode switch
        {
            0 => ThirdPartyChoice.Allowed,
            1 => ThirdPartyChoice.Declined,
            2 => ThirdPartyChoice.NotChosen,
            _ => throw new InvalidOperationException($"fedora-third-party query failed: {query.StdErr.Trim()}"),
        };

        var list = await runner.RunAsync(toolPath, ["list", "--csv", "--columns=name,type"], QueryTimeout, ct)
            .ConfigureAwait(false);
        return new(choice, list.ExitCode == 0 ? ParseList(list.StdOut) : []);
    }

    public async Task SetAllowedAsync(bool allowed, CancellationToken ct)
    {
        var result = await runner.RunAsync(pkexecPath, [toolPath, allowed ? "enable" : "disable"], ChangeTimeout, ct)
            .ConfigureAwait(false);
        switch (result.ExitCode)
        {
            case 0:
                return;
            // pkexec's own exit codes: the prompt was dismissed, or the user is not allowed.
            case 126:
                throw new OperationCanceledException("The password prompt was closed.");
            case 127:
                throw new UnauthorizedAccessException("This account is not allowed to change that.");
            default:
                throw new InvalidOperationException(
                    $"fedora-third-party did not finish: {(result.StdErr.Trim() is { Length: > 0 } error ? error : result.StdOut.Trim())}");
        }
    }

    public static IReadOnlyList<(string Name, string Kind)> ParseList(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(','))
            .Where(cells => cells.Length == 2 && cells[0].Length > 0 && !cells[0].Equals("name", StringComparison.OrdinalIgnoreCase))
            .Select(cells => (cells[0].Trim('"'), cells[1].Trim('"')))
            .ToList();
}
