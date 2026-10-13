using Yar.Core;

namespace Yar.App;

/// <summary>
/// "Open Source": new source-code projects that are gathering interest, by programming language.
/// A place to discover, not to install; a card opens the project in the web browser.
/// </summary>
internal sealed class OpenSourcePage
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.FlowBox _languages = Gtk.FlowBox.New();
    private readonly Gtk.DropDown _period = Gtk.DropDown.NewFromStrings(
        [T._("Started this week"), T._("Started this month"), T._("Started this year")]);
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Adw.StatusPage _status = Adw.StatusPage.New();
    private readonly Gtk.FlowBox _grid = Gtk.FlowBox.New();

    private Ui? _ui;
    private bool _built;
    private int _request;

    public OpenSourcePage()
    {
        _languages.SelectionMode = Gtk.SelectionMode.None;
        _languages.MaxChildrenPerLine = 20;
        _languages.ColumnSpacing = 6;
        _languages.RowSpacing = 6;
        _languages.Hexpand = true;

        _period.Selected = 1;
        _period.Valign = Gtk.Align.Start;
        _period.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "selected")
                Load();
        };

        var controls = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        controls.MarginTop = 12;
        controls.MarginBottom = 6;
        controls.MarginStart = 24;
        controls.MarginEnd = 24;
        controls.Append(_languages);
        controls.Append(_period);

        _grid.SelectionMode = Gtk.SelectionMode.None;
        _grid.Homogeneous = true;
        _grid.MaxChildrenPerLine = 8;
        _grid.MinChildrenPerLine = 1;
        _grid.ColumnSpacing = 12;
        _grid.RowSpacing = 12;
        _grid.Valign = Gtk.Align.Start;
        _grid.MarginTop = 6;
        _grid.MarginBottom = 24;
        _grid.MarginStart = 24;
        _grid.MarginEnd = 24;
        var scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scrolled.Child = _grid;

        _stack.AddNamed(_status, "status");
        _stack.AddNamed(scrolled, "list");
        _stack.Vexpand = true;
        _root.Append(controls);
        _root.Append(_stack);
    }

    public Gtk.Widget Widget => _root;

    public void Show(Ui ui)
    {
        _ui = ui;
        if (!ui.OpenSourceEnabled)
        {
            ShowStatus(T._("This tab is switched off"), T._("You can switch it on in the settings."), "preferences-system-symbolic");
            return;
        }

        if (!_built)
        {
            _built = true;
            var chosen = ui.Settings.DeveloperLanguages ?? [];
            foreach (var (name, _) in DevLanguages.All)
            {
                var toggle = Gtk.ToggleButton.NewWithLabel(name);
                toggle.Active = chosen.Contains(name);
                toggle.OnToggled += (_, _) =>
                {
                    var languages = ui.Settings.DeveloperLanguages ?? [];
                    languages.Remove(name);
                    if (toggle.Active)
                        languages.Add(name);
                    ui.Settings.DeveloperLanguages = languages;
                    ui.Settings.Save();
                    Load();
                };
                _languages.Append(toggle);
            }
        }
        Load();
    }

    // async void: started by the page; every outcome is shown on it.
    private async void Load()
    {
        if (_ui is not { OpenSourceEnabled: true } ui)
            return;

        var languages = (ui.Settings.DeveloperLanguages ?? []).ToList();
        if (languages.Count == 0)
        {
            ShowStatus(
                T._("Pick a programming language"),
                T._("Choose one or more above, and I will show new projects in them that many people starred on GitHub."),
                "applications-development-symbolic");
            return;
        }

        // Only the answer to the latest question is shown, however the answers arrive.
        var request = ++_request;
        var period = _period.Selected switch { 0 => FeedPeriod.Week, 2 => FeedPeriod.Year, _ => FeedPeriod.Month };
        ShowStatus(T._("Asking GitHub…"), null, "content-loading-symbolic");

        IReadOnlyList<DevProject> projects;
        try
        {
            projects = await ui.Host.Developer.GetTrendingAsync(languages, period, CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            projects = [];
        }
        if (request != _request)
            return;

        if (projects.Count == 0)
        {
            ShowStatus(
                T._("Nothing to show right now"),
                T._("Either GitHub could not be reached, or it asked me to slow down. It allows only a few " +
                    "questions a minute; try again in a little while."),
                "network-offline-symbolic");
            return;
        }

        _grid.RemoveAll();
        foreach (var project in projects)
            _grid.Append(StoreCards.Repo(ui, project));
        _stack.VisibleChildName = "list";
    }

    private void ShowStatus(string title, string? description, string icon)
    {
        _status.Title = title;
        _status.Description = description;
        _status.IconName = icon;
        _stack.VisibleChildName = "status";
    }
}
