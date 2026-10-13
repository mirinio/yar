using Yar.Core;

namespace Yar.App;

/// <summary>One source (Flathub, a repository, ...) and the apps that came from it.</summary>
internal static class SourcePage
{
    public static Adw.NavigationPage Create(Ui ui, SourceInfo source)
    {
        var page = Adw.PreferencesPage.New();

        var about = Widgets.Group(source.Name, source.Description);
        var kind = Widgets.Navigation(
            T._("Kind"), ui.Presentation.ProviderTitle(source.ProviderId), () => ui.ShowKind(source.ProviderId));
        kind.AddCssClass("property");
        about.Add(kind);
        if (source.ProviderId != "manual")
        {
            var trust = Widgets.Property(T._("How well I know it"), Presentation.TrustText(source.Trust));
            trust.AddSuffix(Widgets.TrustTag(source.Trust));
            trust.AddSuffix(Widgets.HelpButton(Presentation.TrustExplanation(source.Trust)));
            about.Add(trust);

            var manager = ui.Host.SourceManagerFor(source.ProviderId);
            if (manager?.CanToggle(source) == true)
            {
                var status = Adw.ActionRow.New();
                status.UseMarkup = false;
                status.Title = T._("Turned on");
                status.Subtitle = source.Scope == SourceScope.User
                    ? T._("When on, your computer looks here for apps and updates.")
                    : T._("When on, your computer looks here for apps and updates. Changing this asks for your password.");
                var toggle = Widgets.SourceSwitch(ui, manager, source);
                status.AddSuffix(toggle);
                status.ActivatableWidget = toggle;
                about.Add(status);
            }
            else
            {
                about.Add(Widgets.Property(
                    T._("Status"),
                    source.Enabled
                        ? T._("Turned on. Your computer looks here for apps and updates.")
                        : T._("Turned off. Your computer does not look here.")));
            }
        }
        page.Add(about);

        source.Technical.TryGetValue("homepage", out var homepage);
        source.Technical.TryGetValue("file", out var file);
        if (homepage is not null || source.Url is not null || file is not null)
        {
            var where = Widgets.Group(T._("Where it is"));
            if (homepage is not null)
                where.Add(Widgets.Link(ui, T._("Website"), homepage));
            if (source.Url is not null)
            {
                var address = Widgets.Property(T._("Download address"), source.Url);
                address.AddSuffix(Widgets.HelpButton(
                    T._("This is where your computer fetches apps from. It is meant for programs, not for people, " +
                        "so it usually shows nothing useful in a web browser.")));
                where.Add(address);
            }
            if (file is not null)
                where.Add(Widgets.PathRow(ui, T._("Settings file"), file));
            page.Add(where);
        }

        if (ui.Technical)
        {
            var details = Widgets.Group(T._("Technical details"));
            details.Add(Widgets.Property("id", source.Id));
            foreach (var (key, value) in source.Technical.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                details.Add(Widgets.Property(key, value));
            page.Add(details);
        }

        var apps = ui.Presentation.AppsOf(source);
        var installed = Widgets.Group(
            T._("Apps from here"),
            apps.Count == 0
                ? T._("Nothing installed from here yet.")
                : T.F(apps.Count == 1 ? "{0} of your apps came from here" : "{0} of your apps came from here", apps.Count));
        foreach (var app in apps)
            installed.Add(Widgets.AppRow(ui, app));
        page.Add(installed);

        if (ui.Host.SourceManagerFor(source.ProviderId) is { } remover && remover.CanRemove(source))
        {
            var danger = Widgets.Group(
                T._("Remove this source"),
                apps.Count == 0
                    ? T._("You added this source yourself. Removing it only takes its apps out of the Store.")
                    : T._("You still have apps from this source. Remove those first; otherwise they would stop getting updates."));
            var remove = Gtk.Button.NewWithLabel(T._("Remove source"));
            remove.AddCssClass("destructive-action");
            remove.Halign = Gtk.Align.Start;
            remove.Sensitive = apps.Count == 0;
            remove.OnClicked += (_, _) => Widgets.Confirm(
                ui, T.F("Remove {0}?", source.Name),
                T._("Its apps will no longer be offered in the Store. You can add it again later."),
                T._("Remove"), destructive: true, () => Remove(ui, remover, source, remove));
            danger.Add(remove);
            page.Add(danger);
        }

        return Widgets.Page(source.Name, page);
    }

    private static async void Remove(Ui ui, ISourceManager manager, SourceInfo source, Gtk.Button button)
    {
        button.Sensitive = false;
        try
        {
            await manager.RemoveAsync(source, CancellationToken.None);
            ui.Toast(T.F("I've removed {0}.", source.Name));
            ui.Refresh();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            button.Sensitive = true;
            ui.Toast(T.F("Hmm, I couldn't remove it: {0}", AddSourcePages.Short(e.Message)));
        }
    }
}
