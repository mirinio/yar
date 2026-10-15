using Yar.App;
using Yar.Hosting;

using var host = new YarHost();

// KDE and others write "prefer dark theme" into GTK's settings file. libadwaita takes light or
// dark from the desktop itself and prints a warning when it finds that old setting switched on.
// GTK is therefore started first, on its own, and the setting cleared for this run before
// libadwaita is loaded at all. The settings file is not touched.
Gtk.Module.Initialize();
Gtk.Functions.Init();
if (Gtk.Settings.GetDefault() is { GtkApplicationPreferDarkTheme: true } gtkSettings)
    gtkSettings.GtkApplicationPreferDarkTheme = false;
Adw.Module.Initialize();

#if YAR_SCREENSHOTS
// Pictures for the README are taken by a second copy running beside any Yar that is already open.
const Gio.ApplicationFlags Flags = Gio.ApplicationFlags.NonUnique;
#else
const Gio.ApplicationFlags Flags = Gio.ApplicationFlags.DefaultFlags;
#endif
var application = Adw.Application.New(YarHost.AppId, Flags);
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
// GLib expects the program's own name first, as in C. Without it, the first real argument is taken
// for the name and options such as --help are never seen.
return application.RunWithSynchronizationContext(["yar", .. args]);
