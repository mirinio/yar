using Yar.App;
using Yar.Hosting;

using var host = new YarHost();

// KDE and others write "prefer dark theme" into GTK's settings file. libadwaita takes light or
// dark from the desktop itself and prints a warning when it finds that old setting switched on,
// so it is cleared for this run before libadwaita starts. The settings file is not touched.
Adw.Module.Initialize();
Gtk.Functions.Init();
if (Gtk.Settings.GetDefault() is { GtkApplicationPreferDarkTheme: true } gtkSettings)
    gtkSettings.GtkApplicationPreferDarkTheme = false;
var application = Adw.Application.New(YarHost.AppId, Gio.ApplicationFlags.DefaultFlags);
application.OnActivate += (sender, _) =>
{
    var app = (Adw.Application)sender;
    // A second launch only raises the window that is already there.
    if (app.GetActiveWindow() is { } existing)
    {
        existing.Present();
        return;
    }
    new MainWindow(app, host).Present();
};
return application.RunWithSynchronizationContext(args);
