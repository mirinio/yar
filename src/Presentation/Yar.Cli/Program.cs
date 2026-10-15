using Yar.Cli;
using Yar.Hosting;

// yar-apps: what Yar sees and does, on the command line. Used to check the backends without the
// window, and by the test container to install, remove and update for real. Commands that change
// something print their plan and do nothing without --yes.

if (args.Contains("--version"))
{
    Console.WriteLine($"yar-apps {Yar.Core.AppIdentity.Version}");
    return 0;
}

Func<YarHost, string[], Task<int>>? run = CliOutput.Command(args) switch
{
    "sources" or "apps" or "status" or "unclaimed" or "updates" or "attention" => ReadCommands.CatalogAsync,
    "store" => ReadCommands.StoreAsync,
    "permissions" => ReadCommands.PermissionsAsync,
    "storage" => ReadCommands.StorageAsync,
    "install" or "remove" => ChangeCommands.InstallOrRemoveAsync,
    "update" => ChangeCommands.UpdateAsync,
    "history" => ChangeCommands.HistoryAsync,
    "undo" => ChangeCommands.UndoAsync,
    "inspect" or "add" => IntakeCommands.InspectOrAddAsync,
    "verify" => IntakeCommands.VerifyAsync,
    "source-add" or "source-remove" => SourceCommands.AddOrRemoveAsync,
    "source-switch" => SourceCommands.SwitchAsync,
    _ => null,
};
if (run is null)
{
    Console.Error.WriteLine("Usage: yar-apps <sources|apps|status|unclaimed|updates|attention|storage|store <words>> [--json] [--timings]");
    Console.Error.WriteLine("       yar-apps install <app id> | remove <app> | inspect <file> | add <file or https link> | verify <app>");
    Console.Error.WriteLine("       yar-apps update <app or package> | update --all");
    Console.Error.WriteLine("       yar-apps history | undo <id>");
    Console.Error.WriteLine("Commands that change something print their plan and do nothing without --yes.");
    return 2;
}

// A failed command ends with a message and a failure code, not a crash report.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.Error.WriteLine(e.ExceptionObject is Exception error ? error.Message : "Something went wrong.");
    Environment.Exit(1);
};

using var host = new YarHost();
return await run(host, args);
