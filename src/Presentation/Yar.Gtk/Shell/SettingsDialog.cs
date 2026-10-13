using Yar.Core;
using Yar.Hosting;

namespace Yar.App;

/// <summary>The settings: what to show, what may be fetched from the internet, and the distribution's opt-in.</summary>
internal static class SettingsDialog
{
    /// <param name="changed">Called after the dialog closes, if anything that affects the pages was changed.</param>
    public static void Present(
        Gtk.Window window, YarHost host, Settings settings, CatalogSnapshot? snapshot, Action changed, Action refresh)
    {
        var dialog = Adw.PreferencesDialog.New();
        dialog.Title = T._("Settings");
        var page = Adw.PreferencesPage.New();

        var technical = Adw.SwitchRow.New();
        technical.Title = T._("Show technical details");
        technical.Subtitle = T._("Package names, IDs and file paths, for when you want to look under the hood.");
        technical.Active = settings.ShowTechnicalDetails;

        var online = Adw.SwitchRow.New();
        online.Title = T._("Ratings, pictures and popular apps from the internet");
        online.Subtitle = T._(
            "When on, the Store shows star ratings and reviews from odrs.gnome.org, and popular lists, install " +
            "counts and pictures of apps from flathub.org and your distribution's servers. To get these I send " +
            "which app you are looking at, your language and the name of your Linux distribution; never your " +
            "name or anything else about your computer. When off, the Store still works from what is already " +
            "on your computer.");
        online.Active = settings.UseOnlineData;

        var general = Adw.PreferencesGroup.New();
        general.Add(technical);
        general.Add(online);
        page.Add(general);

        var (developerGroup, developer, languages) = CreateDeveloperGroup(host, settings);
        page.Add(developerGroup);

        if (host.ThirdParty.IsAvailable)
        {
            page.Add(ThirdPartyGroup.Create(
                host, snapshot, host.Os.Name ?? T._("your distribution"), dialog,
                toast: text => dialog.AddToast(Adw.Toast.New(text)), refresh));
        }

        dialog.Add(page);
        dialog.OnClosed += (_, _) =>
        {
            var chosen = languages();
            var same = technical.Active == settings.ShowTechnicalDetails && online.Active == settings.UseOnlineData &&
                developer.Active == settings.ShowDeveloperProjects &&
                (settings.DeveloperLanguages ?? []).SequenceEqual(chosen);
            if (same)
                return;

            settings.ShowTechnicalDetails = technical.Active;
            settings.UseOnlineData = online.Active;
            settings.ShowDeveloperProjects = developer.Active;
            // Remember the ticks only once the section has been used, so the first switch-on can suggest some.
            if (developer.Active || settings.DeveloperLanguages is not null)
                settings.DeveloperLanguages = [.. chosen];
            settings.Save();
            changed();
        };
        dialog.Present(window);
    }

    private static (Adw.PreferencesGroup Group, Adw.SwitchRow Switch, Func<List<string>> Languages) CreateDeveloperGroup(
        YarHost host, Settings settings)
    {
        var group = Widgets.Group(
            T._("Open Source tab"),
            T._("Not needed for using apps. Leave it off if you do not write software."),
            T._("When on, the sidebar gets an Open Source tab. It lists source-code projects on GitHub that were " +
                "started recently and that many people marked with a star, for the programming languages you tick. " +
                "I ask GitHub's public search for them, without any account. These are projects to read about, " +
                "not apps I can install."));

        var developer = Adw.SwitchRow.New();
        developer.Title = T._("Show the Open Source tab");
        developer.Subtitle = T._("Needs the internet switch above to be on.");
        developer.Active = settings.ShowDeveloperProjects;
        group.Add(developer);

        var expander = Adw.ExpanderRow.New();
        expander.Title = T._("Programming languages");
        group.Add(expander);

        var checks = new List<(string Name, Gtk.CheckButton Check)>();
        foreach (var (name, _) in DevLanguages.All)
        {
            var check = Gtk.CheckButton.New();
            check.Valign = Gtk.Align.Center;
            check.Active = settings.DeveloperLanguages?.Contains(name) == true;
            var row = Adw.ActionRow.New();
            row.UseMarkup = false;
            row.Title = name;
            row.AddPrefix(check);
            row.ActivatableWidget = check;
            expander.AddRow(row);
            checks.Add((name, check));
        }

        void Describe()
        {
            var ticked = checks.Where(c => c.Check.Active).Select(c => c.Name).ToList();
            expander.Subtitle = ticked.Count == 0 ? T._("None ticked yet") : string.Join(", ", ticked);
            expander.Sensitive = developer.Active;
        }
        foreach (var (_, check) in checks)
            check.OnToggled += (_, _) => Describe();

        developer.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() != "active")
                return;
            // First time on: tick the languages whose tools are installed, as a starting point.
            if (developer.Active && settings.DeveloperLanguages is null && checks.All(c => !c.Check.Active))
            {
                var detected = DevLanguages.Detect(host.SearchPath);
                foreach (var (name, check) in checks)
                    check.Active = detected.Contains(name);
                expander.Expanded = true;
            }
            Describe();
        };
        Describe();

        return (group, developer, () => checks.Where(c => c.Check.Active).Select(c => c.Name).ToList());
    }
}
