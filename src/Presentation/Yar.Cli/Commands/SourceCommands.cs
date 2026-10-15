using Yar.Core;
using Yar.Hosting;

namespace Yar.Cli;

/// <summary>Commands that change sources, as the Sources tab does.</summary>
internal static class SourceCommands
{
    // yar-apps source-add <https://….flatpakrepo> [name] | source-remove <source id>:
    // Flatpak sources for the current user only.
    public static async Task<int> AddOrRemoveAsync(YarHost host, string[] args)
    {
        var manager = host.Ability<ISourceManager>("flatpak") ?? throw new InvalidOperationException("Flatpak is not available.");
        var values = CliOutput.Words(args);
        if (CliOutput.Command(args) == "source-add")
        {
            var (request, problem) = SourceAddress.Check(values.ElementAtOrDefault(0), values.ElementAtOrDefault(1));
            if (request is null)
            {
                Console.Error.WriteLine(problem);
                return 1;
            }
            if (CliOutput.Failed(await host.Make.AddSourceAsync("flatpak", request, request.Name)))
                return 1;
            Console.WriteLine($"added {request.Name}");
            return 0;
        }

        var all = await host.Catalog.LoadAppsAsync(CancellationToken.None);
        var source = all.Sources.FirstOrDefault(s => s.ProviderId == "flatpak" && s.Id == values.ElementAtOrDefault(0));
        if (source is null || !manager.CanRemove(source))
        {
            Console.Error.WriteLine("No such source, or not one I may remove.");
            return 1;
        }
        if (CliOutput.Failed(await host.Make.RemoveSourceAsync(source)))
            return 1;
        Console.WriteLine($"removed {source.Id}");
        return 0;
    }

    // yar-apps source-switch <kind> <source id> on|off: switch a source, as the switch in the window does.
    // On a real system this asks for a password; it exists so the test container can exercise it.
    public static async Task<int> SwitchAsync(YarHost host, string[] args)
    {
        var values = CliOutput.Words(args);
        if (values.Count != 3 || values[2] is not ("on" or "off"))
        {
            Console.Error.WriteLine("Usage: yar-apps source-switch <flatpak|packagekit> <source id> <on|off>");
            return 2;
        }
        var manager = host.Ability<ISourceManager>(values[0]);
        var all = await host.Catalog.LoadAppsAsync(CancellationToken.None);
        var source = all.Sources.FirstOrDefault(s => s.ProviderId == values[0] && s.Id == values[1]);
        if (manager is null || source is null || !manager.CanToggle(source))
        {
            Console.Error.WriteLine("No such source, or not one I may switch.");
            return 1;
        }
        if (CliOutput.Failed(await host.Make.SwitchSourceAsync(source, values[2] == "on")))
            return 1;
        Console.WriteLine($"{source.Id} is now {values[2]}");
        return 0;
    }
}
