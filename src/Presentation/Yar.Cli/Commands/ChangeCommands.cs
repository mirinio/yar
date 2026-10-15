using Yar.Core;
using Yar.Hosting;

namespace Yar.Cli;

/// <summary>Commands that install, remove, update and undo, as the window's buttons do.</summary>
internal static class ChangeCommands
{
    // yar-apps install <app id> [--kind flatpak|packagekit] [--from <source id>] [--system] [--yes]: the Store's Install button.
    // yar-apps remove <app id or name> [--data] [--yes]: the Remove button.
    public static async Task<int> InstallOrRemoveAsync(YarHost host, string[] args)
    {
        var command = CliOutput.Command(args);
        var words = new List<string>();
        string? from = null, kind = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--from")
                from = args.ElementAtOrDefault(++i);
            else if (args[i] == "--kind")
                kind = args.ElementAtOrDefault(++i);
            else if (!args[i].StartsWith('-'))
                words.Add(args[i]);
        }
        // The first word is the command itself.
        var wanted = words.Count > 1 ? words[1] : "";
        var now = await host.Catalog.LoadAppsAsync(CancellationToken.None);
        ChangePlan plan;

        if (command == "install")
        {
            var catalog = StoreCatalog.Merge(await host.Store.ReadAsync(CancellationToken.None));
            var normal = StoreCatalog.Normalize(wanted);
            var app = catalog.FirstOrDefault(a => a.KnownIds.Any(id => StoreCatalog.Normalize(id).Equals(normal, StringComparison.OrdinalIgnoreCase)));
            // As the Store suggests: a Flatpak from the app's own maker first, then the distribution's package.
            var offer = app?.Offers
                .Where(o => host.Ability<IAppInstaller>(o.ProviderId) is not null)
                .Where(o => (from is null || o.SourceId == from) && (kind is null || o.ProviderId == kind))
                .OrderBy(o => o is { ProviderId: "flatpak", MakerVerified: true } ? 0 : o.ProviderId == "packagekit" ? 1 : 2)
                .FirstOrDefault();
            if (app is null || offer is null || host.Ability<IAppInstaller>(offer.ProviderId) is not { } installer)
            {
                Console.Error.WriteLine("No app with that ID is on offer from a source I can install from. See: yar-apps store <words> --json");
                return 1;
            }
            var targets = installer.TargetsFor(offer, now.Sources);
            var target = args.Contains("--system") ? targets.FirstOrDefault(t => t.Id == "system") : targets.Count > 0 ? targets[0] : null;
            if (target is null)
            {
                Console.Error.WriteLine("That source can't install to the place you asked for.");
                return 1;
            }
            Console.WriteLine($"Install {app.Name} from {offer.SourceId}: {target.Label}");
            plan = await installer.PlanInstallAsync(offer, target, app.Name, CancellationToken.None);
        }
        else
        {
            var matches = now.Apps.Where(a => a.Id == wanted || a.ComponentId == wanted ||
                a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || host.Ability<IAppRemover>(matches[0].ProviderId) is not { } remover)
            {
                Console.Error.WriteLine(matches.Count > 1
                    ? "More than one installed app matches; use its ID from: yar-apps apps --json"
                    : "No installed app matches that I can remove.");
                return 1;
            }
            Console.WriteLine($"Remove {matches[0].Name} ({matches[0].ProviderId} {matches[0].Id})");
            plan = await remover.PlanRemovalAsync(matches[0], CancellationToken.None);
        }

        CliOutput.PrintPlan(plan);
        if (plan.Blocked is not null)
            return 1;
        if (CliOutput.StopUnlessConfirmed(args) is { } stop)
            return stop;

        ChangeRecord done;
        using (CliOutput.FollowProgress(host))
        {
            done = command == "install"
                ? await host.Make.InstallAsync(plan)
                : await host.Make.RemoveAsync(plan, removeData: args.Contains("--data"));
        }
        if (CliOutput.Failed(done))
            return 1;
        Console.WriteLine($"done ({done.Id}) {done.Target}{(done.Trashed.Count > 0 ? $", {done.Trashed.Count} moved to the Trash" : "")}");
        return 0;
    }

    // yar-apps update <app, package or update ID> [--yes], or update --all [--yes]: installs waiting updates.
    public static async Task<int> UpdateAsync(YarHost host, string[] args)
    {
        var wanted = CliOutput.Words(args).FirstOrDefault();
        var everything = await host.Catalog.LoadAsync(CancellationToken.None);
        var chosen = everything.Updates
            .Where(u => host.Ability<IAppUpdater>(u.ProviderId) is not null)
            .Where(u => args.Contains("--all") || (wanted is not null &&
                (u.Id == wanted || u.AppId == wanted || u.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                 u.Id.StartsWith(wanted + ".", StringComparison.Ordinal) || u.Id.EndsWith(":" + wanted, StringComparison.Ordinal))))
            .ToList();
        if (chosen.Count == 0)
        {
            Console.Error.WriteLine("No waiting update matches. See: yar-apps updates");
            return 1;
        }

        var plans = new List<ChangePlan>();
        foreach (var batch in chosen.GroupBy(u => u.ProviderId))
        {
            var plan = await host.Ability<IAppUpdater>(batch.Key)!.PlanUpdateAsync(
                batch.ToList(), batch.Count() == 1 ? batch.First().Name : $"{batch.Count()} updates ({batch.Key})", CancellationToken.None);
            Console.WriteLine($"{plan.Name}:");
            CliOutput.PrintPlan(plan);
            plans.Add(plan);
        }
        if (plans.All(p => p.Blocked is not null))
            return 1;
        if (CliOutput.StopUnlessConfirmed(args) is { } stop)
            return stop;

        var failed = false;
        foreach (var plan in plans.Where(p => p.Blocked is null))
        {
            var updated = await host.Make.UpdateAsync(plan);
            failed |= CliOutput.Failed(updated);
            Console.WriteLine($"{updated.Outcome} ({updated.Id}) {updated.Name}");
        }
        return failed ? 1 : 0;
    }

    // yar-apps history: everything Yar changed, oldest first.
    public static Task<int> HistoryAsync(YarHost host, string[] args)
    {
        var history = host.Journal.Read();
        foreach (var entry in history)
        {
            var undoable = host.Make.WhyNoUndo(entry, history) is null ? "undo" : "-";
            Console.WriteLine(
                $"{entry.Id}  {entry.At.ToLocalTime():yyyy-MM-dd HH:mm}  {entry.Outcome,-9} {entry.Kind,-17} {undoable,-4} {entry.Name}" +
                (entry.UndoOf is null ? "" : $"  (undo of {entry.UndoOf})") +
                (entry.Problem is null ? "" : $"  [{entry.Problem}]"));
        }
        Console.WriteLine($"{history.Count} changes");
        return Task.FromResult(0);
    }

    // yar-apps undo <id> [--yes]: reverse one change from the history.
    public static async Task<int> UndoAsync(YarHost host, string[] args)
    {
        var history = host.Journal.Read();
        var wantedId = CliOutput.Words(args).FirstOrDefault();
        var target = history.FirstOrDefault(entry => entry.Id == wantedId);
        if (target is null)
        {
            Console.Error.WriteLine("No such change. See: yar-apps history");
            return 1;
        }
        if (host.Make.WhyNoUndo(target, history) is { } whyNot)
        {
            Console.Error.WriteLine(whyNot);
            return 1;
        }
        var now = await host.Catalog.LoadAppsAsync(CancellationToken.None);
        var step = await host.Make.PlanUndoAsync(target, now, CancellationToken.None);
        Console.WriteLine(step.Summary);
        if (step.Plan is not null)
            CliOutput.PrintPlan(step.Plan);
        if (step.Plan?.Blocked is not null)
            return 1;
        if (CliOutput.StopUnlessConfirmed(args) is { } stop)
            return stop;
        return CliOutput.Failed(await host.Make.UndoAsync(step, now)) ? 1 : 0;
    }
}
