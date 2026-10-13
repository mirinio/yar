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
        var actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        actions.Halign = Gtk.Align.Center;
        actions.MarginTop = 12;
        if (app.PrimaryLauncherPath is { } launcher && !app.Technical.ContainsKey("broken"))
        {
            var open = Gtk.Button.NewWithLabel(T._("Open"));
            open.AddCssClass("suggested-action");
            open.AddCssClass("pill");
            open.OnClicked += (_, _) => AppLauncher.Start(ui, app, launcher);
            actions.Append(open);
        }
        if (ui.Host.Ability<IAppRemover>(app.ProviderId) is { } remover)
        {
            var remove = Gtk.Button.NewWithLabel(T._("Remove…"));
            remove.AddCssClass("pill");
            // An app that can't be removed from here keeps the button, greyed out, with the reason on it.
            if (remover.WhyNotRemovable(app) is { } whyNot)
            {
                remove.Sensitive = false;
                remove.TooltipText = T._(whyNot);
            }
            remove.OnClicked += (_, _) => ChangeDialog.Remove(ui, app);
            actions.Append(remove);
        }
        header.Append(actions);
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

        if (app.Technical.ContainsKey("added-by-yar"))
            page.Add(CreateOrigin(ui, app));
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

    // For an app the user added from a file: where it came from and whether it is still that file.
    private static Adw.PreferencesGroup CreateOrigin(Ui ui, AppInfo app)
    {
        var group = Widgets.Group(
            T._("You added this app yourself"),
            T._("No source looks after it. When a new version comes out, you add that one again."));
        if (app.Technical.TryGetValue("origin", out var origin))
        {
            group.Add(Widgets.Property(
                T._("Came from"),
                Uri.TryCreate(origin, UriKind.Absolute, out var link) && link.Scheme == Uri.UriSchemeHttps ? link.Host : origin));
        }
        if (app.InstalledAt is { } when)
            group.Add(Widgets.Property(T._("Added"), Presentation.Ago(when)));

        var verified = app.Technical.GetValueOrDefault("hash-verified") == "true";
        var hash = Adw.ActionRow.New();
        hash.UseMarkup = false;
        hash.SubtitleLines = 0;
        hash.Title = verified
            ? T._("Matched the publisher's hash when you added it")
            : T._("Was not compared with a hash from its publisher");
        hash.AddPrefix(Gtk.Image.NewFromIconName(verified ? "emblem-ok-symbolic" : "dialog-question-symbolic"));
        // Only a single file can be compared again as a whole.
        if (app.InstallPath is { } path && File.Exists(path) && app.Technical.ContainsKey("sha256") &&
            ui.Host.Ability<IIntegrityChecker>(app.ProviderId) is { } checker)
        {
            var again = Gtk.Button.NewWithLabel(T._("Check again"));
            again.Valign = Gtk.Align.Center;
            again.TooltipText = T._("Reads the file again and tells you whether it is still the one you added");
            again.OnClicked += async (_, _) =>
            {
                again.Sensitive = false;
                hash.Subtitle = T._("Reading the file…");
                try
                {
                    var result = await checker.CheckAsync(app, CancellationToken.None);
                    hash.Subtitle = result.Unchanged switch
                    {
                        true => T._("Unchanged: it is still exactly the file you added."),
                        false => T._("Changed: the file is no longer the one you added. If you did not replace it yourself, be careful with it."),
                        _ => T._("I have nothing to compare it with."),
                    };
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    hash.Subtitle = T._("I couldn't read the file.");
                }
                again.Sensitive = true;
            };
            hash.AddSuffix(again);
        }
        group.Add(hash);
        return group;
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
