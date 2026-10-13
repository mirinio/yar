using Yar.Core;

namespace Yar.Backends.Flatpak;

// Flatpak apps the user brought as a file: a .flatpakref, which points to an app on a server, or
// a .flatpak bundle, which is the app itself. Both are handed to flatpak and installed for the
// user only; afterwards they are ordinary Flatpak apps.
public sealed partial class FlatpakProvider
{
    private const long MaxRefBytes = 64 * 1024;

    Task<IntakeReport> IFileIntakeHandler.InspectAsync(IntakeReport basics, CancellationToken ct)
    {
        IntakeReport Refused(string reason) =>
            basics with { Blocked = reason, Checks = [.. basics.Checks, new(CheckState.Bad, reason)] };

        if (_flatpak is null)
            return Task.FromResult(Refused("Flatpak is not installed on this computer, so I can't install Flatpak files."));

        if (basics.Technical.GetValueOrDefault("format") == "flatpak")
        {
            return Task.FromResult(basics with
            {
                IconComesWithIt = true,
                Checks =
                [
                    new(CheckState.Good, "A Flatpak app is kept apart from the rest of your computer and is installed only for you."),
                    new(CheckState.Warning,
                        "I can't look inside a Flatpak file before it is installed, so I can't tell you its real name or what it may " +
                        "reach. Flatpak checks that the file is intact while installing; look at the app's page afterwards."),
                ],
            });
        }

        if (basics.FileBytes > MaxRefBytes)
            return Task.FromResult(Refused("This is far too large to be a pointer to a Flatpak app. It is probably something else."));
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(basics.SourcePath).Take(200))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
                keys.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim());
        }

        var id = keys.GetValueOrDefault("Name") ?? "";
        if (!IsRef(id) || !id.Contains('.') || id.Contains('/') ||
            !Uri.TryCreate(keys.GetValueOrDefault("Url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return Task.FromResult(Refused("This pointer doesn't name an app and a protected (https) server to get it from, so I won't use it."));
        }
        if (keys.GetValueOrDefault("IsRuntime") == "true")
            return Task.FromResult(Refused("This points to a building block for other apps, not to an app."));

        return Task.FromResult(basics with
        {
            SuggestedName = keys.GetValueOrDefault("Title") is { Length: > 0 } title ? title : id.Split('.')[^1],
            IconComesWithIt = true,
            Checks =
            [
                new(CheckState.Good, $"Points to the app {id} on {url.Host}."),
                new(CheckState.Good, "A Flatpak app is kept apart from the rest of your computer and is installed only for you."),
                new(CheckState.Warning,
                    $"Installing it also adds {url.Host} to your sources, so the app can get its updates from there. " +
                    "You find it under Sources afterwards and can remove it there."),
            ],
            Technical = new Dictionary<string, string>(basics.Technical) { ["application"] = id },
        });
    }

    Task<ChangePlan> IFileIntakeHandler.PlanAsync(IntakeReport report, IntakeChoices choices, CancellationToken ct)
    {
        if (report.Blocked is { } blocked)
            return Task.FromResult(ChangePlan.Refused(ProviderId, report.FileName, report.SuggestedName, blocked));

        var isRef = report.Technical.GetValueOrDefault("format") == "flatpakref";
        return Task.FromResult(new ChangePlan(
            ProviderId, report.Technical.GetValueOrDefault("application") ?? report.FileName, report.SuggestedName,
            [new(report.SuggestedName, isRef ? "Downloaded from the server the file points to" : report.FileName, isRef ? null : report.FileBytes)])
        {
            Warnings = choices.HashVerified || isRef
                ? []
                : ["It was not compared with a hash from its publisher, so I can't tell whether it is the genuine file."],
            Notes =
            [
                $"It is installed only for you, into {_userInstallationDir.TrimEnd('/')}. No password is needed.",
                "The building blocks it runs on are downloaded too if they are not here yet; I can't say ahead of time how big they are.",
                "Afterwards it is a Flatpak app like any other: its page shows what it may reach, and you remove it from there.",
            ],
            Origin = report.OriginUrl ?? report.FileName,
            Technical = new Dictionary<string, string> { ["file"] = report.SourcePath, ["sha256"] = report.Sha256 },
        });
    }

    async Task<AddedApp> IFileIntakeHandler.AddAsync(
        IntakeReport report, IntakeChoices choices, ChangePlan plan, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        var format = report.Technical.GetValueOrDefault("format");
        if (_flatpak is null || plan.Blocked is not null || plan.ProviderId != ProviderId ||
            plan.Technical.GetValueOrDefault("file") != report.SourcePath || format is not ("flatpakref" or "flatpak"))
        {
            throw new InvalidOperationException(plan.Blocked ?? "This is not a plan I made for this Flatpak file.");
        }
        // The file could have been swapped since it was looked at; install only what was shown.
        if (await HashCheck.ComputeAsync(report.SourcePath, HashKind.Sha256, ct).ConfigureAwait(false) != report.Sha256)
            throw new InvalidOperationException("The file changed since I looked at it, so I stopped. Bring it in again.");

        var before = await UserAppsAsync(ct).ConfigureAwait(false);
        progress.Report(new(OperationPhase.Waiting));
        await RunChangeAsync(
            ["install", "--user", "--noninteractive", format == "flatpakref" ? "--from" : "--bundle", report.SourcePath],
            InstallTimeout, OperationPhase.Installing, progress, ct).ConfigureAwait(false);

        // What is there now that was not before is what the file installed.
        var after = await UserAppsAsync(ct).ConfigureAwait(false);
        var wanted = report.Technical.GetValueOrDefault("application");
        var installed = after.FirstOrDefault(app => before.All(old => old.Ref != app.Ref))
            ?? after.FirstOrDefault(app => app.Application == wanted)
            ?? throw new InvalidOperationException("Flatpak finished without an error, but I can't find the app it installed.");
        return new(ProviderId, $"user:{installed.Ref}", installed.Name ?? report.SuggestedName);
    }

    private async Task<IReadOnlyList<FlatpakApp>> UserAppsAsync(CancellationToken ct) =>
        FlatpakOutput.ParseApps(
            await RunAsync(["list", "--app", "--user", Columns(FlatpakOutput.AppColumns)], ct).ConfigureAwait(false));
}
