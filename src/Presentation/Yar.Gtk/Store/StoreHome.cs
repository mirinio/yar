using Yar.Core;

namespace Yar.App;

/// <summary>The Store's front page: a banner, category shortcuts, and rows of cards.</summary>
internal static class StoreHome
{
    private const int MaxBanner = 6;
    private const int MaxPerRow = 14;

    public static Gtk.Widget Create(Ui ui, StoreData data, Func<StoreApp, bool> wanted, Action<AppCategory> categoryChosen)
    {
        var page = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        // Keeps rows at their own height instead of stretching the last one to fill a tall window.
        page.Valign = Gtk.Align.Start;
        page.MarginTop = 6;
        page.MarginBottom = 24;
        page.MarginStart = 24;
        page.MarginEnd = 24;

        var picks = data.Resolve(data.Collections.Picks).Where(wanted).Take(MaxBanner).ToList();
        var fromFlathub = picks.Count > 0;
        if (!fromFlathub)
            picks = data.WellKnownApps().Where(wanted).Take(MaxBanner).ToList();
        if (picks.Count > 0)
        {
            page.Append(Heading(
                fromFlathub ? T._("Picked this week") : T._("Well-known apps"),
                fromFlathub
                    ? T._("Apps the people who run Flathub chose to highlight this week. I fetch this list from flathub.org.")
                    : null));
            page.Append(Banner(ui, data, picks));
        }

        var categories = Enum.GetValues<AppCategory>()
            .Select(category => (Category: category, Count: data.Apps.Count(app => app.Categories.Contains(category))))
            .Where(entry => entry.Count > 0)
            .OrderBy(entry => entry.Category == AppCategory.Other)
            .ToList();
        if (categories.Count > 0)
        {
            page.Append(Heading(T._("Browse by kind"), null));
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            foreach (var (category, count) in categories)
                row.Append(StoreCards.Category(category, count, () => categoryChosen(category)));
            page.Append(Scroller(row));
        }

        AddRow(page, ui, data, wanted, T._("Trending this week"), data.Collections.Trending,
            T._("Apps that are being installed much more than usual right now, according to Flathub."));
        AddRow(page, ui, data, wanted, T._("Most downloaded this month"), data.Collections.MostInstalled,
            T._("The apps people installed most from Flathub in the last month. \"Installs\" counts Flathub only; " +
                "your distribution does not publish such numbers for its own packages."));
        AddRow(page, ui, data, wanted, T._("Recently updated"), data.Collections.RecentlyUpdated, null);

        if (data.DevProjects.Count > 0)
        {
            page.Append(Heading(
                T._("For developers: new projects on GitHub"),
                T._("Source-code projects started in the last month that many people starred, in the programming " +
                    "languages you ticked in the settings. These are not apps I can install; a card opens the " +
                    "project in your web browser.")));
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            foreach (var project in data.DevProjects.Take(MaxPerRow))
                row.Append(StoreCards.Repo(ui, project));
            page.Append(Scroller(row));
        }

        // The Store uses the whole width of the window; cards are what fill it.
        var scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scrolled.Child = page;
        scrolled.Vexpand = true;
        return scrolled;
    }

    /// <summary>The apps of one category as a grid of cards.</summary>
    public static Gtk.Widget CreateCategory(
        Ui ui, StoreData data, AppCategory category, IReadOnlyList<StoreApp> apps, int total)
    {
        var grid = Gtk.FlowBox.New();
        grid.SelectionMode = Gtk.SelectionMode.None;
        grid.Homogeneous = true;
        grid.MaxChildrenPerLine = 12;
        grid.MinChildrenPerLine = 2;
        grid.ColumnSpacing = 12;
        grid.RowSpacing = 12;
        grid.Valign = Gtk.Align.Start;
        foreach (var app in apps)
            grid.Append(StoreCards.App(ui, data, app));

        var page = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        // Keeps rows at their own height instead of stretching the last one to fill a tall window.
        page.Valign = Gtk.Align.Start;
        page.MarginTop = 6;
        page.MarginBottom = 24;
        page.MarginStart = 24;
        page.MarginEnd = 24;
        page.Append(Heading(
            apps.Count < total
                ? T.F("{0}: the first {1} of {2} apps", Presentation.CategoryName(category), apps.Count, total)
                : T.F("{0}: {1} apps", Presentation.CategoryName(category), total),
            T._("Most installed first, where Flathub has numbers; the rest by name. Search above to look " +
                "through all of them.")));
        page.Append(grid);

        // The Store uses the whole width of the window; cards are what fill it.
        var scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scrolled.Child = page;
        scrolled.Vexpand = true;
        return scrolled;
    }

    private static void AddRow(
        Gtk.Box page, Ui ui, StoreData data, Func<StoreApp, bool> wanted, string title,
        IReadOnlyList<StoreListing> listings, string? help)
    {
        var apps = data.Resolve(listings).Where(wanted).Take(MaxPerRow).ToList();
        // A list that could not be fetched, or has nothing new for this user, is simply left out.
        if (apps.Count == 0)
            return;

        page.Append(Heading(title, help));
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        foreach (var app in apps)
            row.Append(StoreCards.App(ui, data, app));
        page.Append(Scroller(row));
    }

    private static Gtk.Box Banner(Ui ui, StoreData data, List<StoreApp> apps)
    {
        var carousel = Adw.Carousel.New();
        carousel.Spacing = 12;
        carousel.AllowScrollWheel = false;
        foreach (var app in apps)
            carousel.Append(StoreCards.Banner(ui, data, app));

        var dots = Adw.CarouselIndicatorDots.New();
        dots.Carousel = carousel;

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        box.Append(carousel);
        box.Append(PagerRow(carousel, dots));
        return box;
    }

    /// <summary>Previous and next buttons around the dots, so a carousel also works without swiping.</summary>
    public static Gtk.Widget PagerRow(Adw.Carousel carousel, Adw.CarouselIndicatorDots dots)
    {
        void Move(int by)
        {
            var pages = (int)carousel.NPages;
            if (pages == 0)
                return;
            var target = Math.Clamp((int)Math.Round(carousel.Position) + by, 0, pages - 1);
            carousel.ScrollTo(carousel.GetNthPage((uint)target), true);
        }

        var previous = Gtk.Button.NewFromIconName("go-previous-symbolic");
        previous.AddCssClass("flat");
        previous.TooltipText = T._("Previous");
        previous.OnClicked += (_, _) => Move(-1);
        var next = Gtk.Button.NewFromIconName("go-next-symbolic");
        next.AddCssClass("flat");
        next.TooltipText = T._("Next");
        next.OnClicked += (_, _) => Move(1);

        // The dots draw wider than the space they ask for, so they get the middle of a three-part row to themselves.
        var row = Gtk.CenterBox.New();
        row.StartWidget = previous;
        row.CenterWidget = dots;
        row.EndWidget = next;
        row.MarginTop = 4;
        return row;
    }

    private static Gtk.Box Heading(string title, string? help)
    {
        var label = Gtk.Label.New(title);
        label.AddCssClass("title-3");
        label.Xalign = 0;
        label.Hexpand = true;
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        row.MarginTop = 12;
        row.Append(label);
        if (help is not null)
            row.Append(Widgets.HelpButton(help));
        return row;
    }

    // A row that scrolls sideways when it has more cards than fit.
    private static Gtk.ScrolledWindow Scroller(Gtk.Widget row)
    {
        var scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Never);
        scrolled.Child = row;
        row.MarginBottom = 10;
        return scrolled;
    }
}
