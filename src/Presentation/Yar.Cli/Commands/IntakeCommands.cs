using Yar.Core;
using Yar.Hosting;

namespace Yar.Cli;

/// <summary>Commands for apps brought as a file or a link, as the Add-an-app page does.</summary>
internal static class IntakeCommands
{
    private static readonly string[] Valued = ["--name", "--icon", "--to", "--program", "--hash"];

    // yar-apps inspect <file>: what the file is and whether it can run here. Changes nothing.
    // yar-apps add <file or https link> [--name N] [--icon picture] [--to folder] [--program path] [--hash H]
    //              [--no-menu] [--pin] [--move] [--unsigned-ok] [--yes]
    public static async Task<int> InspectOrAddAsync(YarHost host, string[] args)
    {
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        var plain = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (Valued.Contains(args[i]) && i + 1 < args.Length)
                given[args[i]] = args[++i];
            else if (!args[i].StartsWith('-'))
                plain.Add(args[i]);
        }
        if (plain.Count < 2)
        {
            Console.Error.WriteLine("Which file?");
            return 2;
        }

        // A link is downloaded first and then looked at like any file.
        string? fetched = null, cameFrom = null;
        if (plain[1].StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var (link, linkProblem) = DownloadLink.Check(plain[1]);
            if (link is null)
            {
                Console.Error.WriteLine(linkProblem);
                return 1;
            }
            var info = await host.Downloader.ProbeAsync(link, CancellationToken.None);
            Console.WriteLine($"downloading {info.FileName} ({(info.Bytes is { } size ? $"{size / 1_000_000.0:0.0} MB" : "size unknown")}) from {info.Address.Host}");
            fetched = await host.Downloader.DownloadAsync(info, new Progress<long>(), CancellationToken.None);
            cameFrom = info.Address.AbsoluteUri;
        }
        void Forget(IntakeReport seen)
        {
            host.Intake.Discard(seen);
            if (fetched is not null)
                host.Downloader.Discard(fetched);
        }

        var report = await host.Intake.InspectAsync(fetched ?? plain[1], cameFrom, CancellationToken.None);
        Print(report);

        var verified = false;
        if (given.TryGetValue("--hash", out var pasted) && report.Blocked is null)
        {
            var (published, problem) = HashCheck.Parse(pasted, report.FileName);
            if (published is null)
            {
                Console.Error.WriteLine(problem ?? "No hash given.");
                Forget(report);
                return 1;
            }
            verified = await HashCheck.MatchesAsync(report.SourcePath, published, CancellationToken.None);
            Console.WriteLine(verified
                ? $"  ok  Matches the {published.KindName} hash you gave{(published.Weak ? " (an old kind of hash that proves little)" : "")}."
                : $"  NO  Does not match the {published.KindName} hash you gave: this is not the file the publisher describes.");
            if (!verified)
            {
                Forget(report);
                return 1;
            }
        }

        if (CliOutput.Command(args) == "inspect" || report.Blocked is not null)
        {
            Forget(report);
            return report.Blocked is null ? 0 : 1;
        }

        var choices = new IntakeChoices(
            given.GetValueOrDefault("--name") ?? report.SuggestedName, Path.GetFullPath(given.GetValueOrDefault("--to") ?? host.DefaultAppsFolder))
        {
            IconPath = given.TryGetValue("--icon", out var picture) ? Path.GetFullPath(picture) : report.IconPath,
            Program = given.GetValueOrDefault("--program"),
            AppMenu = !args.Contains("--no-menu"),
            Pin = args.Contains("--pin"),
            DeleteOriginal = fetched is not null || args.Contains("--move"),
            HashVerified = verified,
        };
        var plan = await host.Intake.PlanAsync(report, choices, CancellationToken.None);
        CliOutput.PrintPlan(plan);
        if (plan.Blocked is null && report.Kind == BundleKind.Package && args.Contains("--yes") && !args.Contains("--unsigned-ok"))
        {
            Console.Error.WriteLine("A package from a file is installed with administrator rights and unchecked. Add --unsigned-ok to say you trust it.");
            Forget(report);
            return 1;
        }
        if (plan.Blocked is not null)
        {
            Forget(report);
            return 1;
        }
        if (CliOutput.StopUnlessConfirmed(args) is { } stop)
        {
            Forget(report);
            return stop;
        }

        var added = await host.Make.AddAppAsync(host.Intake, report, choices, plan);
        if (CliOutput.Failed(added))
        {
            Forget(report);
            return 1;
        }
        if (fetched is not null)
            host.Downloader.Discard(fetched);
        Console.WriteLine($"done ({added.Id}) {added.Target}");
        if (added.Undo.TryGetValue(AppChanges.PinProblem, out var pinProblem))
            Console.WriteLine($"not pinned: {pinProblem}");
        return 0;
    }

    // yar-apps verify <app id or name>: is a single-file app still the file that was added?
    public static async Task<int> VerifyAsync(YarHost host, string[] args)
    {
        var wanted = CliOutput.Words(args).FirstOrDefault() ?? "";
        var all = await host.Catalog.LoadAppsAsync(CancellationToken.None);
        var found = all.Apps.Where(a => a.Id == wanted || a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count != 1 || host.Ability<IIntegrityChecker>(found[0].ProviderId) is not { } checker)
        {
            Console.Error.WriteLine("No single app matches that I keep a hash for.");
            return 1;
        }
        var result = await checker.CheckAsync(found[0], CancellationToken.None);
        Console.WriteLine($"when added: {result.Sha256WhenAdded ?? "not recorded"}{(result.WasVerified ? " (matched the publisher's hash)" : "")}");
        Console.WriteLine($"now:        {result.Sha256Now ?? "not a single file"}");
        Console.WriteLine(result.Unchanged switch { true => "unchanged", false => "CHANGED since it was added", _ => "nothing to compare" });
        return result.Unchanged == false ? 1 : 0;
    }

    private static void Print(IntakeReport report)
    {
        Console.WriteLine($"{report.FileName}: {report.KindLabel}");
        Console.WriteLine($"  name: {report.SuggestedName}{(report.Summary is null ? "" : $" - {report.Summary}")}");
        Console.WriteLine($"  icon: {report.IconPath ?? (report.IconComesWithIt ? "comes with it" : "none found")}");
        if (report.Sha256.Length > 0)
            Console.WriteLine($"  sha256: {report.Sha256}");
        foreach (var program in report.Programs)
            Console.WriteLine($"  program: {program.Label}");
        foreach (var check in report.Checks)
            Console.WriteLine($"  {(check.State == CheckState.Good ? "ok " : check.State == CheckState.Warning ? " ! " : "NO ")} {check.Text}");
    }
}
