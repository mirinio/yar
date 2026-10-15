using Yar.Core;

namespace Yar.App;

/// <summary>One kind of software (Flatpak, system packages, ...): what it is, where it lives, what came through it.</summary>
internal static class KindPage
{
    public static Adw.NavigationPage Create(Ui ui, string providerId)
    {
        var page = Adw.PreferencesPage.New();
        var title = ui.Presentation.ProviderTitle(providerId);
        var report = ui.Snapshot.Providers.FirstOrDefault(r => r.ProviderId == providerId);
        var facts = ui.Snapshot.Facts.FirstOrDefault(f => f.ProviderId == providerId);

        var what = Widgets.Group(T._("What it is"));
        what.Add(Widgets.Paragraph(ui.Presentation.KindExplanation(providerId)));
        page.Add(what);

        if (report?.Status.Availability == ProviderAvailability.NotInstalled)
        {
            var absent = Widgets.Group();
            absent.Add(Widgets.Property(T._("On this computer"), T._("Not set up here. You do not need it unless an app asks for it.")));
            page.Add(absent);
        }
        else if (report?.Status.Availability == ProviderAvailability.Error)
        {
            var failed = Widgets.Group();
            failed.Add(Widgets.Property(T._("Problem"), T.F("I couldn't read this: {0}", report.Status.Reason ?? "")));
            page.Add(failed);
        }

        if (facts?.HomepageUrl is { } homepage)
        {
            var more = Widgets.Group(T._("Learn more"));
            more.Add(Widgets.Link(ui, T._("Website"), homepage));
            page.Add(more);
        }

        var locations = facts?.Locations.Where(l => Directory.Exists(l.Path)).ToList() ?? [];
        if (locations.Count > 0)
        {
            var where = Widgets.Group(T._("Where it is"), T._("The folders on your computer that belong to it."));
            foreach (var location in locations)
                where.Add(Widgets.PathRow(ui, T._(location.Label), location.Path));
            page.Add(where);
        }

        var sources = ui.Presentation.SourcesOf(providerId, ui.Technical);
        if (sources.Count > 0 && providerId != "manual")
        {
            var group = Widgets.Group(
                T._("Its sources"),
                help: T._("A source is a place apps are downloaded from, like a store. Your computer checks the " +
                    "sources that are turned on when it looks for new apps and updates."));
            foreach (var source in sources)
                group.Add(SourceWidgets.Row(ui, source));
            page.Add(group);
        }

        var apps = ui.Presentation.AppsOf(providerId);
        if (report?.Status.Availability == ProviderAvailability.Available)
        {
            var group = Widgets.Group(
                T._("Installed apps"),
                apps.Count == 0
                    ? T._("Nothing installed this way yet.")
                    : T.F(apps.Count == 1 ? "{0} app installed this way" : "{0} apps installed this way", apps.Count));
            foreach (var app in apps)
                group.Add(Widgets.AppRow(ui, app));
            page.Add(group);
        }

        return Widgets.Page(title, page);
    }
}
