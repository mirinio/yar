#if YAR_SCREENSHOTS
using Yar.Core;

namespace Yar.App;

// Takes the pictures for the README. Only built in the Screenshots configuration, by
// scripts/screenshots.sh; normal and release builds contain none of this.
//
// YAR_SHOT_STEPS is a list of steps separated by ";", run one after the other:
//   wait:<ms>                 section:<apps|store|history|...>   store:<component id>
//   install:<component id>[:<provider>]                          installnow:<component id>[:<provider>]
//   app:<app or component id>                                    remove:<app or component id>
//   add:<file>                addstart                           shot:<file name>
// Pictures are saved into YAR_SHOT_DIR; Yar quits after the last step.
internal sealed partial class MainWindow
{
    private void PrepareScreenshots()
    {
        if (Environment.GetEnvironmentVariable("YAR_SHOT_SIZE")?.Split('x') is [var w, var h] &&
            int.TryParse(w, out var width) && int.TryParse(h, out var height))
        {
            _window.SetDefaultSize(width, height);
        }
        Adw.StyleManager.GetDefault().ColorScheme = Environment.GetEnvironmentVariable("YAR_THEME") == "light"
            ? Adw.ColorScheme.ForceLight
            : Adw.ColorScheme.ForceDark;
    }

    private async void StartScreenshots()
    {
        var steps = (Environment.GetEnvironmentVariable("YAR_SHOT_STEPS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var folder = Environment.GetEnvironmentVariable("YAR_SHOT_DIR") ?? ".";
        if (steps.Length == 0)
            return;

        // The list of apps has to be there before anything can be shown.
        while (_ui is null)
            await Task.Delay(200);

        foreach (var step in steps)
        {
            var parts = step.Split(':', 3);
            var argument = parts.Length > 1 ? parts[1] : "";
            try
            {
                await RunStep(parts[0], argument, parts.Length > 2 ? parts[2] : null, folder);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"screenshot step '{step}' failed: {e.Message}");
            }
        }
        _window.Destroy();
    }

    private async Task RunStep(string kind, string argument, string? extra, string folder)
    {
        var ui = _ui!;
        switch (kind)
        {
            case "wait":
                await Task.Delay(int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case "section":
                _sidebar.SelectRow(_sidebar.GetRowAtIndex(Array.FindIndex(_sections, s => s.Name == argument)));
                break;
            case "store" or "install" or "installnow":
                var catalog = StoreCatalog.Merge(await ui.Host.Store.ReadAsync(CancellationToken.None));
                var app = catalog.First(a => a.KnownIds.Any(id => StoreCatalog.Normalize(id) == StoreCatalog.Normalize(argument)));
                var offer = app.Offers.FirstOrDefault(o => extra is null || o.ProviderId == extra) ?? app.Offers[0];
                if (kind == "store")
                {
                    ui.Push(StoreAppPage.Create(ui, new StoreData(catalog), app));
                }
                else if (kind == "install")
                {
                    ChangeDialog.Install(ui, app, offer);
                }
                else
                {
                    // A real install, without the preview, so that the strip at the bottom has something to show.
                    var installer = ui.Host.Ability<IAppInstaller>(offer.ProviderId)!;
                    var plan = await installer.PlanInstallAsync(offer, installer.TargetsFor(offer, ui.Snapshot.Sources)[0], app.Name, CancellationToken.None);
                    _ = ui.Host.Make.InstallAsync(plan);
                }
                break;
            case "app" or "remove":
                var installed = ui.Snapshot.Apps.First(a => a.Id == argument || a.ComponentId == argument);
                ui.ShowApp(installed);
                if (kind == "remove")
                    ChangeDialog.Remove(ui, installed);
                break;
            case "add":
                AddAppPage.Open(ui, argument);
                break;
            case "addstart":
                ui.Push(AddAppStart.Create(ui));
                break;
            case "shot":
                var paintable = Gtk.WidgetPaintable.New(_window);
                var snapshot = Gtk.Snapshot.New();
                paintable.Snapshot(snapshot, _window.GetWidth(), _window.GetHeight());
                if (snapshot.ToNode() is { } node)
                    _window.GetNative()!.GetRenderer()!.RenderTexture(node, null).SaveToPng(Path.Combine(folder, argument));
                break;
            default:
                throw new InvalidOperationException($"unknown step {kind}");
        }
    }
}
#endif
