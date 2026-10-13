using Yar.Core;

namespace Yar.App;

/// <summary>The cards the Store's front page is made of.</summary>
internal static class StoreCards
{
    private static readonly Dictionary<AppCategory, string> CategoryIcons = new()
    {
        [AppCategory.Games] = "applications-games",
        [AppCategory.Internet] = "applications-internet",
        [AppCategory.Office] = "applications-office",
        [AppCategory.Graphics] = "applications-graphics",
        [AppCategory.SoundAndVideo] = "applications-multimedia",
        [AppCategory.Development] = "applications-development",
        [AppCategory.Education] = "applications-education",
        [AppCategory.Science] = "applications-science",
        [AppCategory.Utilities] = "applications-utilities",
        [AppCategory.System] = "applications-system",
        [AppCategory.Settings] = "preferences-system",
        [AppCategory.Other] = "applications-other",
    };

    /// <summary>A large card for the banner at the top of the Store.</summary>
    public static Gtk.Button Banner(Ui ui, StoreData data, StoreApp app)
    {
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        text.Valign = Gtk.Align.Center;
        text.Hexpand = true;
        text.Append(Line(app.Name, "title-1", lines: 1));
        if (app.Summary is not null)
            text.Append(Line(app.Summary, null, lines: 2));
        var tags = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        tags.MarginTop = 6;
        foreach (var tag in Facts(ui, data, app))
            tags.Append(Widgets.Tag(tag));
        if (app.Offers.Any(offer => offer.MakerVerified))
        {
            var verified = Widgets.Tag(T._("Verified maker"));
            verified.AddCssClass("yar-good");
            tags.Append(verified);
        }
        text.Append(tags);

        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 24);
        content.Append(Widgets.Icon(app.IconPath, app.IconName, 112));
        content.Append(text);

        var card = Card(content, () => ui.Push(StoreAppPage.Create(ui, data, app)));
        card.AddCssClass("yar-banner");
        card.Hexpand = true;
        return card;
    }

    /// <summary>A small card for one app: icon, name, a line about it, and its numbers.</summary>
    public static Gtk.Button App(Ui ui, StoreData data, StoreApp app)
    {
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        var icon = Widgets.Icon(app.IconPath, app.IconName, 64);
        icon.Halign = Gtk.Align.Center;
        content.Append(icon);
        content.Append(Line(app.Name, "heading", lines: 1, center: true));
        content.Append(Line(app.Summary ?? "", "caption", lines: 2, center: true, dim: true));
        var facts = string.Join(" · ", Facts(ui, data, app));
        if (facts.Length > 0)
            content.Append(Line(facts, "caption", lines: 1, center: true));

        var card = Card(content, () => ui.Push(StoreAppPage.Create(ui, data, app)));
        card.AddCssClass("yar-card");
        card.SetSizeRequest(168, 176);
        card.TooltipText = app.Summary;
        return card;
    }

    /// <summary>A card for a source-code project. Opens the project's page in the web browser.</summary>
    public static Gtk.Button Repo(Ui ui, DevProject project)
    {
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        content.Append(Line(project.FullName, "heading", lines: 1));
        content.Append(Line(project.Description ?? T._("No description"), "caption", lines: 3, dim: true));
        var facts = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        facts.Valign = Gtk.Align.End;
        facts.Vexpand = true;
        facts.Append(Widgets.Tag($"★ {CompactNumber.Format(project.Stars)}"));
        if (project.Language is not null)
            facts.Append(Widgets.Tag(project.Language));
        content.Append(facts);

        var card = Card(content, () => Open(ui, project.Url));
        card.AddCssClass("yar-card");
        card.SetSizeRequest(250, 132);
        card.TooltipText = T._("Open on GitHub in your web browser");
        return card;
    }

    /// <summary>A category shortcut: icon above its name.</summary>
    public static Gtk.Button Category(AppCategory category, int count, Action chosen)
    {
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        var icon = Widgets.Icon(null, IconFor(category), 40);
        icon.Halign = Gtk.Align.Center;
        content.Append(icon);
        content.Append(Line(Presentation.CategoryName(category), "caption-heading", lines: 1, center: true));

        var button = Gtk.Button.New();
        button.Child = content;
        button.AddCssClass("flat");
        button.AddCssClass("yar-category");
        button.SetSizeRequest(96, -1);
        button.TooltipText = T.F(count == 1 ? "{0} app" : "{0} apps", count);
        button.OnClicked += (_, _) => chosen();
        return button;
    }

    public static string IconFor(AppCategory category) => CategoryIcons.GetValueOrDefault(category, "applications-other");

    /// <summary>The short facts shown on a card: rating, and installs last month where known.</summary>
    private static List<string> Facts(Ui ui, StoreData data, StoreApp app)
    {
        var facts = new List<string>();
        if (StoreCatalog.InstalledMatch(app, ui.Snapshot.Apps) is not null)
            facts.Add(T._("Installed"));
        if (data.RatingOf(app) is { Average: { } average })
            facts.Add($"★ {average:0.0}");
        if (data.InstallsOf(app) is { } installs)
            facts.Add(T.F("{0} installs", CompactNumber.Format(installs)));
        return facts;
    }

    private static Gtk.Button Card(Gtk.Widget content, Action activated)
    {
        var card = Gtk.Button.New();
        card.Child = content;
        card.AddCssClass("card");
        card.Valign = Gtk.Align.Start;
        card.OnClicked += (_, _) => activated();
        return card;
    }

    private static Gtk.Label Line(string text, string? style, int lines, bool center = false, bool dim = false)
    {
        var label = Gtk.Label.New(text);
        if (style is not null)
            label.AddCssClass(style);
        if (dim)
            label.AddCssClass("dim-label");
        label.Xalign = center ? 0.5f : 0f;
        label.Justify = center ? Gtk.Justification.Center : Gtk.Justification.Left;
        label.Wrap = lines > 1;
        label.Lines = lines;
        label.Ellipsize = Pango.EllipsizeMode.End;
        // Lets a card stay its set width however long the text is.
        label.WidthChars = 1;
        label.MaxWidthChars = 1;
        label.Hexpand = true;
        return label;
    }

    private static async void Open(Ui ui, string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return;
            await Gtk.UriLauncher.New(uri.AbsoluteUri).LaunchAsync(ui.Window);
        }
        catch (GLib.GException)
        {
            ui.Toast(T._("Hmm, I couldn't open that."));
        }
    }
}
