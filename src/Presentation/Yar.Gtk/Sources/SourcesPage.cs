using Yar.Core;

namespace Yar.App;

/// <summary>"Sources": the kinds of software on this computer. Each leads to its own page.</summary>
internal sealed class SourcesPage
{
    private readonly Adw.PreferencesPage _page = Adw.PreferencesPage.New();
    private readonly List<Adw.PreferencesGroup> _groups = [];

    public Gtk.Widget Widget => _page;

    public void Show(Ui ui)
    {
        foreach (var group in _groups)
            _page.Remove(group);
        _groups.Clear();

        var present = ui.Snapshot.Providers
            .Where(report => report.Status.Availability != ProviderAvailability.NotInstalled).ToList();
        var absent = ui.Snapshot.Providers.Except(present).ToList();

        var here = Widgets.Group(
            T._("Where your apps come from"),
            T._("Pick one to see what it is, where it keeps things, and which of your apps came through it."),
            T._("On Linux, apps can reach your computer in several ways. Each way has its own sources: " +
                "stores or collections it downloads from. Most people use two or three of them side by side."));
        foreach (var report in present)
            here.Add(KindRow(ui, report));
        Add(here);

        var more = Widgets.Group(
            T._("Add a source"),
            T._("More sources mean more apps to choose from in the Store."));
        more.Add(Widgets.Navigation(
            T._("Popular sources"), T._("A list of sources many people use, with how well I know each"),
            () => ui.Push(AddSourcePages.CreatePopular(ui))));
        more.Add(Widgets.Navigation(
            T._("Add by address"), T._("If a website gave you the address of its own source"),
            () => ui.Push(AddSourcePages.CreateByAddress(ui))));
        if (ui.Host.ThirdParty.IsAvailable)
        {
            more.Add(Widgets.Navigation(
                T.F("Software from outside {0}", ui.Presentation.DistroName),
                T._("One switch for a few well-known sources your distribution keeps off until you say yes"),
                ui.OpenSettings));
        }
        Add(more);

        if (absent.Count > 0)
        {
            var missing = Widgets.Group(
                T._("Not on this computer"),
                help: T._("These ways of installing apps exist, but are not set up here. That is fine; " +
                    "you do not need all of them."));
            foreach (var report in absent)
            {
                var row = KindRow(ui, report);
                row.AddCssClass("dim-label");
                missing.Add(row);
            }
            Add(missing);
        }
    }

    private void Add(Adw.PreferencesGroup group)
    {
        _page.Add(group);
        _groups.Add(group);
    }

    private static Adw.ActionRow KindRow(Ui ui, ProviderReport report)
    {
        var id = report.ProviderId;
        var tags = new List<Gtk.Widget>();
        if (report.Status.Availability == ProviderAvailability.Error)
        {
            tags.Add(Widgets.Tag(T._("Couldn't read"), attention: true));
        }
        else if (report.Status.Availability == ProviderAvailability.Available)
        {
            var sources = ui.Presentation.SourcesOf(id, ui.Technical).Count;
            var apps = ui.Presentation.AppsOf(id).Count;
            if (id != "manual")
                tags.Add(Widgets.Tag(T.F(sources == 1 ? "{0} source" : "{0} sources", sources)));
            tags.Add(Widgets.Tag(T.F(apps == 1 ? "{0} app" : "{0} apps", apps)));
        }

        var row = Widgets.Navigation(
            ui.Presentation.ProviderTitle(id), Presentation.ProviderDescription(id), () => ui.ShowKind(id), [.. tags]);
        row.AddPrefix(Gtk.Image.NewFromIconName(Presentation.KindIcon(id)));
        return row;
    }
}
