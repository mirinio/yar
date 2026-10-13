using System.Diagnostics;
using Yar.Core;

namespace Yar.App;

/// <summary>
/// "Storage": how full the disks are and what apps have to do with it. It measures and shows;
/// removing anything comes with uninstall.
/// </summary>
internal sealed class StoragePage
{
    private const int MaxRanked = 15;

    // Programs that can show what is inside any folder, in the order they are tried.
    private static readonly (string Path, string Name)[] Analyzers =
    [
        ("/usr/bin/filelight", "Filelight"),
        ("/usr/bin/baobab", "Disk Usage Analyzer"),
    ];

    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Adw.StatusPage _status = Adw.StatusPage.New();
    private readonly Adw.Bin _content = Adw.Bin.New();

    private Ui? _ui;
    private CatalogSnapshot? _measured;
    private bool _measuring;
    private bool _visible;

    public StoragePage()
    {
        _stack.AddNamed(_status, "status");
        _stack.AddNamed(_content, "content");
        _stack.Vexpand = true;
        ShowStatus(T._("Open this tab to measure"), null, busy: false);
    }

    public Gtk.Widget Widget => _stack;

    public void Show(Ui ui)
    {
        _ui = ui;
        if (_visible)
            Measure();
    }

    /// <summary>Called when the tab is chosen. Measuring takes a few seconds, so it only happens when someone looks.</summary>
    public void Activate()
    {
        _visible = true;
        Measure();
    }

    public void Deactivate() => _visible = false;

    // async void: started by the page; every outcome is shown on it.
    private async void Measure()
    {
        if (_ui is not { } ui || _measuring || ReferenceEquals(_measured, ui.Snapshot))
            return;
        _measuring = true;
        if (_measured is null)
            ShowStatus(T._("Measuring your disks and apps…"), T._("This takes a few seconds."), busy: true);

        try
        {
            var snapshot = ui.Snapshot;
            var overview = await ui.Host.Storage.AnalyzeAsync(snapshot, CancellationToken.None);
            _measured = snapshot;
            _content.Child = Build(ui, overview);
            _stack.VisibleChildName = "content";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ShowStatus(T._("Hmm, I couldn't measure"), Widgets.Escape(e.Message), busy: false);
        }
        finally
        {
            _measuring = false;
        }

        // The app list changed while measuring: measure once more for the new one.
        if (_visible && _ui is { } latest && !ReferenceEquals(_measured, latest.Snapshot))
            Measure();
    }

    private static Adw.PreferencesPage Build(Ui ui, StorageOverview overview)
    {
        var page = Adw.PreferencesPage.New();
        AddDisks(page, ui, overview);
        AddApps(page, ui, overview);
        AddTidy(page, ui, overview);
        return page;
    }

    private static void AddDisks(Adw.PreferencesPage page, Ui ui, StorageOverview overview)
    {
        var inUse = Widgets.Group(
            T._("Your disks"),
            help: T._("A disk is divided into parts, called partitions. Linux uses some of them: one for the system and " +
                "your files, and small ones it needs to start up. The bar shows how full each one is."));
        var unused = Widgets.Group(
            T._("Parts of your disks Linux is not using"),
            help: T._("These partitions exist but are not opened by Linux right now. Often they belong to Windows on " +
                "the same computer, or are a spare. I do not touch them."));

        foreach (var disk in overview.Disks)
        {
            foreach (var partition in disk.Partitions)
            {
                var startUp = partition.MountPoints.All(m => m.StartsWith("/boot", StringComparison.Ordinal));
                if (partition.InUse && partition is { UsedBytes: { } used, FreeBytes: { } free })
                {
                    // The small start-up partitions only matter to people who ask for technical details.
                    if (startUp && !ui.Technical)
                        continue;
                    var row = Adw.ActionRow.New();
                    row.UseMarkup = false;
                    row.Title = PartitionTitle(ui, partition);
                    row.Subtitle = T.F("{0} free of {1}", Presentation.FormatSize(free), Presentation.FormatSize(used + free)) +
                        (ui.Technical ? $" · {partition.Name} · {partition.FileSystem} · {string.Join(", ", partition.MountPoints)}" : "");
                    if (partition.ReadOnly)
                        row.AddSuffix(Widgets.Tag(T._("Read-only")));
                    row.AddSuffix(Widgets.UsageBar(used, used + free));
                    inUse.Add(row);
                }
                else if (!partition.InUse && (partition.SizeBytes >= 100 * 1024 * 1024 || ui.Technical))
                {
                    var row = Adw.ActionRow.New();
                    row.UseMarkup = false;
                    row.Title = partition.FileSystem switch
                    {
                        "ntfs" => T._("Windows partition"),
                        "swap" => T._("Extra memory (swap)"),
                        null => T._("Partition without a file system"),
                        var other => T.F("Partition ({0})", other),
                    };
                    row.Subtitle = Presentation.FormatSize(partition.SizeBytes) +
                        (partition.Label is null ? "" : $" · {partition.Label}") +
                        (ui.Technical ? $" · {partition.Name}" : "");
                    unused.Add(row);
                }
            }

            if (disk.UnallocatedBytes > 0)
            {
                var row = Adw.ActionRow.New();
                row.UseMarkup = false;
                row.Title = T._("Space that belongs to no partition");
                row.Subtitle = T.F("{0} on {1}. It can be turned into a new partition or added to one, with a disk tool.",
                    Presentation.FormatSize(disk.UnallocatedBytes), disk.Model ?? disk.Name);
                unused.Add(row);
            }
        }

        if (overview.Disks.Count == 0)
            inUse.Description = Widgets.Escape(T._("I couldn't read which disks this computer has."));
        page.Add(inUse);
        if (overview.Disks.Any(d => d.UnallocatedBytes > 0 || d.Partitions.Any(p => !p.InUse && (p.SizeBytes >= 100 * 1024 * 1024 || ui.Technical))))
            page.Add(unused);
    }

    private static string PartitionTitle(Ui ui, PartitionInfo partition)
    {
        var system = partition.MountPoints.Contains("/");
        var home = partition.MountPoints.Any(m => m == "/home" || ui.Host.Dirs.Home.StartsWith(m.TrimEnd('/') + "/", StringComparison.Ordinal) && m != "/");
        return (system, home) switch
        {
            (true, true) => T._("The system and your files"),
            (true, false) => T._("The system"),
            (false, true) => T._("Your files"),
            _ when partition.MountPoints.All(m => m.StartsWith("/boot", StringComparison.Ordinal)) => T._("Start-up files"),
            _ => partition.Label ?? partition.MountPoints[0],
        };
    }

    private static void AddApps(Adw.PreferencesPage page, Ui ui, StorageOverview overview)
    {
        var known = overview.Items.Where(item => item.Kind != StorageKind.Other && item.Bytes > 0).ToList();
        var other = overview.Items.FirstOrDefault(item => item.Kind == StorageKind.Other);

        var group = Widgets.Group(
            T._("What your apps take"),
            T.F("{0} that I can tie to apps. Each block is one thing; the bigger the block, the more space it takes.",
                Presentation.FormatSize(overview.AccountedBytes)),
            T._("Blue blocks are Flatpak apps, green ones are system packages, purple ones are parts several Flatpak " +
                "apps share, orange is temporary files and red is data left behind by apps that are gone. An app's " +
                "block includes the data it keeps, where I know where that is. Click a block to open that app."));
        if (known.Count > 0)
        {
            var map = new TreemapView(height: 340);
            map.Show(known, item => Open(ui, item));
            group.Add(map.Widget);
        }
        page.Add(group);

        var ranked = Widgets.Group(T._("Biggest first"));
        var biggest = known.Count > 0 ? known.Max(item => item.Bytes) : 1;
        foreach (var item in known.Take(MaxRanked))
        {
            var row = Adw.ActionRow.New();
            row.UseMarkup = false;
            row.Title = item.Kind == StorageKind.Cache ? T._("Temporary files") : item.Title;
            var size = (item.AtLeast ? T._("at least ") : "") + Presentation.FormatSize(item.Bytes);
            row.Subtitle = item.DataBytes > 0
                ? T.F("{0} · {1} of it is its own data · {2}", size, Presentation.FormatSize(item.DataBytes), TreemapView.Legend(item.Kind, item.ProviderId))
                : $"{size} · {TreemapView.Legend(item.Kind, item.ProviderId)}";
            row.AddSuffix(Widgets.UsageBar(item.Bytes, biggest));
            if (AppOf(ui, item) is not null)
            {
                row.Activatable = true;
                row.AddSuffix(Gtk.Image.NewFromIconName("go-next-symbolic"));
                row.OnActivated += (_, _) => Open(ui, item);
            }
            ranked.Add(row);
        }
        if (known.Count > MaxRanked)
            ranked.Description = Widgets.Escape(T.F("The {0} biggest of {1}.", MaxRanked, known.Count));
        if (known.Count > 0)
            page.Add(ranked);

        if (other is not null)
        {
            var rest = Widgets.Group(
                T._("Everything else"),
                help: T._("Your documents, pictures, downloads and games, the system itself, and apps whose size I " +
                    "cannot measure. I only look at apps; a disk-usage tool shows the rest folder by folder."));
            var row = Adw.ActionRow.New();
            row.UseMarkup = false;
            row.Title = Presentation.FormatSize(other.Bytes);
            row.Subtitle = T._("Space in use that I cannot tie to an app");
            if (AnalyzerButton(ui, ui.Host.Dirs.Home) is { } button)
                row.AddSuffix(button);
            rest.Add(row);
            page.Add(rest);
        }
    }

    private static void AddTidy(Adw.PreferencesPage page, Ui ui, StorageOverview overview)
    {
        var tidy = overview.Items.Where(item => item.Kind is StorageKind.Leftover or StorageKind.Cache).ToList();
        if (tidy.Count == 0)
            return;

        var group = Widgets.Group(
            T._("Could be tidied"),
            T._("I can show these now. Removing them for you comes when I learn to uninstall."),
            T._("\"Left behind\" is the settings folder of a Flatpak app that is no longer installed; some are nearly " +
                "empty and were only ever created, never used. Temporary files are things apps keep to be faster " +
                "and can make again."));
        foreach (var item in tidy)
        {
            var title = item.Kind == StorageKind.Cache
                ? T._("Temporary files of all your apps")
                : T.F("Left behind by {0}", item.Title);
            var row = Widgets.PathRow(ui, title, item.Path ?? "");
            row.AddPrefix(Widgets.Tag((item.AtLeast ? "≥ " : "") + Presentation.FormatSize(item.Bytes)));
            if (item.Kind == StorageKind.Cache && item.Path is not null && AnalyzerButton(ui, item.Path) is { } button)
                row.AddSuffix(button);
            group.Add(row);
        }
        page.Add(group);
    }

    private static AppInfo? AppOf(Ui ui, StorageItem item) =>
        item is { Kind: StorageKind.App, AppId: not null }
            ? ui.Snapshot.Apps.FirstOrDefault(app => app.ProviderId == item.ProviderId && app.Id == item.AppId)
            : null;

    private static void Open(Ui ui, StorageItem item)
    {
        if (AppOf(ui, item) is { } app)
            ui.ShowApp(app);
    }

    // A button that opens a folder in a disk-usage tool, when one is installed.
    private static Gtk.Button? AnalyzerButton(Ui ui, string folder)
    {
        if (Analyzers.FirstOrDefault(a => File.Exists(a.Path)) is not { Path: not null } analyzer)
            return null;

        var button = Gtk.Button.NewWithLabel(T.F("Look inside with {0}", analyzer.Name));
        button.Valign = Gtk.Align.Center;
        button.OnClicked += (_, _) =>
        {
            try
            {
                var info = new ProcessStartInfo(analyzer.Path) { UseShellExecute = false };
                info.ArgumentList.Add(folder);
                using var process = Process.Start(info);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                ui.Toast(T._("Hmm, I couldn't open that."));
            }
        };
        return button;
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
        _status.IconName = busy ? null : "drive-harddisk-symbolic";
        _status.Title = title;
        _status.Description = description;
        _status.Child = spinner;
        _stack.VisibleChildName = "status";
    }
}
