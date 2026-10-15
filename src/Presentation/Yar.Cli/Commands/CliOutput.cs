using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yar.Core;
using Yar.Hosting;

namespace Yar.Cli;

/// <summary>What every command shares: reading its words, and printing plans and outcomes.</summary>
internal static class CliOutput
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>As <see cref="Json"/>, but with enums as numbers, as the store and storage output always had it.</summary>
    public static readonly JsonSerializerOptions PlainJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The command: the first word that is not an option.</summary>
    public static string? Command(string[] args) => args.FirstOrDefault(a => !a.StartsWith('-'));

    /// <summary>The words after the command, without options.</summary>
    public static List<string> Words(string[] args) => args.Where(a => !a.StartsWith('-')).Skip(1).ToList();

    /// <summary>True when a change did not go through; says why.</summary>
    public static bool Failed(ChangeRecord done)
    {
        if (done.Outcome == ChangeOutcome.Done)
            return false;
        Console.Error.WriteLine(done.Problem ?? done.Outcome.ToString());
        return true;
    }

    /// <summary>What a change would do, as the window's preview shows it.</summary>
    public static void PrintPlan(ChangePlan plan)
    {
        static string Size(long? bytes) =>
            bytes is { } b ? string.Create(CultureInfo.InvariantCulture, $"{b / 1_000_000.0:0.0} MB") : "?";

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

    /// <summary>
    /// The end of every command that changes something: without --yes, say that nothing changed.
    /// Returns the exit code to stop with, or null when the change should go ahead.
    /// </summary>
    public static int? StopUnlessConfirmed(string[] args)
    {
        if (args.Contains("--yes"))
            return null;
        Console.WriteLine("Nothing was changed. Add --yes to do it.");
        return 0;
    }

    /// <summary>Prints how far the running change has come, one line per new state, until disposed.</summary>
    public static IDisposable FollowProgress(YarHost host) => new ProgressPrinter(host);

    private sealed class ProgressPrinter : IDisposable
    {
        private readonly YarHost _host;
        private readonly Lock _gate = new();
        private string _shown = "";

        public ProgressPrinter(YarHost host)
        {
            _host = host;
            host.Operations.Changed += Show;
        }

        public void Dispose() => _host.Operations.Changed -= Show;

        private void Show()
        {
            if (_host.Operations.Current is not { } running)
                return;
            var line = $"{running.Progress.Phase} {(running.Progress.Fraction is { } f ? $"{f:P0}" : "")} {running.Progress.Detail}".Trim();
            lock (_gate)
            {
                if (line != _shown)
                    Console.WriteLine("  " + (_shown = line));
            }
        }
    }
}
