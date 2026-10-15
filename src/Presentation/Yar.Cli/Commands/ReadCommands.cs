using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Yar.Core;
using Yar.Hosting;

namespace Yar.Cli;

/// <summary>Commands that only look: what is installed, on offer, waiting, and where the space goes.</summary>
internal static class ReadCommands
{
    // yar-apps sources|apps|status|unclaimed|updates|attention [--json] [--timings]
    public static async Task<int> CatalogAsync(YarHost host, string[] args)
    {
        var command = CliOutput.Command(args);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var started = Stopwatch.GetTimestamp();
        var snapshot = await host.Catalog.LoadAsync(cancellation.Token);
        var total = Stopwatch.GetElapsedTime(started);

        if (args.Contains("--json"))
        {
            object payload = command switch
            {
                "sources" => snapshot.Sources,
                "apps" => snapshot.Apps,
                "unclaimed" => snapshot.UnclaimedLaunchers.Select(e => e.Path),
                "updates" => snapshot.Updates,
                "attention" => AttentionFinder.Find(snapshot),
                _ => snapshot.Providers,
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, CliOutput.Json));
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
                foreach (var item in AttentionFinder.Find(snapshot))
                    Console.WriteLine($"{item.Kind,-15} {item.Title,-44} {(item.SizeBytes is { } bytes ? bytes.ToString(CultureInfo.InvariantCulture) : "-"),12}  {item.Path}");
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

        if (args.Contains("--timings"))
        {
            Console.WriteLine();
            foreach (var timing in snapshot.Timings)
                Console.WriteLine($"{timing.Milliseconds,9:0.0} ms  {timing.Stage}");
            Console.WriteLine($"{total.TotalMilliseconds,9:0.0} ms  total (stages overlap, so they do not add up to this)");
        }
        return 0;
    }

    // yar-apps store <words> [--json]: what the Store would show for a search, read from the local catalogs.
    public static async Task<int> StoreAsync(YarHost host, string[] args)
    {
        var started = Stopwatch.GetTimestamp();
        var apps = StoreCatalog.Merge(await host.Store.ReadAsync(CancellationToken.None));
        var words = string.Join(' ', CliOutput.Words(args));
        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(
                StoreCatalog.Search(apps, words, c => c.ToString(), 5),
                CliOutput.PlainJson));
            return 0;
        }
        foreach (var app in StoreCatalog.Search(apps, words, c => c.ToString(), 15))
        {
            Console.WriteLine(
                $"{app.Name,-28} {string.Join(" + ", app.Offers.Select(o => o.SourceId + (o.MakerVerified ? " (verified)" : ""))),-44} {app.Summary}");
        }
        Console.WriteLine(
            $"{apps.Count} apps, {apps.Count(a => a.Offers.Count > 1)} offered by more than one source, " +
            $"read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        return 0;
    }

    // yar-apps permissions <words>: what an installed app is allowed to reach, as its page says it.
    public static async Task<int> PermissionsAsync(YarHost host, string[] args)
    {
        var wanted = string.Join(' ', CliOutput.Words(args));
        var installed = await host.Catalog.LoadAppsAsync(CancellationToken.None);
        var app = installed.Apps.FirstOrDefault(a => a.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        if (app is null)
        {
            Console.Error.WriteLine("No installed app with that name.");
            return 1;
        }
        var raw = host.Ability<IPermissionProvider>(app.ProviderId) is { } permissions
            ? await permissions.GetPermissionsAsync(app, CancellationToken.None)
            : null;
        if (raw is null)
        {
            Console.WriteLine($"{app.Name} ({app.ProviderId}) is not kept apart: it can do whatever you can.");
            return 0;
        }
        var report = FlatpakPermissions.Describe(raw);
        Console.WriteLine($"{app.Name}: {report.Verdict}");
        foreach (var note in report.Notes)
            Console.WriteLine($"  {note.Weight,-6} {note.Text}{(note.Detail is null ? "" : $" ({note.Detail})")}");
        return 0;
    }

    // yar-apps storage [--json]: disks, and where the space goes, as the Storage tab shows it.
    public static async Task<int> StorageAsync(YarHost host, string[] args)
    {
        var loaded = await host.Catalog.LoadAsync(CancellationToken.None);
        var overview = await host.Storage.AnalyzeAsync(loaded, CancellationToken.None);
        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(
                overview, CliOutput.PlainJson));
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
}
