using Yar.Core;

namespace Yar.App;

/// <summary>"My Apps": every installed app, split into what came from a source and what you set up yourself.</summary>
internal sealed class AppsPage
{
    private enum Origin { All, FromSources, SelfInstalled }

    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.SearchEntry _search = Gtk.SearchEntry.New();
    private readonly Gtk.Box _filters = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Adw.PreferencesPage _page = Adw.PreferencesPage.New();
    private readonly Adw.PreferencesPage _results = Adw.PreferencesPage.New();
    private readonly List<Adw.PreferencesGroup> _resultGroups = [];
    private Ui? _ui;

    private readonly List<Section> _sections = [];
    private Gtk.DropDown? _categoryDropDown;
    private Adw.PreferencesGroup? _attention;
    private List<AppCategory?> _categoryChoices = [null];
    private Origin _origin = Origin.All;
    private AppCategory? _category;

    public AppsPage()
    {
        _search.PlaceholderText = T._("Search by name or by tag, like \"games\"");
        _search.Hexpand = true;
        _search.OnSearchChanged += (_, _) => ApplyFilter();

        var originButtons = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        originButtons.AddCssClass("linked");
        Gtk.ToggleButton? first = null;
        foreach (var (origin, label) in new[]
        {
            (Origin.All, T._("All")),
            (Origin.FromSources, T._("From sources")),
            (Origin.SelfInstalled, T._("Installed myself")),
        })
        {
            var button = Gtk.ToggleButton.NewWithLabel(label);
            if (first is null)
            {
                first = button;
                button.Active = true;
            }
            else
            {
                button.SetGroup(first);
            }
            button.OnToggled += (_, _) =>
            {
                if (!button.Active)
                    return;
                _origin = origin;
                ApplyFilter();
            };
            originButtons.Append(button);
        }
        _filters.Append(originButtons);

        var add = Gtk.Button.NewWithLabel(T._("Add an app…"));
        add.TooltipText = T._("Add an app you downloaded as a file, or from a download link");
        add.Halign = Gtk.Align.End;
        add.Hexpand = true;
        add.OnClicked += (_, _) =>
        {
            if (_ui is { } ui)
                ui.Push(AddAppStart.Create(ui));
        };
        _filters.Append(add);

        var controls = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        controls.Append(_search);
        controls.Append(_filters);
        var clamp = Adw.Clamp.New();
        clamp.MaximumSize = 600;
        clamp.Child = controls;
        clamp.MarginTop = 12;
        clamp.MarginBottom = 6;
        clamp.MarginStart = 12;
        clamp.MarginEnd = 12;

        var nothingFound = Adw.StatusPage.New();
        nothingFound.IconName = "edit-find-symbolic";
        nothingFound.Title = T._("Nothing found");
        nothingFound.Description = T._("I couldn't find an installed app that matches.");

        var nothingInstalled = Adw.StatusPage.New();
        nothingInstalled.IconName = "application-x-executable-symbolic";
        nothingInstalled.Title = T._("No apps yet");
        nothingInstalled.Description = T._("I couldn't find any installed apps on this computer.");

        _stack.AddNamed(_page, "list");
        _stack.AddNamed(_results, "results");
        _stack.AddNamed(nothingFound, "nothing-found");
        _stack.AddNamed(nothingInstalled, "nothing-installed");
        _stack.Vexpand = true;

        _root.Append(clamp);
        _root.Append(_stack);
    }

    public Gtk.Widget Widget => _root;

    public void Show(Ui ui)
    {
        _ui = ui;
        foreach (var section in _sections)
        {
            _page.Remove(section.Heading);
            foreach (var (group, _) in section.Groups)
                _page.Remove(group);
        }
        _sections.Clear();

        if (_attention is not null)
            _page.Remove(_attention);
        _attention = null;
        var attention = AttentionFinder.Find(ui.Snapshot);
        if (attention.Count > 0)
        {
            _attention = Adw.PreferencesGroup.New();
            var row = Widgets.Navigation(
                T.F(attention.Count == 1 ? "{0} thing is worth a look" : "{0} things are worth a look", attention.Count),
                T._("Apps installed twice, menu entries that lead nowhere, or data left behind"),
                () => ui.Push(AttentionPage.Create(ui, attention)));
            row.AddPrefix(Gtk.Image.NewFromIconName("dialog-information-symbolic"));
            _attention.Add(row);
            _page.Add(_attention);
        }

        RebuildCategoryChoices(ui.Snapshot.Apps);

        var fromSources = ui.Snapshot.Apps.Where(app => !app.IsSelfInstalled)
            .GroupBy(ui.Presentation.OriginName)
            .OrderBy(origin => origin.Min(ui.Presentation.OriginRank))
            .ThenBy(origin => origin.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(origin => (origin.Key, Apps: origin.ToList()))
            .ToList();
        AddSection(ui, selfInstalled: false, T._("From sources"),
            T._("These apps came from a source: a store or collection that also delivers their updates. " +
                "Each group below is one source."),
            fromSources);

        var selfInstalled = ui.Snapshot.Apps.Where(app => app.IsSelfInstalled).ToList();
        AddSection(ui, selfInstalled: true, T._("Installed myself"),
            T._("These are apps you set up yourself, outside of any store: downloads, AppImages, game shortcuts. " +
                "Nobody updates them for you."),
            selfInstalled.Count == 0 ? [] : [(T._("Not from any source"), selfInstalled)]);

        ApplyFilter();
    }

    private void AddSection(
        Ui ui, bool selfInstalled, string title, string help, List<(string Title, List<AppInfo> Apps)> groups)
    {
        if (groups.Count == 0)
            return;

        var label = Gtk.Label.New(title);
        label.AddCssClass("title-2");
        label.Xalign = 0;
        label.Hexpand = true;
        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        header.Append(label);
        header.Append(Widgets.HelpButton(help));
        var heading = Adw.PreferencesGroup.New();
        heading.Add(header);
        _page.Add(heading);

        var section = new Section(selfInstalled, heading, []);
        foreach (var (groupTitle, apps) in groups)
        {
            var group = Widgets.Group(groupTitle, T.F(apps.Count == 1 ? "{0} app" : "{0} apps", apps.Count));
            var rows = new List<(Adw.ActionRow, AppInfo)>();
            foreach (var app in apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = Widgets.AppRow(ui, app);
                group.Add(row);
                rows.Add((row, app));
            }
            _page.Add(group);
            section.Groups.Add((group, rows));
        }
        _sections.Add(section);
    }

    // The drop-down offers only categories that some installed app actually has.
    private void RebuildCategoryChoices(IReadOnlyList<AppInfo> apps)
    {
        if (_categoryDropDown is not null)
            _filters.Remove(_categoryDropDown);

        _categoryChoices =
        [
            null,
            .. apps.SelectMany(app => app.Categories).Distinct()
                .OrderBy(c => c == AppCategory.Other)
                .ThenBy(Presentation.CategoryName, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => (AppCategory?)c),
        ];
        if (!_categoryChoices.Contains(_category))
            _category = null;

        var dropDown = Gtk.DropDown.NewFromStrings(_categoryChoices
            .Select(c => c is { } category ? Presentation.CategoryName(category) : T._("All categories"))
            .ToArray());
        dropDown.Selected = (uint)_categoryChoices.IndexOf(_category);
        dropDown.TooltipText = T._("Show only one kind of app");
        dropDown.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() != "selected" || dropDown.Selected >= _categoryChoices.Count)
                return;
            _category = _categoryChoices[(int)dropDown.Selected];
            ApplyFilter();
        };
        _categoryDropDown = dropDown;
        _filters.Append(dropDown);
    }

    private void ApplyFilter()
    {
        var query = _search.GetText().Trim();
        if (query.Length > 0 && _ui is not null)
        {
            ShowResults(_ui, query);
            return;
        }

        var anyVisible = false;
        foreach (var section in _sections)
        {
            var sectionWanted = _origin == Origin.All || (_origin == Origin.SelfInstalled) == section.SelfInstalled;
            var sectionVisible = false;
            foreach (var (group, rows) in section.Groups)
            {
                var groupVisible = false;
                foreach (var (row, app) in rows)
                {
                    row.Visible = sectionWanted && PassesFilters(app);
                    groupVisible |= row.Visible;
                }
                group.Visible = groupVisible;
                sectionVisible |= groupVisible;
            }
            section.Heading.Visible = sectionVisible;
            anyVisible |= sectionVisible;
        }

        // The note about things worth a look belongs to the full list, not to a filtered one.
        if (_attention is not null)
            _attention.Visible = _origin == Origin.All && _category is null;
        _stack.VisibleChildName = anyVisible ? "list" : _sections.Count == 0 ? "nothing-installed" : "nothing-found";
    }

    private bool PassesFilters(AppInfo app) =>
        (_origin == Origin.All || (_origin == Origin.SelfInstalled) == app.IsSelfInstalled) &&
        (_category is not { } category || app.Categories.Contains(category));

    // While searching, the grouping by source gives way to "how did I find this": by word or by tag.
    private void ShowResults(Ui ui, string query)
    {
        foreach (var group in _resultGroups)
            _results.Remove(group);
        _resultGroups.Clear();

        var matches = ui.Snapshot.Apps
            .Where(PassesFilters)
            .Select(app => (App: app, Match: AppSearch.Match(app, query, Presentation.CategoryName)))
            .Where(found => found.Match.Kind != SearchMatchKind.None)
            .OrderBy(found => found.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (matches.Count == 0)
        {
            _stack.VisibleChildName = "nothing-found";
            return;
        }

        var byWord = matches.Where(found => found.Match.Kind == SearchMatchKind.Word).ToList();
        if (byWord.Count > 0)
        {
            var group = Widgets.Group(
                T._("Found by name"),
                T.F(byWord.Count == 1
                    ? "{0} app has \u201c{1}\u201d in its name or description"
                    : "{0} apps have \u201c{1}\u201d in their name or description", byWord.Count, query));
            foreach (var (app, _) in byWord)
                group.Add(Widgets.AppRow(ui, app));
            AddResultGroup(group);
        }

        // One group per tag: a short search such as "s" + "e" can fit both Settings and Science.
        foreach (var tag in matches.Where(found => found.Match.Kind == SearchMatchKind.Tag)
            .GroupBy(found => found.Match.Category!.Value)
            .OrderBy(tag => Presentation.CategoryName(tag.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var name = Presentation.CategoryName(tag.Key);
            var group = Widgets.Group(
                T.F("Found by tag: {0}", name),
                T.F(tag.Count() == 1 ? "{0} app is tagged {1}" : "{0} apps are tagged {1}", tag.Count(), name),
                T._("These apps do not have your search words in their name. I am showing them because " +
                    "their tag, the kind of app they are, matches what you typed."));
            foreach (var (app, _) in tag)
                group.Add(Widgets.AppRow(ui, app, matchedTag: tag.Key));
            AddResultGroup(group);
        }

        _stack.VisibleChildName = "results";
    }

    private void AddResultGroup(Adw.PreferencesGroup group)
    {
        _results.Add(group);
        _resultGroups.Add(group);
    }

    private sealed record Section(
        bool SelfInstalled,
        Adw.PreferencesGroup Heading,
        List<(Adw.PreferencesGroup Group, List<(Adw.ActionRow Row, AppInfo App)> Rows)> Groups);
}
