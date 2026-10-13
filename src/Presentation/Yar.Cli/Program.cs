using System.Text.Json;
using System.Text.Json.Serialization;
using Yar.Hosting;

// Developer tool: prints what the backends see, so they can be checked without the GUI.

var json = args.Contains("--json");
var timings = args.Contains("--timings");
var command = args.FirstOrDefault(a => !a.StartsWith('-'));
if (command is not ("sources" or "apps" or "status" or "unclaimed" or "updates" or "store" or "source-add" or "source-remove"))
{
    Console.Error.WriteLine("Usage: yar-apps <sources|apps|status|unclaimed|updates|store <words>> [--json] [--timings]");
    return 2;
}

using var host = new YarHost();

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
        await manager.AddAsync(request, CancellationToken.None);
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
        await manager.RemoveAsync(source, CancellationToken.None);
        Console.WriteLine($"removed {source.Id}");
    }
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
