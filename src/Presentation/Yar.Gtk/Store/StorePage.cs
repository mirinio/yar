using Yar.Core;

namespace Yar.App;

/// <summary>"Store": apps you could install, from every catalog on this computer. Shows; does not install yet.</summary>
internal sealed class StorePage
{
    private const int MaxResults = 60;
    private const int MaxInCategory = 150;

    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.SearchEntry _search = Gtk.SearchEntry.New();
    private readonly Gtk.CheckButton _includeInstalled = Gtk.CheckButton.NewWithLabel(T._("Also show apps I already have"));
    private readonly Gtk.Button _categoryChip = Gtk.Button.New();
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Adw.StatusPage _status = Adw.StatusPage.New();
    private readonly Adw.PreferencesPage _results = Adw.PreferencesPage.New();
    private readonly Adw.Bin _home = Adw.Bin.New();
    private readonly List<Adw.PreferencesGroup> _groups = [];

    private Ui? _ui;
    private StoreData? _data;
    private AppCategory? _category;
    private bool _loading;
    private bool _onlineRequested;
    private bool _reloadWanted;
    private string _sourcesKey = "";
    private string _onlineKey = "";

    public StorePage()
    {
        _search.PlaceholderText = T._("Search for apps to install");
        _search.Hexpand = true;
        _search.OnSearchChanged += (_, _) => Fill();
        _includeInstalled.OnToggled += (_, _) => Fill();

        _categoryChip.AddCssClass("pill");
        _categoryChip.Visible = false;
        _categoryChip.TooltipText = T._("Show all kinds of apps again");
        _categoryChip.OnClicked += (_, _) => ChooseCategory(null);

        var options = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        options.Append(_categoryChip);
        options.Append(_includeInstalled);
        var controls = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        controls.Append(_search);
        controls.Append(options);
        controls.MarginTop = 12;
        controls.MarginBottom = 6;
        controls.MarginStart = 24;
        controls.MarginEnd = 24;

        _stack.AddNamed(_status, "status");
        _stack.AddNamed(_home, "home");
        _stack.AddNamed(_results, "results");
        _stack.Vexpand = true;
        _root.Append(controls);
        _root.Append(_stack);
    }

    public Gtk.Widget Widget => _root;

    public void Show(Ui ui)
    {
        _ui = ui;

        // Switching online data changes what may be fetched and shown.
        var onlineKey = $"{ui.OnlineAllowed}";
        if (onlineKey != _onlineKey)
        {
            _onlineKey = onlineKey;
            _onlineRequested = false;
            if (_data is not null && !ui.OnlineAllowed)
            {
                _data.Collections = StoreCollections.Empty;
                _data.Ratings = null;
            }
        }

        // A source that was added, removed or switched brings or takes away catalogs: read them again.
        var sourcesKey = string.Join('|', ui.Snapshot.Sources.Select(s => $"{s.ProviderId}:{s.Id}:{s.Enabled}"));
        if (_data is null || sourcesKey != _sourcesKey)
        {
            _sourcesKey = sourcesKey;
            Load();
            return;
        }
        LoadOnline();
        Fill();
    }

    // async void: started from the page itself; every failure ends up on screen.
    private async void Load()
    {
        if (_ui is null)
            return;
        if (_loading)
        {
            // Sources changed again while the catalogs were being read: read them once more afterwards.
            _reloadWanted = true;
            return;
        }
        _loading = true;
        if (_data is null)
            ShowStatus(T._("Reading the app catalogs…"), null, "system-search-symbolic");

        try
        {
            var entries = await _ui.Host.Store.ReadAsync(CancellationToken.None);
            var previous = _data;
            _data = new StoreData(await Task.Run(() => StoreCatalog.Merge(entries)))
            {
                Collections = previous?.Collections ?? StoreCollections.Empty,
                Ratings = previous?.Ratings,
            };
        }
        catch (Exception e)
        {
            ShowStatus(T._("Hmm, I couldn't read the app catalogs"), Widgets.Escape(e.Message), "dialog-warning-symbolic");
            return;
        }
        finally
        {
            _loading = false;
        }

        if (_reloadWanted)
        {
            _reloadWanted = false;
            Load();
            return;
        }
        LoadOnline();
        Fill();
    }

    // Everything from the internet is an extra: the Store works without it, and quietly if it fails.
    private async void LoadOnline()
    {
        if (_onlineRequested || _data is null || _ui is not { OnlineAllowed: true } ui)
            return;
        _onlineRequested = true;

        // Both are asked for together and the page is redrawn once, so the banner does not jump
        // back to its first card twice while someone is already looking at it.
        var collections = Quietly(() => ui.Host.Online.GetCollectionsAsync(CancellationToken.None));
        var ratings = Quietly(() => ui.Host.Online.GetRatingsAsync(CancellationToken.None));
        var (lists, stars) = (await collections, await ratings);

        // The catalogs may have been read again in the meantime; the answers belong to whatever is shown now.
        if (_data is not { } data || _ui is not { OnlineAllowed: true })
            return;
        if (lists is not null)
            data.Collections = lists;
        if (stars is not null)
            data.Ratings = stars;
        Fill();
    }

    private static async Task<T?> Quietly<T>(Func<Task<T>> fetch)
        where T : class
    {
        try
        {
            return await fetch();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void ChooseCategory(AppCategory? category)
    {
        _category = category;
        _categoryChip.Visible = category is not null;
        if (category is { } chosen)
            _categoryChip.Label = $"{Presentation.CategoryName(chosen)}  ✕";
        Fill();
    }

    private void Fill()
    {
        if (_ui is not { } ui || _data is not { } data)
            return;

        if (data.Apps.Count == 0)
        {
            ShowStatus(
                T._("No app catalogs found"),
                T._("I list apps from the catalogs your sources keep on this computer, and I couldn't find any."),
                "system-search-symbolic");
            return;
        }

        var query = _search.GetText().Trim();
        var showInstalled = _includeInstalled.Active;
        bool Wanted(StoreApp app) => showInstalled || StoreCatalog.InstalledMatch(app, ui.Snapshot.Apps) is null;

        if (query.Length > 0)
        {
            ShowResults(ui, data, query, Wanted, showInstalled);
        }
        else if (_category is { } category)
        {
            var all = StoreCatalog.InCategory(data.Apps, category, data.InstallsOf).Where(Wanted).ToList();
            _home.Child = StoreHome.CreateCategory(ui, data, category, all.Take(MaxInCategory).ToList(), all.Count);
            _stack.VisibleChildName = "home";
        }
        else
        {
            _home.Child = StoreHome.Create(ui, data, Wanted, chosen => ChooseCategory(chosen));
            _stack.VisibleChildName = "home";
        }
    }

    private void ShowResults(Ui ui, StoreData data, string query, Func<StoreApp, bool> wanted, bool showInstalled)
    {
        foreach (var group in _groups)
            _results.Remove(group);
        _groups.Clear();

        var hits = StoreCatalog.Search(data.Apps, query, Presentation.CategoryName, int.MaxValue)
            .Where(app => _category is not { } category || app.Categories.Contains(category))
            .Where(wanted).Take(MaxResults).ToList();
        if (hits.Count == 0)
        {
            ShowStatus(
                T._("Nothing found"),
                showInstalled
                    ? T._("None of your sources has an app that matches.")
                    : T._("None of your sources has an app that matches and that you don't have yet."),
                "edit-find-symbolic");
            return;
        }

        var found = Widgets.Group(
            T.F(hits.Count == 1 ? "{0} app found" : "{0} apps found", hits.Count),
            hits.Count == MaxResults ? T._("Showing the best matches. Type more to narrow it down.") : null);
        foreach (var app in hits)
            found.Add(Row(ui, data, app));
        _results.Add(found);
        _groups.Add(found);
        _stack.VisibleChildName = "results";
    }

    private static Adw.ActionRow Row(Ui ui, StoreData data, StoreApp app)
    {
        var tags = new List<Gtk.Widget>();
        if (StoreCatalog.InstalledMatch(app, ui.Snapshot.Apps) is not null)
            tags.Add(Widgets.Tag(T._("Installed")));
        if (data.RatingOf(app) is { Average: { } average })
            tags.Add(Widgets.Tag($"★ {average:0.0}"));
        if (data.InstallsOf(app) is { } installs)
            tags.Add(Widgets.Tag(T.F("{0} installs", CompactNumber.Format(installs))));
        if (app.Offers.Count > 1)
            tags.Add(Widgets.Tag(T.F("{0} sources", app.Offers.Count)));

        var row = Widgets.Navigation(
            app.Name, app.Summary, () => ui.Push(StoreAppPage.Create(ui, data, app)), [.. tags]);
        row.SubtitleLines = 1;
        row.AddPrefix(Widgets.Icon(app.IconPath, app.IconName, 32));
        return row;
    }

    private void ShowStatus(string title, string? description, string icon)
    {
        _status.Title = title;
        _status.Description = description;
        _status.IconName = icon;
        _stack.VisibleChildName = "status";
    }
}
