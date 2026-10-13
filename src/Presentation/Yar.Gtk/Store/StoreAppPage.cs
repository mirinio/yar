using System.Globalization;
using Yar.Core;

namespace Yar.App;

/// <summary>One app in the Store: what it is, what people think of it, and which sources offer it.</summary>
internal static class StoreAppPage
{
    public static Adw.NavigationPage Create(Ui ui, StoreData data, StoreApp app)
    {
        var page = Adw.PreferencesPage.New();
        var installed = StoreCatalog.InstalledMatch(app, ui.Snapshot.Apps);

        var header = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        header.Append(Widgets.Icon(app.IconPath, app.IconName, 96));
        var name = Gtk.Label.New(app.Name);
        name.AddCssClass("title-1");
        name.Wrap = true;
        name.Justify = Gtk.Justification.Center;
        header.Append(name);
        if (app.Summary is not null)
        {
            var summary = Gtk.Label.New(app.Summary);
            summary.AddCssClass("dim-label");
            summary.Wrap = true;
            summary.Justify = Gtk.Justification.Center;
            header.Append(summary);
        }
        var tags = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        tags.Halign = Gtk.Align.Center;
        tags.MarginTop = 6;
        if (data.RatingOf(app) is { Average: { } average } rating)
            tags.Append(Widgets.Tag($"★ {average:0.0} · {T.F(rating.Total == 1 ? "{0} rating" : "{0} ratings", rating.Total)}"));
        foreach (var category in app.Categories.Where(c => c != AppCategory.Other))
            tags.Append(Widgets.Tag(Presentation.CategoryName(category)));
        header.Append(tags);
        var headerGroup = Adw.PreferencesGroup.New();
        headerGroup.Add(header);
        page.Add(headerGroup);
        _ = AddInstallCount(ui, app, tags);

        if (ScreenshotCarousel.Create(ui, app) is { } screenshots)
        {
            var pictures = Widgets.Group(
                T._("What it looks like"),
                help: T._("These pictures are provided by the app's maker. I download them from the source's own " +
                    "server when you open this page; you can switch that off in the settings."));
            pictures.Add(screenshots);
            page.Add(pictures);
        }

        if (installed is not null)
        {
            var have = Widgets.Group();
            var row = Widgets.Navigation(
                T._("You already have this app"), ui.Presentation.OriginName(installed), () => ui.ShowApp(installed));
            row.AddPrefix(Gtk.Image.NewFromIconName("emblem-ok-symbolic"));
            have.Add(row);
            page.Add(have);
        }

        var offerRows = new Dictionary<StoreOffer, (Adw.ActionRow Row, string Notes)>();
        page.Add(CreateOffers(ui, app, offerRows));
        page.Add(CreateReach(ui, app, offerRows));

        if (app.Description is not null)
        {
            var about = Widgets.Group(T._("About this app"));
            about.Add(Widgets.Paragraph(app.Description));
            page.Add(about);
        }

        var facts = Widgets.Group(T._("Details"));
        if (app.Developer is not null)
            facts.Add(Widgets.Property(T._("Made by"), app.Developer));
        if (app.License is not null)
        {
            var license = Widgets.Property(T._("Licence"), LicenseText(app.License));
            license.AddSuffix(Widgets.HelpButton(
                T._("A licence says what you may do with an app. Free and open-source apps let anyone read, check " +
                    "and improve how they work. Proprietary apps keep that to their maker.")));
            facts.Add(license);
        }
        if (app.Homepage is not null)
            facts.Add(Widgets.Link(ui, T._("Website"), app.Homepage));
        if (ui.Technical)
            facts.Add(Widgets.Property("id", string.Join(", ", app.KnownIds)));
        page.Add(facts);

        page.Add(CreateReviews(ui, app));
        return Widgets.Page(app.Name, page);
    }

    // Flathub's numbers for this app, added to the header once they arrive. Nothing is shown without them.
    private static async Task AddInstallCount(Ui ui, StoreApp app, Gtk.Box tags)
    {
        if (!ui.OnlineAllowed || StoreData.FlathubIdOf(app) is not { } id)
            return;
        try
        {
            if (await ui.Host.Online.GetInstallStatsAsync(id, CancellationToken.None) is { Total: > 0 } stats)
            {
                var tag = Widgets.Tag(T.F(
                    "{0} installs · {1} last month", CompactNumber.Format(stats.Total), CompactNumber.Format(stats.LastMonth)));
                tag.TooltipText = T._("How often this app was installed from Flathub. Other sources publish no numbers.");
                tags.Append(tag);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
        }
    }

    private static Adw.PreferencesGroup CreateOffers(
        Ui ui, StoreApp app, Dictionary<StoreOffer, (Adw.ActionRow Row, string Notes)> offerRows)
    {
        var suggested = StoreCatalog.Suggest(app, ui.Presentation.TrustOf);
        var group = Widgets.Group(
            T._("Where you can get it"),
            app.Offers.Count == 1
                ? T._("One of your sources offers this app.")
                : T.F("{0} of your sources offer this app. It is the same app either way; they differ in how it is installed.", app.Offers.Count),
            T._("A Flatpak version is kept apart from the rest of your system and usually arrives straight from " +
                "the app's maker, so it is often the newest. A package from your distribution is checked by the " +
                "distribution's own people and fits in most tightly, but can be a little older and asks for your " +
                "password to install."));

        foreach (var offer in app.Offers.OrderBy(o => o == suggested ? 0 : 1).ThenBy(ui.Presentation.TrustOf))
        {
            var source = ui.Presentation.SourceOf(offer);
            var trust = ui.Presentation.TrustOf(offer);
            var notes = new List<string>
            {
                offer.ProviderId == "flatpak"
                    ? T._("Flatpak: kept apart from the rest of your system")
                    : T._("System package: fits in with your system, needs your password to install"),
            };
            if (offer.MakerVerified)
            {
                notes.Add(offer.VerifiedBy is null
                    ? T._("Published by the app's own maker")
                    : T.F("Published by the app's own maker ({0})", offer.VerifiedBy));
            }
            else if (offer.ProviderId == "flatpak")
            {
                notes.Add(T._("Packaged by someone other than the app's maker, or not confirmed"));
            }
            if (offer.Version is not null)
                notes.Add(T.F("Version {0}", offer.Version));

            var rowTags = new List<Gtk.Widget>();
            if (offer == suggested && app.Offers.Count > 1)
                rowTags.Add(Widgets.Tag(T._("Suggested"), attention: false));
            if (offer.MakerVerified)
            {
                var verified = Widgets.Tag(T._("Verified maker"));
                verified.AddCssClass("yar-good");
                rowTags.Add(verified);
            }
            rowTags.Add(Widgets.TrustTag(trust));

            var title = source?.Name ?? offer.SourceId;
            var row = source is null
                ? Plain(title, string.Join("\n", notes), rowTags)
                : Widgets.Navigation(title, string.Join("\n", notes), () => ui.ShowSource(source), [.. rowTags]);
            row.SubtitleLines = 0;
            group.Add(row);
            offerRows[offer] = (row, string.Join("\n", notes));
        }

        var later = Adw.ActionRow.New();
        later.UseMarkup = false;
        later.Title = T._("I can't install apps yet");
        later.Subtitle = T._("For now, your software centre can install it.");
        var open = Gtk.Button.NewWithLabel(T._("Open software centre"));
        open.Valign = Gtk.Align.Center;
        // An appstream: address opens the app's page in Discover or GNOME Software.
        open.OnClicked += (_, _) => Launch(ui, $"appstream://{Uri.EscapeDataString(app.Offers[0].ComponentId)}");
        later.AddSuffix(open);
        group.Add(later);
        return group;
    }

    // "What it can reach", and the sizes on the offer rows. Both arrive after the page is shown.
    private static Adw.PreferencesGroup CreateReach(
        Ui ui, StoreApp app, Dictionary<StoreOffer, (Adw.ActionRow Row, string Notes)> offerRows)
    {
        var flatpak = app.Offers.FirstOrDefault(offer => offer.ProviderId == "flatpak");
        var group = Widgets.PermissionsGroup(null, ui.Technical);
        if (flatpak is not null)
        {
            group.Description = Widgets.Escape(ui.OnlineAllowed
                ? T._("Looking up what the Flatpak version may reach…")
                : T._("For the Flatpak version I look this up on flathub.org, which is switched off in the settings."));
        }

        foreach (var (offer, (row, notes)) in offerRows)
            _ = FillOffer(ui, app, offer, row, notes, offer == flatpak ? group : null);
        return group;
    }

    private static async Task FillOffer(
        Ui ui, StoreApp app, StoreOffer offer, Adw.ActionRow row, string notes, Adw.PreferencesGroup? reach)
    {
        OfferSize? size = null;
        try
        {
            if (offer.ProviderId == "flatpak" && ui.OnlineAllowed)
            {
                var summary = await ui.Host.Online.GetAppSummaryAsync(offer.ComponentId, CancellationToken.None);
                size = summary?.Size;
                if (reach is not null)
                {
                    if (summary?.Permissions is { } raw)
                    {
                        var report = FlatpakPermissions.Describe(raw);
                        var prefix = app.Offers.Count > 1 ? T._("As a Flatpak: ") : "";
                        reach.Description = Widgets.Escape(prefix + Widgets.PermissionVerdict(report.Verdict));
                        Widgets.AddPermissionRows(reach, report, ui.Technical);
                        if (report.Verdict == PermissionWeight.Broad)
                            row.AddSuffix(Widgets.Tag(T._("Broad access"), attention: true));
                    }
                    else
                    {
                        reach.Description = Widgets.Escape(T._("I couldn't find out what the Flatpak version may reach."));
                    }
                }
            }
            else if (ui.Host.Ability<IOfferSizeProvider>(offer.ProviderId) is { } sizes)
            {
                size = await sizes.GetSizeAsync(offer, CancellationToken.None);
            }
        }
        // Sizes and permissions are extras from other services; whatever goes wrong there, the page stays usable.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (reach is not null)
                reach.Description = Widgets.Escape(T._("I couldn't find out what the Flatpak version may reach."));
        }

        var parts = new List<string>();
        if (size?.DownloadBytes is { } download)
            parts.Add(T.F("{0} to download", Presentation.FormatSize(download)));
        if (size?.InstalledBytes is { } installed)
            parts.Add(T.F("{0} once installed", Presentation.FormatSize(installed)));
        if (parts.Count > 0)
            row.Subtitle = $"{notes}\n{string.Join(" · ", parts)}";
    }

    private static Adw.PreferencesGroup CreateReviews(Ui ui, StoreApp app)
    {
        var group = Widgets.Group(
            T._("Reviews"),
            help: T._("Reviews are written by other people using Linux and collected by ODRS, a service that GNOME and " +
                "KDE also use. I fetch them from odrs.gnome.org when you open this page. You can switch that off " +
                "in the settings."));

        if (!ui.OnlineAllowed)
        {
            group.Description = Widgets.Escape(T._("Reviews are switched off in the settings."));
            return group;
        }

        var waiting = Adw.ActionRow.New();
        waiting.Title = T._("Fetching reviews…");
        var spinner = Gtk.Spinner.New();
        spinner.Spinning = true;
        waiting.AddPrefix(spinner);
        group.Add(waiting);

        _ = FillReviews(ui, app, group, waiting);
        return group;
    }

    private static async Task FillReviews(Ui ui, StoreApp app, Adw.PreferencesGroup group, Adw.ActionRow waiting)
    {
        IReadOnlyList<AppReview>? reviews = null;
        try
        {
            reviews = await ui.Host.Online.GetReviewsAsync(StoreData.ReviewIdsOf(app), CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
        }

        group.Remove(waiting);
        if (reviews is null)
        {
            group.Description = Widgets.Escape(T._("Hmm, I couldn't reach the review service."));
            return;
        }
        if (reviews.Count == 0)
        {
            group.Description = Widgets.Escape(T._("Nobody has reviewed this app yet."));
            return;
        }

        foreach (var review in reviews)
        {
            var row = Adw.ActionRow.New();
            // Review text is written by strangers: never markup, and never more than plain words.
            row.UseMarkup = false;
            row.Title = $"{new string('★', review.Stars)}{new string('☆', 5 - review.Stars)}  {review.Summary}";
            var byline = string.Join(" · ", new[]
            {
                review.Reviewer,
                review.Date?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
                review.Version is null ? null : T.F("version {0}", review.Version),
                review.Distro,
            }.Where(part => !string.IsNullOrWhiteSpace(part)));
            row.Subtitle = review.Text is null ? byline : $"{review.Text}\n{byline}";
            row.SubtitleLines = 0;
            row.SubtitleSelectable = true;
            group.Add(row);
        }
    }

    private static Adw.ActionRow Plain(string title, string subtitle, List<Gtk.Widget> suffixes)
    {
        var row = Adw.ActionRow.New();
        row.UseMarkup = false;
        row.Title = title;
        row.Subtitle = subtitle;
        foreach (var suffix in suffixes)
            row.AddSuffix(suffix);
        return row;
    }

    private static string LicenseText(string license) =>
        license.Contains("proprietary", StringComparison.OrdinalIgnoreCase)
            ? T._("Proprietary: only the maker can see how it works")
            : T.F("{0} (free and open source)", license);

    private static async void Launch(Ui ui, string uri)
    {
        try
        {
            if (!await Gtk.UriLauncher.New(uri).LaunchAsync(ui.Window))
                ui.Toast(T._("Hmm, I couldn't open that."));
        }
        catch (GLib.GException)
        {
            ui.Toast(T._("Hmm, I couldn't find a software centre to open."));
        }
    }
}
