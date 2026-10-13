using System.Text.Json;
using System.Text.Json.Serialization;
using Yar.Hosting;

// Developer tool: prints what the backends see, so they can be checked without the GUI.

var json = args.Contains("--json");
var timings = args.Contains("--timings");
var command = args.FirstOrDefault(a => !a.StartsWith('-'));
if (command is not ("sources" or "apps" or "status" or "unclaimed" or "updates" or "store" or "source-add" or "source-remove" or "permissions" or "attention" or "storage" or "source-switch" or "history" or "undo" or "install" or "remove" or "inspect" or "add" or "verify"))
{
    Console.Error.WriteLine("Usage: yar-apps <sources|apps|status|unclaimed|updates|store <words>> [--json] [--timings]");
    return 2;
}

// A failed command ends with a message and a failure code, not a crash report.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.Error.WriteLine(e.ExceptionObject is Exception error ? error.Message : "Something went wrong.");
    Environment.Exit(1);
};

using var host = new YarHost();

// True when a change did not go through; says why.
static bool Failed(Yar.Core.ChangeRecord done)
{
    if (done.Outcome == Yar.Core.ChangeOutcome.Done)
        return false;
    Console.Error.WriteLine(done.Problem ?? done.Outcome.ToString());
    return true;
}

// yar-apps history: everything Yar changed, oldest first. yar-apps undo <id> [--yes]: reverse one of them.
if (command is "history" or "undo")
{
    var history = host.Journal.Read();
    if (command == "history")
    {
        foreach (var entry in history)
        {
            var undoable = host.Make.WhyNoUndo(entry, history) is null ? "undo" : "-";
            Console.WriteLine(
                $"{entry.Id}  {entry.At.ToLocalTime():yyyy-MM-dd HH:mm}  {entry.Outcome,-9} {entry.Kind,-17} {undoable,-4} {entry.Name}" +
                (entry.UndoOf is null ? "" : $"  (undo of {entry.UndoOf})") +
                (entry.Problem is null ? "" : $"  [{entry.Problem}]"));
        }
        Console.WriteLine($"{history.Count} changes");
        return 0;
    }

    var wantedId = args.Where(a => !a.StartsWith('-')).ElementAtOrDefault(1);
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
        PrintPlan(step.Plan);
    if (step.Plan?.Blocked is not null)
        return 1;
    if (!args.Contains("--yes"))
    {
        Console.WriteLine("Nothing was changed. Add --yes to do it.");
        return 0;
    }
    return Failed(await host.Make.UndoAsync(step, now)) ? 1 : 0;
}

// yar-apps install <app id> [--kind flatpak|packagekit] [--from <source id>] [--system] [--yes]: what the Store's Install button does.
// yar-apps remove <app id or name> [--data] [--yes]: what the Remove button does.
// Both print the plan and change nothing without --yes.
if (command is "install" or "remove")
{
    var options = new HashSet<string>(["--from", "--kind"], StringComparer.Ordinal);
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
    Yar.Core.ChangePlan plan;

    if (command == "install")
    {
        var catalog = Yar.Core.StoreCatalog.Merge(await host.Store.ReadAsync(CancellationToken.None));
        var normal = Yar.Core.StoreCatalog.Normalize(wanted);
        var app = catalog.FirstOrDefault(a => a.KnownIds.Any(id => Yar.Core.StoreCatalog.Normalize(id).Equals(normal, StringComparison.OrdinalIgnoreCase)));
        // As the Store suggests: a Flatpak from the app's own maker first, then the distribution's package.
        var offer = app?.Offers
            .Where(o => host.Ability<Yar.Core.IAppInstaller>(o.ProviderId) is not null)
            .Where(o => (from is null || o.SourceId == from) && (kind is null || o.ProviderId == kind))
            .OrderBy(o => o is { ProviderId: "flatpak", MakerVerified: true } ? 0 : o.ProviderId == "packagekit" ? 1 : 2)
            .FirstOrDefault();
        if (app is null || offer is null || host.Ability<Yar.Core.IAppInstaller>(offer.ProviderId) is not { } installer)
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
        if (matches.Count != 1 || host.Ability<Yar.Core.IAppRemover>(matches[0].ProviderId) is not { } remover)
        {
            Console.Error.WriteLine(matches.Count > 1
                ? "More than one installed app matches; use its ID from: yar-apps apps --json"
                : "No installed app matches that I can remove.");
            return 1;
        }
        Console.WriteLine($"Remove {matches[0].Name} ({matches[0].ProviderId} {matches[0].Id})");
        plan = await remover.PlanRemovalAsync(matches[0], CancellationToken.None);
    }

    PrintPlan(plan);
    if (plan.Blocked is not null)
        return 1;
    if (!args.Contains("--yes"))
    {
        Console.WriteLine("Nothing was changed. Add --yes to do it.");
        return 0;
    }
    var shown = "";
    void Show()
    {
        if (host.Operations.Current is not { } running)
            return;
        var line = $"{running.Progress.Phase} {(running.Progress.Fraction is { } f ? $"{f:P0}" : "")} {running.Progress.Detail}".Trim();
        lock (options)
        {
            if (line != shown)
                Console.WriteLine("  " + (shown = line));
        }
    }
    host.Operations.Changed += Show;
    var done = command == "install"
        ? await host.Make.InstallAsync(plan)
        : await host.Make.RemoveAsync(plan, removeData: args.Contains("--data"));
    host.Operations.Changed -= Show;
    if (Failed(done))
        return 1;
    Console.WriteLine($"done ({done.Id}) {done.Target}{(done.Trashed.Count > 0 ? $", {done.Trashed.Count} moved to the Trash" : "")}");
    return 0;
}

// yar-apps inspect <file>: what the file is and whether it can run here. Changes nothing.
// yar-apps add <file> [--name N] [--icon picture] [--to folder] [--program path] [--hash H] [--no-menu] [--move] [--yes]
if (command is "inspect" or "add")
{
    var valued = new[] { "--name", "--icon", "--to", "--program", "--hash" };
    var given = new Dictionary<string, string>(StringComparer.Ordinal);
    var plain = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (valued.Contains(args[i]) && i + 1 < args.Length)
            given[args[i]] = args[++i];
        else if (!args[i].StartsWith('-'))
            plain.Add(args[i]);
    }
    if (plain.Count < 2)
    {
        Console.Error.WriteLine("Which file?");
        return 2;
    }

    var report = await host.Intake.InspectAsync(plain[1], originUrl: null, CancellationToken.None);
    Console.WriteLine($"{report.FileName}: {report.KindLabel}");
    Console.WriteLine($"  name: {report.SuggestedName}{(report.Summary is null ? "" : $" - {report.Summary}")}");
    Console.WriteLine($"  icon: {report.IconPath ?? (report.IconComesWithIt ? "comes with it" : "none found")}");
    if (report.Sha256.Length > 0)
        Console.WriteLine($"  sha256: {report.Sha256}");
    foreach (var program in report.Programs)
        Console.WriteLine($"  program: {program.Label}");
    foreach (var check in report.Checks)
        Console.WriteLine($"  {(check.State == Yar.Core.CheckState.Good ? "ok " : check.State == Yar.Core.CheckState.Warning ? " ! " : "NO ")} {check.Text}");

    var verified = false;
    if (given.TryGetValue("--hash", out var pasted) && report.Blocked is null)
    {
        var (published, problem) = Yar.Core.HashCheck.Parse(pasted, report.FileName);
        if (published is null)
        {
            Console.Error.WriteLine(problem ?? "No hash given.");
            host.Intake.Discard(report);
            return 1;
        }
        verified = await Yar.Core.HashCheck.MatchesAsync(report.SourcePath, published, CancellationToken.None);
        Console.WriteLine(verified
            ? $"  ok  Matches the {published.KindName} hash you gave{(published.Weak ? " (an old kind of hash that proves little)" : "")}."
            : $"  NO  Does not match the {published.KindName} hash you gave: this is not the file the publisher describes.");
        if (!verified)
        {
            host.Intake.Discard(report);
            return 1;
        }
    }

    if (command == "inspect" || report.Blocked is not null)
    {
        host.Intake.Discard(report);
        return report.Blocked is null ? 0 : 1;
    }

    var choices = new Yar.Core.IntakeChoices(given.GetValueOrDefault("--name") ?? report.SuggestedName, Path.GetFullPath(given.GetValueOrDefault("--to") ?? host.DefaultAppsFolder))
    {
        IconPath = given.TryGetValue("--icon", out var picture) ? Path.GetFullPath(picture) : report.IconPath,
        Program = given.GetValueOrDefault("--program"),
        AppMenu = !args.Contains("--no-menu"),
        DeleteOriginal = args.Contains("--move"),
        HashVerified = verified,
    };
    var addPlan = await host.Intake.PlanAsync(report, choices, CancellationToken.None);
    PrintPlan(addPlan);
    if (addPlan.Blocked is not null || !args.Contains("--yes"))
    {
        host.Intake.Discard(report);
        if (addPlan.Blocked is null)
            Console.WriteLine("Nothing was changed. Add --yes to do it.");
        return addPlan.Blocked is null ? 0 : 1;
    }
    var added = await host.Make.AddAppAsync(host.Intake, report, choices, addPlan);
    if (Failed(added))
    {
        host.Intake.Discard(report);
        return 1;
    }
    Console.WriteLine($"done ({added.Id}) {added.Target}");
    return 0;
}

// yar-apps verify <app id or name>: is a single-file app still the file that was added?
if (command == "verify")
{
    var wantedApp = args.Where(a => !a.StartsWith('-')).ElementAtOrDefault(1) ?? "";
    var all = await host.Catalog.LoadAppsAsync(CancellationToken.None);
    var found = all.Apps.Where(a => a.Id == wantedApp || a.Name.Equals(wantedApp, StringComparison.OrdinalIgnoreCase)).ToList();
    if (found.Count != 1 || host.Ability<Yar.Core.IIntegrityChecker>(found[0].ProviderId) is not { } checker)
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

// What a change would do, as the window's preview shows it.
static void PrintPlan(Yar.Core.ChangePlan plan)
{
    static string Size(long? bytes) =>
        bytes is { } b ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b / 1_000_000.0:0.0} MB") : "?";

    foreach (var item in plan.Items)
        Console.WriteLine($"  {item.Name}  {Size(item.Bytes)}{(item.Detail is null ? "" : "  " + item.Detail)}");
    if (plan.DownloadBytes is not null || plan.DiskBytes is not null)
        Console.WriteLine($"  download {Size(plan.DownloadBytes)}, on disk {Size(plan.DiskBytes)}");
    if (plan.DataPath is not null)
        Console.WriteLine($"  data: {plan.DataPath} ({Size(plan.DataBytes)})");
    foreach (var warning in plan.Warnings)
        Console.WriteLine($"  ! {warning}");
    foreach (var note in plan.Notes)
        Console.WriteLine($"  - {note}");
    if (plan.NeedsPassword)
        Console.WriteLine("  - your system will ask for your password");
    if (plan.Blocked is not null)
        Console.WriteLine($"  REFUSED: {plan.Blocked}");
}

// Developer checks for the two things that change Flatpak sources. Both act only on the current user's own sources.
if (command is "source-add" or "source-remove")
{
    var manager = host.SourceManagerFor("flatpak") ?? throw new InvalidOperationException("Flatpak is not available.");
    var values = args.Where(a => !a.StartsWith('-')).Skip(1).ToList();
    if (command == "source-add")
    {
        var (request, problem) = Yar.Core.SourceAddress.Check(values.ElementAtOrDefault(0), values.ElementAtOrDefault(1));
        if (request is null)
        {
            Console.Error.WriteLine(problem);
            return 1;
        }
        if (Failed(await host.Make.AddSourceAsync("flatpak", request, request.Name)))
            return 1;
        Console.WriteLine($"added {request.Name}");
    }
    else
    {
        var all = await host.Catalog.LoadAppsAsync(CancellationToken.None);
        var source = all.Sources.FirstOrDefault(s => s.ProviderId == "flatpak" && s.Id == values.ElementAtOrDefault(0));
        if (source is null || !manager.CanRemove(source))
        {
            Console.Error.WriteLine("No such source, or not one I may remove.");
            return 1;
        }
        if (Failed(await host.Make.RemoveSourceAsync(source)))
            return 1;
        Console.WriteLine($"removed {source.Id}");
    }
    return 0;
}

// yar-apps permissions <words>: what an installed app is allowed to reach, as the app page will say it.
if (command == "permissions")
{
    var wanted = string.Join(' ', args.Where(a => !a.StartsWith('-')).Skip(1));
    var installed = await host.Catalog.LoadAppsAsync(CancellationToken.None);
    var app = installed.Apps.FirstOrDefault(a => a.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    if (app is null)
    {
        Console.Error.WriteLine("No installed app with that name.");
        return 1;
    }
    var raw = host.Ability<Yar.Core.IPermissionProvider>(app.ProviderId) is { } permissions
        ? await permissions.GetPermissionsAsync(app, CancellationToken.None)
        : null;
    if (raw is null)
    {
        Console.WriteLine($"{app.Name} ({app.ProviderId}) is not kept apart: it can do whatever you can.");
        return 0;
    }
    var report = Yar.Core.FlatpakPermissions.Describe(raw);
    Console.WriteLine($"{app.Name}: {report.Verdict}");
    foreach (var note in report.Notes)
        Console.WriteLine($"  {note.Weight,-6} {note.Text}{(note.Detail is null ? "" : $" ({note.Detail})")}");
    return 0;
}

// yar-apps storage: disks, and where the space goes, as the Storage tab will show it.
if (command == "storage")
{
    var loaded = await host.Catalog.LoadAsync(CancellationToken.None);
    var overview = await host.Storage.AnalyzeAsync(loaded, CancellationToken.None);
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            overview, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }
    static string Gb(long? bytes) => bytes is { } b ? $"{b / 1e9,8:0.0} GB" : "          -";
    foreach (var disk in overview.Disks)
    {
        Console.WriteLine($"{disk.Name}  {disk.Model}  {Gb(disk.SizeBytes)}  not in any partition: {Gb(disk.UnallocatedBytes)}");
        foreach (var part in disk.Partitions)
        {
            Console.WriteLine(
                $"  {part.Name,-10} {part.FileSystem ?? "-",-6} size {Gb(part.SizeBytes)} used {Gb(part.UsedBytes)} free {Gb(part.FreeBytes)}  {string.Join(",", part.MountPoints)}");
        }
    }
    Console.WriteLine();
    foreach (var item in overview.Items.Take(25))
    {
        Console.WriteLine(
            $"{item.Kind,-12} {(item.AtLeast ? ">=" : "  ")}{item.Bytes / 1e6,10:0.0} MB  (data {item.DataBytes / 1e6,8:0.0} MB)  {item.Title}");
    }
    Console.WriteLine($"{overview.Items.Count} items, {overview.AccountedBytes / 1e9:0.0} GB accounted for");
    return 0;
}

// yar-apps source-switch <kind> <source id> on|off: switch a source, as the switch in the window does.
// On a real system this asks for a password; it exists so the test container can exercise it.
if (command == "source-switch")
{
    var values = args.Where(a => !a.StartsWith('-')).Skip(1).ToList();
    if (values.Count != 3 || values[2] is not ("on" or "off"))
    {
        Console.Error.WriteLine("Usage: yar-apps source-switch <flatpak|packagekit> <source id> <on|off>");
        return 2;
    }
    var manager = host.SourceManagerFor(values[0]);
    var all = await host.Catalog.LoadAppsAsync(CancellationToken.None);
    var source = all.Sources.FirstOrDefault(s => s.ProviderId == values[0] && s.Id == values[1]);
    if (manager is null || source is null || !manager.CanToggle(source))
    {
        Console.Error.WriteLine("No such source, or not one I may switch.");
        return 1;
    }
    if (Failed(await host.Make.SwitchSourceAsync(source, values[2] == "on")))
        return 1;
    Console.WriteLine($"{source.Id} is now {values[2]}");
    return 0;
}

// yar-apps store <words>: what the Store would show for a search, read from the local catalogs.
if (command == "store")
{
    var storeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
    var storeApps = Yar.Core.StoreCatalog.Merge(await host.Store.ReadAsync(CancellationToken.None));
    var words = string.Join(' ', args.Where(a => !a.StartsWith('-')).Skip(1));
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            Yar.Core.StoreCatalog.Search(storeApps, words, c => c.ToString(), 5),
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }
    foreach (var app in Yar.Core.StoreCatalog.Search(storeApps, words, c => c.ToString(), 15))
    {
        Console.WriteLine(
            $"{app.Name,-28} {string.Join(" + ", app.Offers.Select(o => o.SourceId + (o.MakerVerified ? " (verified)" : ""))),-44} {app.Summary}");
    }
    Console.WriteLine(
        $"{storeApps.Count} apps, {storeApps.Count(a => a.Offers.Count > 1)} offered by more than one source, " +
        $"read in {System.Diagnostics.Stopwatch.GetElapsedTime(storeStarted).TotalMilliseconds:0} ms");
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var started = System.Diagnostics.Stopwatch.GetTimestamp();
var snapshot = await host.Catalog.LoadAsync(cancellation.Token);
var total = System.Diagnostics.Stopwatch.GetElapsedTime(started);

if (json)
{
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    object payload = command switch
    {
        "sources" => snapshot.Sources,
        "apps" => snapshot.Apps,
        "unclaimed" => snapshot.UnclaimedLaunchers.Select(e => e.Path),
        "updates" => snapshot.Updates,
        "attention" => Yar.Core.AttentionFinder.Find(snapshot),
        _ => snapshot.Providers,
    };
    Console.WriteLine(JsonSerializer.Serialize(payload, options));
    return 0;
}

switch (command)
{
    case "sources":
        foreach (var source in snapshot.Sources)
        {
            Console.WriteLine(
                $"{source.ProviderId,-11} {source.Id,-28} {(source.Enabled ? "on " : "off")} {source.Trust,-10} {source.Name}");
        }
        break;
    case "apps":
        foreach (var app in snapshot.Apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            Console.WriteLine($"{app.ProviderId,-11} {app.SourceId ?? "-",-28} {app.Version ?? "-",-16} {app.Name}");
        Console.WriteLine($"{snapshot.Apps.Count} apps, {snapshot.Duplicates.Count} installed more than once");
        break;
    case "updates":
        foreach (var update in snapshot.Updates)
        {
            Console.WriteLine(
                $"{update.ProviderId,-11} {update.Severity,-12} {(update.AppId is null ? "system" : "app"),-7} {update.Name}  {update.CurrentVersion ?? "?"} -> {update.NewVersion ?? "?"}");
        }
        Console.WriteLine(
            $"{snapshot.Updates.Count} updates: " +
            string.Join(", ", snapshot.Updates.GroupBy(u => u.Severity).Select(g => $"{g.Count()} {g.Key}")));
        foreach (var report in snapshot.Providers.Where(r => r.UpdateCheckError is not null))
            Console.WriteLine($"{report.ProviderId}: update check failed: {report.UpdateCheckError}");
        break;
    case "attention":
        foreach (var item in Yar.Core.AttentionFinder.Find(snapshot))
            Console.WriteLine($"{item.Kind,-15} {item.Title,-44} {(item.SizeBytes is { } bytes ? bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-"),12}  {item.Path}");
        break;
    case "unclaimed":
        foreach (var entry in snapshot.UnclaimedLaunchers)
            Console.WriteLine($"{entry.Name,-40} {entry.Path}");
        Console.WriteLine($"{snapshot.UnclaimedLaunchers.Count} launchers no provider claimed");
        break;
    default:
        foreach (var report in snapshot.Providers)
            Console.WriteLine($"{report.ProviderId,-11} {report.Status.Availability} {report.Status.Reason}");
        break;
}

if (timings)
{
    Console.WriteLine();
    foreach (var timing in snapshot.Timings)
        Console.WriteLine($"{timing.Milliseconds,9:0.0} ms  {timing.Stage}");
    Console.WriteLine($"{total.TotalMilliseconds,9:0.0} ms  total (stages overlap, so they do not add up to this)");
}

return 0;
