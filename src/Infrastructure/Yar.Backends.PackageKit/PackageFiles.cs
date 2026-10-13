using System.Globalization;
using Yar.Core;

namespace Yar.Backends.PackageKit;

// Package files the user brought themselves (.rpm, .deb). They are handed to the package manager
// like any package, with one difference that the preview spells out: nobody the system knows has
// vouched for them, and what they contain runs with administrator rights while being installed.
public sealed partial class PackageKitProvider
{
    private static readonly string[] RpmTool = ["/usr/bin/rpm", "/bin/rpm"];
    private static readonly string[] DebTool = ["/usr/bin/dpkg-deb", "/bin/dpkg-deb"];

    private const string UnsignedWarning =
        "Your distribution has not checked this package, and usually nobody your system knows has signed it. " +
        "While it is being installed, scripts inside it run with administrator rights and can change anything on " +
        "this computer. Only go on if you trust where it came from.";

    async Task<IntakeReport> IFileIntakeHandler.InspectAsync(IntakeReport basics, CancellationToken ct)
    {
        var format = basics.Technical.GetValueOrDefault("format");
        IntakeReport Refused(string reason) =>
            basics with { Blocked = reason, Checks = [.. basics.Checks, new(CheckState.Bad, reason)] };

        var backend = await _client.GetBackendAsync(ct).ConfigureAwait(false);
        if (backend is null)
            return Refused("This computer's package manager can't be reached right now, so I can't install packages.");
        var uses = backend.Name.Contains("apt", StringComparison.OrdinalIgnoreCase) ? "deb" : "rpm";
        if (format != uses)
        {
            return Refused(format == "deb"
                ? "This is a package for Debian and Ubuntu (.deb). This computer uses RPM packages, so it can't be installed here. " +
                  "Look for an .rpm, an AppImage or a Flatpak of the same app."
                : "This is an RPM package, as Fedora and openSUSE use. This computer uses Debian packages (.deb), so it can't be " +
                  "installed here. Look for a .deb, an AppImage or a Flatpak of the same app.");
        }

        var facts = await ReadPackageFactsAsync(basics.SourcePath, uses, ct).ConfigureAwait(false);
        if (facts is null || !IsPackageName(facts.Name))
            return Refused("I couldn't read this package. It may be damaged or cut short by an interrupted download.");

        var checks = new List<IntakeCheck>
        {
            new(CheckState.Good, $"It is the kind of package this computer uses, and contains {facts.Name} {facts.Version}."),
        };
        IReadOnlyList<PkPackage> all;
        try
        {
            all = await _client.SimulateInstallFilesAsync([basics.SourcePath], ct).ConfigureAwait(false);
            var extra = all.Count(p => p.Name != facts.Name);
            checks.Add(new(CheckState.Good, extra == 0
                ? "Everything it depends on is already here."
                : $"What it depends on is available: {extra} more {(extra == 1 ? "package" : "packages")} would come from your sources."));
        }
        catch (Exception e) when (e is PackageKitException or TimeoutException)
        {
            return Refused(
                $"Your package manager says it can't be installed here: {Short(e.Message)} " +
                "Often that means it was built for another version of your system or for another kind of processor.");
        }
        checks.Add(new(CheckState.Warning, UnsignedWarning));

        var arch = all.FirstOrDefault(p => p.Name == facts.Name)?.Arch ?? facts.Arch;
        return basics with
        {
            SuggestedName = facts.Name,
            Summary = facts.Summary,
            Version = facts.Version,
            InstalledBytes = facts.InstalledBytes,
            IconComesWithIt = true,
            Checks = checks,
            Technical = new Dictionary<string, string>(basics.Technical) { ["package"] = facts.Name, ["arch"] = arch },
        };
    }

    async Task<ChangePlan> IFileIntakeHandler.PlanAsync(IntakeReport report, IntakeChoices choices, CancellationToken ct)
    {
        if (report.Blocked is { } blocked || report.Technical.GetValueOrDefault("package") is not { } name)
            return ChangePlan.Refused(ProviderId, report.FileName, report.SuggestedName, report.Blocked ?? "I couldn't read this package.");

        var all = await _client.SimulateInstallFilesAsync([report.SourcePath], ct).ConfigureAwait(false);
        var leaving = all.Where(p => p.Info is InfoRemoving or InfoObsoleting).ToList();
        var items = new List<PlanItem> { new(name, $"{report.FileName}, version {report.Version}", report.InstalledBytes) };
        items.AddRange(all.Where(p => p.Name != name).OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new PlanItem(
            p.Name, leaving.Contains(p) ? "Will be removed" : p.Summary.Length > 0 ? p.Summary : "Needed by it")));

        var warnings = new List<string> { UnsignedWarning };
        if (!choices.HashVerified)
            warnings.Add("It was not compared with a hash from its publisher, so I can't tell whether it is the genuine file.");
        if (leaving.Count > 0)
            warnings.Add($"Installing this also removes: {string.Join(", ", leaving.Select(p => p.Name))}.");

        return new(ProviderId, $"{name}.{report.Technical.GetValueOrDefault("arch")}", name, items)
        {
            DiskBytes = report.InstalledBytes,
            NeedsPassword = true,
            Warnings = warnings,
            Notes =
            [
                "Your distribution decides where its files go, and the package brings its own app-menu entry.",
                "It won't get updates by itself unless the package also sets up a source for them.",
                .. choices.HashVerified ? new[] { "It matches the hash you pasted from its publisher." } : [],
            ],
            Blocked = Guard(leaving, name),
            Origin = report.OriginUrl ?? report.FileName,
            Technical = new Dictionary<string, string> { ["package"] = name, ["file"] = report.SourcePath, ["sha256"] = report.Sha256 },
        };
    }

    async Task<AddedApp> IFileIntakeHandler.AddAsync(
        IntakeReport report, IntakeChoices choices, ChangePlan plan, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        if (plan.Blocked is { } blocked)
            throw new InvalidOperationException(blocked);
        if (plan.ProviderId != ProviderId || plan.Technical.GetValueOrDefault("file") != report.SourcePath ||
            !IsPackageName(plan.Technical.GetValueOrDefault("package")))
        {
            throw new InvalidOperationException("This is not a plan I made for this package file.");
        }
        // The file could have been swapped since it was looked at; install only what was shown.
        var now = await HashCheck.ComputeAsync(report.SourcePath, HashKind.Sha256, ct).ConfigureAwait(false);
        if (now != report.Sha256)
            throw new InvalidOperationException("The file changed since I looked at it, so I stopped. Bring it in again.");

        progress.Report(new(OperationPhase.Waiting));
        await _client.InstallFilesAsync([report.SourcePath], Relay(progress, OperationPhase.Installing), ct).ConfigureAwait(false);
        return new(ProviderId, plan.Target, plan.Name);
    }

    private sealed record PackageFacts(string Name, string Version, string Arch, long? InstalledBytes, string? Summary);

    // Asks the system's own package tool what a file contains. It reads the file; it installs nothing.
    private async Task<PackageFacts?> ReadPackageFactsAsync(string path, string format, CancellationToken ct)
    {
        if (_runner is null)
            return null;
        // If the tool is missing, starting it fails, and that is reported like any unreadable package.
        var candidates = format == "rpm" ? RpmTool : DebTool;
        var tool = candidates.FirstOrDefault(File.Exists) ?? candidates[0];

        string[] arguments = format == "rpm"
            ? ["-qp", "--nosignature", "--nodigest", "--qf", "%{NAME}\\n%{VERSION}-%{RELEASE}\\n%{ARCH}\\n%{SIZE}\\n%{SUMMARY}\\n", "--", path]
            : ["--showformat", "${Package}\\n${Version}\\n${Architecture}\\n${Installed-Size}\\n${Description}\\n", "-W", "--", path];
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(tool, arguments, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            return null;
        }
        var lines = result.StdOut.Split('\n');
        if (result.ExitCode != 0 || lines.Length < 5)
            return null;

        long? bytes = long.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > 0
            // dpkg counts in units of 1024 bytes, rpm in bytes.
            ? format == "rpm" ? size : size * 1024
            : null;
        return new(lines[0].Trim(), lines[1].Trim(), lines[2].Trim(), bytes, lines[4].Trim() is { Length: > 0 } summary ? summary : null);
    }

    private static string Short(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? message;
        return line.Length > 240 ? line[..240] + "…" : line;
    }
}
