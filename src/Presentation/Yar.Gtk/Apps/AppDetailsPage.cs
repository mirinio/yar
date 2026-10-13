using Yar.Core;

namespace Yar.App;

/// <summary>Everything Yar knows about one app, including where it lives on disk.</summary>
internal static class AppDetailsPage
{
    public static Adw.NavigationPage Create(Ui ui, AppInfo app)
    {
        var page = Adw.PreferencesPage.New();

        var header = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        header.Append(Presentation.IconFor(app, 96));
        var name = Gtk.Label.New(app.Name);
        name.AddCssClass("title-1");
        name.Wrap = true;
        name.Justify = Gtk.Justification.Center;
        header.Append(name);
        if (app.Summary is not null)
        {
            var summary = Gtk.Label.New(app.Summary);
            summary.AddCssClass("dim-label");
            summary.Wrap = true;
            summary.Justify = Gtk.Justification.Center;
            header.Append(summary);
        }
        var tags = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        tags.Halign = Gtk.Align.Center;
        tags.MarginTop = 6;
        foreach (var category in app.Categories)
            tags.Append(Widgets.Tag(Presentation.CategoryName(category)));
        header.Append(tags);
        if (app.PrimaryLauncherPath is { } launcher && !app.Technical.ContainsKey("broken"))
        {
            var open = Gtk.Button.NewWithLabel(T._("Open"));
            open.AddCssClass("suggested-action");
            open.AddCssClass("pill");
            open.Halign = Gtk.Align.Center;
            open.MarginTop = 12;
            open.OnClicked += (_, _) => AppLauncher.Start(ui, app, launcher);
            header.Append(open);
        }
        var headerGroup = Adw.PreferencesGroup.New();
        headerGroup.Add(header);
        page.Add(headerGroup);

        var about = Widgets.Group();
        var origin = Widgets.Navigation(T._("Comes from"), ui.Presentation.OriginName(app), () => ui.ShowOrigin(app));
        origin.AddCssClass("property");
        about.Add(origin);
        if (app.Version is not null)
            about.Add(Widgets.Property(T._("Version"), app.Version));
        if (app.SizeBytes is { } size)
            about.Add(Widgets.Property(T._("Size"), Presentation.FormatSize(size)));
        if (ui.Presentation.UpdateFor(app) is { } update)
        {
            about.Add(Widgets.Property(
                T._("Update waiting"),
                update.NewVersion is null
                    ? Presentation.SeverityName(update.Severity)
                    : $"{update.NewVersion} · {Presentation.SeverityName(update.Severity)}"));
        }
        if (app.Technical.ContainsKey("broken"))
            about.Add(Widgets.Property(T._("Problem"), T._("This launcher points to a program that is no longer there.")));
        if (app.LauncherPaths.Count == 0)
            about.Add(Widgets.Property(T._("App menu"), T._("This app is not in your app menu yet.")));
        page.Add(about);

        page.Add(CreateLocations(ui, app));
        AddPermissions(ui, app, page);

        if (ui.Technical)
        {
            var details = Widgets.Group(T._("Technical details"));
            details.Add(Widgets.Property("provider", app.ProviderId));
            details.Add(Widgets.Property("id", app.Id));
            if (app.SourceId is not null)
                details.Add(Widgets.Property("source", app.SourceId));
            if (app.ComponentId is not null)
                details.Add(Widgets.Property("component", app.ComponentId));
            foreach (var (key, value) in app.Technical.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                details.Add(Widgets.Property(key, value));
            page.Add(details);
        }

        return Widgets.Page(app.Name, page);
    }

    private static Adw.PreferencesGroup CreateLocations(Ui ui, AppInfo app)
    {
        var group = Widgets.Group(
            T._("Where it is"),
            help: T._("On Linux an app is not always one folder. Here are the places that belong to this app. " +
                "You can copy a path or open it in your file manager."));

        if (app.InstallPath is not null)
        {
            group.Add(Widgets.PathRow(
                ui, File.Exists(app.InstallPath) ? T._("The app file") : T._("Installed in"), app.InstallPath));
        }
        else if (app.ProviderId == "packagekit")
        {
            var note = Adw.ActionRow.New();
            note.UseMarkup = false;
            note.Title = T._("This app has no folder of its own");
            note.Subtitle = T._("System packages put their files into shared system folders. The program itself is below.");
            group.Add(note);
        }

        if (app.Technical.TryGetValue("program", out var program) && program != app.InstallPath)
            group.Add(Widgets.PathRow(ui, T._("Program"), program));
        if (app.DataPath is not null)
            group.Add(Widgets.PathRow(ui, T._("Where it keeps your data"), app.DataPath));
        foreach (var launcher in app.LauncherPaths)
            group.Add(Widgets.PathRow(ui, T._("App menu entry"), launcher));
        return group;
    }

    // Flatpak apps are asked for their permissions when the page opens; the group appears when the answer does.
    private static async void AddPermissions(Ui ui, AppInfo app, Adw.PreferencesPage page)
    {
        if (ui.Host.Ability<IPermissionProvider>(app.ProviderId) is not { } provider)
        {
            // Snaps are confined too, but I cannot read their rules yet, so I say nothing rather than guess.
            if (app.ProviderId != "snap")
                page.Add(Widgets.PermissionsGroup(null, ui.Technical));
            return;
        }

        try
        {
            if (await provider.GetPermissionsAsync(app, CancellationToken.None) is { } raw)
                page.Add(Widgets.PermissionsGroup(FlatpakPermissions.Describe(raw), ui.Technical));
        }
        // Permissions are an extra on this page; whatever goes wrong looking them up, the page stays.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
    }
}
