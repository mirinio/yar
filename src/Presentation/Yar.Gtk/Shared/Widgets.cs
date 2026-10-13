using Yar.Core;

namespace Yar.App;

/// <summary>The small building blocks the pages share.</summary>
internal static class Widgets
{
    private const string Css = """
        .yar-tag {
            font-size: 0.8em;
            padding: 2px 8px;
            border-radius: 99px;
            background-color: alpha(currentColor, 0.1);
        }
        .yar-tag.yar-match {
            background-color: alpha(@accent_bg_color, 0.85);
            color: @accent_fg_color;
        }
        .yar-banner {
            padding: 18px;
            background-image: linear-gradient(120deg, alpha(@accent_bg_color, 0.35), alpha(@accent_bg_color, 0.08));
        }
        .yar-card {
            padding: 12px;
        }
        .yar-category {
            padding: 8px 6px;
        }
        .yar-shot {
            border-radius: 12px;
        }
        .yar-tag.yar-good {
            background-color: alpha(@success_color, 0.25);
        }
        .yar-tag.yar-attention {
            background-color: alpha(@warning_color, 0.25);
        }
        """;

    private static bool _cssLoaded;

    public static void LoadCss()
    {
        if (_cssLoaded || Gdk.Display.GetDefault() is not { } display)
            return;
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString(Css);
        Gtk.StyleContext.AddProviderForDisplay(display, provider, 600); // GTK_STYLE_PROVIDER_PRIORITY_APPLICATION
        _cssLoaded = true;
    }

    /// <summary>A page with a header bar (and back button, once pushed) around <paramref name="content"/>.</summary>
    public static Adw.NavigationPage Page(string title, Gtk.Widget content, Adw.Banner? banner = null)
    {
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        if (banner is not null)
            toolbar.AddTopBar(banner);
        toolbar.Content = content;
        return Adw.NavigationPage.New(toolbar, title);
    }

    public static Adw.PreferencesGroup Group(
        string? title = null, string? description = null, string? help = null, int helpWidthChars = 42)
    {
        var group = Adw.PreferencesGroup.New();
        // Group titles are always read as markup.
        if (title is not null)
            group.Title = Escape(title);
        if (description is not null)
            group.Description = Escape(description);
        if (help is not null)
            group.HeaderSuffix = HelpButton(help, helpWidthChars);
        return group;
    }

    /// <summary>A small "?" that explains a term to someone who has not met it before.</summary>
    public static Gtk.MenuButton HelpButton(string explanation, int widthChars = 42)
    {
        var label = Gtk.Label.New(explanation);
        label.Wrap = true;
        label.MaxWidthChars = widthChars;
        label.Xalign = 0;
        label.MarginTop = 6;
        label.MarginBottom = 6;
        label.MarginStart = 6;
        label.MarginEnd = 6;

        var popover = Gtk.Popover.New();
        popover.Child = label;

        var button = Gtk.MenuButton.New();
        // A plain "?" rather than a themed icon, which some icon themes draw as an "i".
        // Set as a child, not as a label: a labelled menu button also draws a drop-down arrow.
        button.Child = Gtk.Label.New("?");
        button.TooltipText = T._("What is this?");
        button.Popover = popover;
        button.Valign = Gtk.Align.Center;
        button.AddCssClass("flat");
        button.AddCssClass("circular");
        return button;
    }

    public static Gtk.Label Tag(string text, bool attention = false)
    {
        var label = Gtk.Label.New(text);
        label.AddCssClass("yar-tag");
        if (attention)
            label.AddCssClass("yar-attention");
        label.Valign = Gtk.Align.Center;
        return label;
    }

    /// <summary>How well Yar knows a source, as a coloured tag: green for the system's own, amber for the unknown.</summary>
    public static Gtk.Label TrustTag(TrustLevel trust)
    {
        var tag = Tag(Presentation.TrustText(trust), attention: trust is TrustLevel.Unknown or TrustLevel.ThirdParty);
        if (trust == TrustLevel.Distro)
            tag.AddCssClass("yar-good");
        return tag;
    }

    /// <summary>An icon from a file when there is one of a sane size, otherwise from the icon theme.</summary>
    public static Gtk.Image Icon(string? path, string? name, int pixelSize)
    {
        Gtk.Image? image = null;
        if (path is not null)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length is > 0 and <= 5 * 1024 * 1024)
                    image = Gtk.Image.NewFromFile(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (image is null && name is not null && Gdk.Display.GetDefault() is { } display &&
            Gtk.IconTheme.GetForDisplay(display).HasIcon(name))
        {
            image = Gtk.Image.NewFromIconName(name);
        }
        image ??= Gtk.Image.NewFromIconName("application-x-executable");
        image.PixelSize = pixelSize;
        return image;
    }

    /// <summary>Asks before doing something that changes the system. <paramref name="confirmed"/> runs only on yes.</summary>
    public static void Confirm(
        Ui ui, string heading, string body, string yes, bool destructive, Action confirmed, Action? cancelled = null) =>
        Confirm(ui.Window, heading, body, yes, destructive, confirmed, cancelled);

    public static void Confirm(
        Gtk.Widget parent, string heading, string body, string yes, bool destructive, Action confirmed,
        Action? cancelled = null)
    {
        var dialog = Adw.AlertDialog.New(heading, body);
        dialog.AddResponse("cancel", T._("Cancel"));
        dialog.AddResponse("yes", yes);
        dialog.SetResponseAppearance(
            "yes", destructive ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested);
        dialog.SetDefaultResponse("cancel");
        dialog.SetCloseResponse("cancel");
        dialog.OnResponse += (_, args) =>
        {
            if (args.Response == "yes")
                confirmed();
            else
                cancelled?.Invoke();
        };
        dialog.Present(parent);
    }

    /// <summary>A row for a fact: small label, prominent value that can be selected and copied.</summary>
    public static Adw.ActionRow Property(string title, string value)
    {
        var row = Adw.ActionRow.New();
        // Values come from files on disk; never let them be read as markup.
        row.UseMarkup = false;
        row.Title = title;
        row.Subtitle = value;
        row.SubtitleSelectable = true;
        row.AddCssClass("property");
        return row;
    }

    /// <summary>A row that leads to another page when activated.</summary>
    public static Adw.ActionRow Navigation(
        string title, string? subtitle, Action activated, params Gtk.Widget[] suffixes)
    {
        var row = Adw.ActionRow.New();
        row.UseMarkup = false;
        row.Title = title;
        row.Subtitle = subtitle ?? "";
        row.Activatable = true;
        foreach (var suffix in suffixes)
            row.AddSuffix(suffix);
        row.AddSuffix(Gtk.Image.NewFromIconName("go-next-symbolic"));
        row.OnActivated += (_, _) => activated();
        return row;
    }

    /// <summary>A row that opens a website. Anything that is not a plain https address is shown but not opened.</summary>
    public static Adw.ActionRow Link(Ui ui, string title, string url)
    {
        var row = Property(title, url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return row;

        row.Activatable = true;
        row.TooltipText = T._("Open in your web browser");
        row.AddSuffix(Gtk.Image.NewFromIconName("adw-external-link-symbolic"));
        row.OnActivated += (_, _) => Run(ui, Gtk.UriLauncher.New(uri.AbsoluteUri).LaunchAsync(ui.Window));
        return row;
    }

    /// <summary>A row for a place on disk, with buttons to copy the path and to show it in the file manager.</summary>
    public static Adw.ActionRow PathRow(Ui ui, string title, string path)
    {
        var row = Property(title, path);

        var copy = Gtk.Button.NewFromIconName("edit-copy-symbolic");
        copy.TooltipText = T._("Copy this path");
        copy.Valign = Gtk.Align.Center;
        copy.AddCssClass("flat");
        copy.OnClicked += (_, _) =>
        {
            if (Gdk.Display.GetDefault() is not { } display)
                return;
            display.GetClipboard().SetText(path);
            ui.Toast(T._("Copied"));
        };
        row.AddSuffix(copy);

        var isFolder = Directory.Exists(path);
        var open = Gtk.Button.NewFromIconName("folder-open-symbolic");
        open.TooltipText = T._("Show in your file manager");
        open.Valign = Gtk.Align.Center;
        open.AddCssClass("flat");
        open.Sensitive = isFolder || File.Exists(path);
        open.OnClicked += (_, _) =>
        {
            var launcher = Gtk.FileLauncher.New(Gio.FileHelper.NewForPath(path));
            // A folder is opened; for a file, its folder is opened with the file selected.
            Run(ui, isFolder ? launcher.LaunchAsync(ui.Window) : launcher.OpenContainingFolderAsync(ui.Window));
        };
        row.AddSuffix(open);
        return row;
    }

    /// <param name="matchedTag">The tag a search found this app by; it is shown highlighted.</param>
    public static Adw.ActionRow AppRow(Ui ui, AppInfo app, AppCategory? matchedTag = null)
    {
        var row = Adw.ActionRow.New();
        row.UseMarkup = false;
        row.Title = app.Name;
        row.Subtitle = app.Summary ?? "";
        row.SubtitleLines = 1;
        row.Activatable = true;
        row.AddPrefix(Presentation.IconFor(app, 32));
        if (ui.Presentation.UpdateFor(app) is not null)
            row.AddSuffix(Tag(T._("Update waiting"), attention: true));
        if (matchedTag is { } matched)
        {
            var tag = Tag(Presentation.CategoryName(matched));
            tag.AddCssClass("yar-match");
            row.AddSuffix(tag);
        }
        else
        {
            // "Other" says nothing, so it is not worth a tag.
            foreach (var category in app.Categories.Where(c => c != AppCategory.Other).Take(1))
                row.AddSuffix(Tag(Presentation.CategoryName(category)));
        }
        row.AddSuffix(Gtk.Image.NewFromIconName("go-next-symbolic"));
        row.OnActivated += (_, _) => ui.ShowApp(app);
        return row;
    }

    /// <summary>
    /// The on/off switch of a source. Turning on a source nobody vouches for asks first, and a
    /// change that fails or is cancelled puts the switch back where it was.
    /// </summary>
    public static Gtk.Switch SourceSwitch(Ui ui, ISourceManager manager, SourceInfo source)
    {
        var toggle = Gtk.Switch.New();
        toggle.Active = source.Enabled;
        toggle.Valign = Gtk.Align.Center;
        toggle.TooltipText = source.Enabled ? T._("Turn this source off") : T._("Turn this source on");

        var settingBack = false;
        void Revert()
        {
            settingBack = true;
            toggle.Active = source.Enabled;
            settingBack = false;
        }

        toggle.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() != "active" || settingBack || toggle.Active == source.Enabled)
                return;

            var wanted = toggle.Active;
            if (wanted && source.Trust is TrustLevel.Unknown or TrustLevel.ThirdParty)
            {
                Confirm(
                    ui, T.F("Turn on {0}?", source.Name), Presentation.TrustExplanation(source.Trust),
                    T._("Turn on"), destructive: false,
                    confirmed: () => SwitchSource(ui, manager, source, wanted, toggle, Revert),
                    cancelled: Revert);
            }
            else if (!wanted && source.Trust == TrustLevel.Distro)
            {
                // The system's own sources are where security updates come from.
                Confirm(
                    ui, T.F("Turn off {0}?", source.Name),
                    T._("Your system gets its own apps and security updates from here. With it off, you may " +
                        "stop getting updates. Only turn it off if you know why you need to."),
                    T._("Turn off"), destructive: true,
                    confirmed: () => SwitchSource(ui, manager, source, wanted, toggle, Revert),
                    cancelled: Revert);
            }
            else
            {
                SwitchSource(ui, manager, source, wanted, toggle, Revert);
            }
        };
        return toggle;
    }

    private static async void SwitchSource(
        Ui ui, ISourceManager manager, SourceInfo source, bool enabled, Gtk.Switch toggle, Action revert)
    {
        toggle.Sensitive = false;
        try
        {
            await manager.SetEnabledAsync(source, enabled, CancellationToken.None);
            ui.Toast(enabled ? T.F("{0} is now turned on.", source.Name) : T.F("{0} is now turned off.", source.Name));
            ui.Refresh();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Includes the user closing the password prompt: nothing was changed.
            revert();
            toggle.Sensitive = true;
            ui.Toast(T.F("I left {0} as it was: {1}", source.Name, AddSourcePages.Short(e.Message)));
        }
    }

    public static Adw.ActionRow SourceRow(Ui ui, SourceInfo source)
    {
        var apps = ui.Presentation.AppsOf(source).Count;
        var tags = new List<Gtk.Widget>();
        if (apps > 0)
            tags.Add(Tag(T.F(apps == 1 ? "{0} app" : "{0} apps", apps)));
        if (source.ProviderId != "manual")
            tags.Add(TrustTag(source.Trust));
        // The switch sits right in the list, so nobody has to open each source to find it.
        if (ui.Host.SourceManagerFor(source.ProviderId) is { } manager && manager.CanToggle(source))
            tags.Add(SourceSwitch(ui, manager, source));
        else if (!source.Enabled)
            tags.Add(Tag(T._("Turned off")));

        var row = Navigation(source.Name, source.Description, () => ui.ShowSource(source), [.. tags]);
        row.SubtitleLines = 2;
        if (!source.Enabled)
            row.AddCssClass("dim-label");
        return row;
    }

    // Opening something in another program can fail or be cancelled; say so instead of going quiet.
    private static async void Run(Ui ui, Task<bool> opening)
    {
        try
        {
            if (!await opening)
                ui.Toast(T._("Hmm, I couldn't open that."));
        }
        catch (GLib.GException)
        {
            ui.Toast(T._("Hmm, I couldn't open that."));
        }
    }

    public static Gtk.Label Paragraph(string text)
    {
        var label = Gtk.Label.New(text);
        label.Wrap = true;
        label.Xalign = 0;
        label.Selectable = false;
        return label;
    }

    public static string Escape(string text) => GLib.Functions.MarkupEscapeText(text, -1);
}
