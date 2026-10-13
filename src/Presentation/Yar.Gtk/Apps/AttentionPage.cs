using Yar.Core;

namespace Yar.App;

/// <summary>Things Yar noticed that you may want to tidy up. It explains; it does not remove anything yet.</summary>
internal static class AttentionPage
{
    public static Adw.NavigationPage Create(Ui ui, IReadOnlyList<AttentionItem> items)
    {
        var page = Adw.PreferencesPage.New();

        var intro = Widgets.Group(
            T._("Worth a look"),
            T._("Nothing here is urgent or broken beyond repair. I can show you these things now; removing them " +
                "for you comes when I learn to uninstall."));
        page.Add(intro);

        Add(page, ui, items, AttentionKind.Duplicate,
            T._("Installed twice"),
            T._("The same app from two sources. Both work, but they take up room twice and update separately. " +
                "Most people keep one."),
            item => item.Apps.Select(app => (Gtk.Widget)Widgets.AppRow(ui, app, origin: true)));

        Add(page, ui, items, AttentionKind.BrokenLauncher,
            T._("Menu entries that lead nowhere"),
            T._("These are in your app menu, but the program they start is gone. Clicking them does nothing."),
            item => item.Apps.Select(app => (Gtk.Widget)Widgets.AppRow(ui, app)));

        Add(page, ui, items, AttentionKind.NotInMenu,
            T._("Apps that are not in your menu"),
            T._("Programs I found in your folders that you can only start by finding the file. " +
                "I will be able to add them to your menu later."),
            item => item.Apps.Select(app => (Gtk.Widget)Widgets.AppRow(ui, app)));

        Add(page, ui, items, AttentionKind.LeftoverData,
            T._("Data left behind by apps that are gone"),
            T._("When a Flatpak app is removed, its settings and files stay, in case you install it again. " +
                "If you will not, this is space you can have back."),
            item =>
            {
                var row = Widgets.PathRow(ui, item.Title, item.Path ?? "");
                row.AddPrefix(Widgets.Tag(item.SizeBytes is { } size ? Presentation.FormatSize(size) : T._("Large")));
                return [row];
            });

        return Widgets.Page(T._("Worth a look"), page);
    }

    private static void Add(
        Adw.PreferencesPage page, Ui ui, IReadOnlyList<AttentionItem> items, AttentionKind kind, string title,
        string explanation, Func<AttentionItem, IEnumerable<Gtk.Widget>> rows)
    {
        _ = ui;
        var ofKind = items.Where(item => item.Kind == kind).ToList();
        if (ofKind.Count == 0)
            return;

        var group = Widgets.Group(title, explanation);
        foreach (var item in ofKind)
        {
            foreach (var row in rows(item))
                group.Add(row);
        }
        page.Add(group);
    }
}
