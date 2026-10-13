using Yar.Core;
using Yar.Hosting;

namespace Yar.App;

internal sealed class MainWindow
{
    private readonly YarHost _host;
    private readonly Settings _settings;
    private readonly Adw.ApplicationWindow _window;
    private readonly Adw.ToastOverlay _toasts = Adw.ToastOverlay.New();
    private readonly Gtk.Stack _mode = Gtk.Stack.New();
    private readonly Gtk.Stack _sectionStack = Gtk.Stack.New();
    private readonly Adw.StatusPage _status = Adw.StatusPage.New();
    private readonly Gtk.Button _refresh = Gtk.Button.NewFromIconName("view-refresh-symbolic");
    private readonly Gtk.Label _updateCount = Gtk.Label.New("");
    private readonly AppsPage _apps = new();
    private readonly SourcesPage _sources = new();
    private readonly StorePage _store = new();
    private readonly OpenSourcePage _openSource = new();
    private readonly StoragePage _storage = new();
    private readonly Adw.NavigationSplitView _split = Adw.NavigationSplitView.New();
    private readonly Gtk.ListBox _sidebar = Gtk.ListBox.New();
    private readonly UpdatesPage _updates = new();
    private readonly HistoryPage _history = new();
    private Ui? _ui;
    private readonly Section[] _sections;

    private int _section;
    private bool _loading;
    private ChangeKinds _again;
    private readonly SynchronizationContext? _gtkContext = SynchronizationContext.Current;
    private readonly Gtk.Spinner _busy = Gtk.Spinner.New();
    private CatalogSnapshot? _snapshot;
    private Presentation? _presentation;

    public MainWindow(Adw.Application application, YarHost host)
    {
        _host = host;
        _settings = Settings.Load(host.Dirs.ConfigHome);
        Widgets.LoadCss();

        _window = Adw.ApplicationWindow.New(application);
        _window.Title = "Yar";
        // The icon installed with the menu entry; the desktop also matches the window to that entry by this ID.
        _window.IconName = YarHost.AppId;
        _window.SetDefaultSize(1040, 720);

        _sections =
        [
            CreateSection("apps", T._("My Apps"), "view-grid-symbolic", _apps.Widget),
            CreateSection("store", T._("Store"), "system-software-install-symbolic", _store.Widget),
            CreateSection("sources", T._("Sources"), "folder-download-symbolic", _sources.Widget),
            CreateSection("updates", T._("Updates"), "software-update-available-symbolic", _updates.Widget),
            CreateSection("storage", T._("Storage"), "drive-harddisk-symbolic", _storage.Widget),
            CreateSection("history", T._("History"), "document-open-recent-symbolic", _history.Widget),
            CreateSection("opensource", T._("Open Source"), "applications-development-symbolic", _openSource.Widget),
        ];
        foreach (var section in _sections)
            _sectionStack.AddNamed(section.Navigation, section.Name);

        var statusToolbar = Adw.ToolbarView.New();
        statusToolbar.AddTopBar(Adw.HeaderBar.New());
        statusToolbar.Content = _status;
        _mode.AddNamed(statusToolbar, "status");
        _mode.AddNamed(_sectionStack, "main");
        _toasts.Child = _mode;

        _split.Sidebar = Adw.NavigationPage.New(CreateSidebar(), "Yar");
        // The strip that shows a running change sits under every page, so it is never out of sight.
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        _toasts.Vexpand = true;
        content.Append(_toasts);
        content.Append(new OperationBar(host.Operations, OnGtkThread).Widget);
        _split.Content = Adw.NavigationPage.New(content, "Yar");
        _window.Content = _split;

        // On a narrow window the sidebar folds away and the content takes the whole width;
        // libadwaita then puts a back button in the header that leads to the sidebar.
        var narrow = Adw.Breakpoint.New(Adw.BreakpointCondition.Parse("max-width: 720sp"));
        narrow.AddSetter(_split, "collapsed", new GObject.Value(true));
        _window.AddBreakpoint(narrow);

        // Closing in the middle of a change would leave it half done.
        _window.OnCloseRequest += (_, _) =>
        {
            if (_host.Operations.Current is not { } running)
                return false;
            Widgets.Confirm(
                _window, T._("Stop and close?"),
                T.F("I am in the middle of this: {0}. Closing now stops it, and it may be left half done.",
                    ChangeText.Running(running.Record)),
                T._("Stop and close"), destructive: true, () =>
                {
                    _host.Operations.CancelCurrent();
                    _window.Destroy();
                });
            return true;
        };
        // A change that ended belongs in the history at once, whether or not it altered the list of apps.
        _host.Operations.Finished += _ => OnGtkThread(() =>
        {
            if (_ui is not null)
                _history.Show(_ui);
        });

        StartUp();
    }

    public void Present() => _window.Present();

    // Each section has its own page history, so going "back" never jumps between sections.
    private static Section CreateSection(string name, string title, string icon, Gtk.Widget content)
    {
        var banner = Adw.Banner.New("");
        var root = Widgets.Page(title, content, banner);
        var navigation = Adw.NavigationView.New();
        navigation.Add(root);
        return new Section(name, title, icon, navigation, root, banner);
    }

    private Adw.ToolbarView CreateSidebar()
    {
        var list = _sidebar;
        list.AddCssClass("navigation-sidebar");
        foreach (var section in _sections)
        {
            var label = Gtk.Label.New(section.Title);
            label.Xalign = 0;
            label.Hexpand = true;
            var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            box.Append(Gtk.Image.NewFromIconName(section.Icon));
            box.Append(label);
            if (section.Name == "updates")
            {
                _updateCount.AddCssClass("yar-tag");
                _updateCount.Valign = Gtk.Align.Center;
                _updateCount.Visible = false;
                box.Append(_updateCount);
            }
            list.Append(box);
        }
        list.OnRowSelected += (_, args) =>
        {
            if (args.Row is null)
                return;
            _section = args.Row.GetIndex();
            // Choosing a section in the sidebar always lands on its first page.
            _sections[_section].Navigation.PopToPage(_sections[_section].Root);
            _sectionStack.VisibleChildName = _sections[_section].Name;
            _split.ShowContent = true;
            // Measuring disks takes a few seconds, so it only happens while that tab is being looked at.
            if (_sections[_section].Name == "storage")
                _storage.Activate();
            else
                _storage.Deactivate();
        };
        list.SelectRow(list.GetRowAtIndex(0));

        _refresh.TooltipText = T._("Look again");
        _refresh.OnClicked += (_, _) => Refresh(ChangeKinds.All, announce: false);

        var settings = Gtk.Button.NewFromIconName("emblem-system-symbolic");
        settings.TooltipText = T._("Settings");
        settings.OnClicked += (_, _) => ShowSettings();

        _busy.Visible = false;
        _busy.TooltipText = T._("Checking for changes…");

        var header = Adw.HeaderBar.New();
        header.PackStart(_refresh);
        header.PackStart(_busy);
        header.PackEnd(settings);

        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(header);
        toolbar.Content = list;
        return toolbar;
    }

    // Shows the saved list at once if there is one, then checks it against reality in the background.
    private void StartUp()
    {
        if (_host.Catalog.LoadCached() is { } saved)
            Apply(saved);
        else
            ShowStatus(T._("Looking at your apps…"), description: null, busy: true);

        Refresh(ChangeKinds.All, announce: false);

        _host.Changes.RefreshNeeded += kinds => OnGtkThread(() => Refresh(kinds, announce: true));
        _host.Changes.Start();
    }

    // async void: this is an event-style entry point, and every failure is shown to the user.
    private async void Refresh(ChangeKinds kinds, bool announce)
    {
        if (_loading)
        {
            // Something changed while a check was running: check once more when it is done.
            _again |= kinds;
            return;
        }
        _loading = true;
        _refresh.Sensitive = false;
        _busy.Visible = true;
        _busy.Spinning = true;

        try
        {
            CatalogSnapshot fresh;
            if (_snapshot is not null && kinds == ChangeKinds.Updates)
            {
                fresh = await _host.Catalog.LoadUpdatesAsync(_snapshot, CancellationToken.None);
            }
            else
            {
                var apps = await _host.Catalog.LoadAppsAsync(CancellationToken.None);
                // With nothing on screen yet, show the apps now; update badges follow a moment later.
                if (_snapshot is null)
                    Apply(apps);
                fresh = await _host.Catalog.LoadUpdatesAsync(apps, CancellationToken.None);
            }

            if (_snapshot is null || !SnapshotCache.SameContent(_snapshot, fresh))
            {
                var wasBrowsing = _sections.Any(section => section.Navigation.GetVisiblePage() != section.Root);
                Apply(fresh);
                // Applying returns every section to its first page; say why if that moved the user.
                if (announce || wasBrowsing)
                    _toasts.AddToast(Adw.Toast.New(T._("I found changes and updated the list.")));
            }
        }
        catch (Exception e)
        {
            if (_snapshot is null)
                ShowStatus(T._("Hmm, I couldn't look at your apps"), Widgets.Escape(e.Message), busy: false);
            else
                _toasts.AddToast(Adw.Toast.New(T._("Hmm, I couldn't check for changes.")));
        }
        finally
        {
            _loading = false;
            _refresh.Sensitive = true;
            _busy.Spinning = false;
            _busy.Visible = false;
        }

        if (_again != ChangeKinds.None)
        {
            var next = _again;
            _again = ChangeKinds.None;
            Refresh(next, announce);
        }
    }

    private void ShowSettings() => SettingsDialog.Present(
        _window, _host, _settings, _snapshot,
        changed: Render,
        refresh: () => Refresh(ChangeKinds.All, announce: false));

    private void Apply(CatalogSnapshot snapshot)
    {
        _snapshot = snapshot;
        _presentation = new Presentation(snapshot, _host.Os);
        Render();
    }

    private void ShowStatus(string title, string? description, bool busy)
    {
        Gtk.Spinner? spinner = null;
        if (busy)
        {
            spinner = Gtk.Spinner.New();
            spinner.Spinning = true;
            spinner.SetSizeRequest(32, 32);
            spinner.Halign = Gtk.Align.Center;
        }
        _status.IconName = busy ? null : "dialog-warning-symbolic";
        _status.Title = title;
        _status.Description = description;
        _status.Child = spinner;
        _mode.VisibleChildName = "status";
    }

    // Change notices arrive on other threads; widgets may only be touched on GTK's own.
    private void OnGtkThread(Action action)
    {
        if (_gtkContext is not null)
        {
            _gtkContext.Post(_ => action(), null);
            return;
        }
        GLib.Functions.IdleAdd(0, () =>
        {
            action();
            return false;
        });
    }

    private void Render()
    {
        if (_snapshot is null || _presentation is null)
            return;

        // Pages that were pushed show the old data, so every section goes back to its first page.
        foreach (var section in _sections)
            section.Navigation.PopToPage(section.Root);

        var ui = new Ui(
            _window, _host, _settings, _snapshot, _presentation,
            push: page => _sections[_section].Navigation.Push(page),
            toast: text => _toasts.AddToast(Adw.Toast.New(text)),
            refresh: () => Refresh(ChangeKinds.All, announce: false),
            openSettings: ShowSettings);
        _ui = ui;
        _history.Show(ui);
        _apps.Show(ui);
        _store.Show(ui);
        _sources.Show(ui);
        _updates.Show(ui);
        _openSource.Show(ui);
        _storage.Show(ui);

        // The Open Source tab is only in the sidebar while it is switched on in the settings.
        var openSourceIndex = Array.FindIndex(_sections, section => section.Name == "opensource");
        if (_sidebar.GetRowAtIndex(openSourceIndex) is { } openSourceRow)
        {
            openSourceRow.Visible = ui.OpenSourceEnabled;
            if (!ui.OpenSourceEnabled && _section == openSourceIndex)
                _sidebar.SelectRow(_sidebar.GetRowAtIndex(0));
        }

        _updateCount.SetText(_snapshot.Updates.Count.ToString(System.Globalization.CultureInfo.CurrentCulture));
        _updateCount.Visible = _snapshot.Updates.Count > 0;

        var failed = _snapshot.Providers
            .Where(report => report.Status.Availability == ProviderAvailability.Error)
            .ToList();
        var message = failed.Count == 0
            ? ""
            : T.F("I couldn't read everything ({0}), so some apps may be missing.",
                string.Join(", ", failed.Select(report => _presentation.ProviderTitle(report.ProviderId))));
        foreach (var section in _sections)
        {
            section.Banner.Title = message;
            section.Banner.Revealed = failed.Count > 0;
        }

        _mode.VisibleChildName = "main";
    }

    private sealed record Section(
        string Name, string Title, string Icon, Adw.NavigationView Navigation, Adw.NavigationPage Root, Adw.Banner Banner);
}
