using Yar.Core;
using Yar.Metadata;

namespace Yar.App;

/// <summary>Adding sources: a list of popular ones, and a way to add one by its address.</summary>
internal static class AddSourcePages
{
    public static Adw.NavigationPage CreatePopular(Ui ui)
    {
        var page = Adw.PreferencesPage.New();
        var group = Widgets.Group(
            T._("Popular sources"),
            T._("Sources many people use. Adding one only makes its apps available; nothing is installed until you choose to."),
            T._("This list ships with me; I do not download it. The coloured tag says how well I know a source. " +
                "Sources are added just for you, which needs no password and does not affect other people using this computer."));

        foreach (var suggestion in PopularSources.LoadBundled())
        {
            var existing = PopularSources.FindExisting(suggestion, ui.Snapshot.Sources);
            var manager = ui.Host.SourceManagerFor(suggestion.ProviderId);

            var row = Adw.ActionRow.New();
            row.UseMarkup = false;
            row.Title = suggestion.Name;
            row.Subtitle = suggestion.Caution is null
                ? suggestion.Description
                : $"{suggestion.Description}\n⚠ {suggestion.Caution}";
            row.SubtitleLines = 0;
            row.AddSuffix(Widgets.TrustTag(suggestion.Trust));

            if (existing is not null)
            {
                row.AddSuffix(Widgets.Tag(T._("Already added")));
                row.Activatable = true;
                row.AddSuffix(Gtk.Image.NewFromIconName("go-next-symbolic"));
                row.OnActivated += (_, _) => ui.ShowSource(existing);
            }
            else if (suggestion.Url is not null && manager is { CanAdd: true })
            {
                var add = Gtk.Button.NewWithLabel(T._("Add"));
                add.Valign = Gtk.Align.Center;
                add.OnClicked += (_, _) => ConfirmAdd(
                    ui, manager, new SourceRequest(suggestion.RemoteName ?? suggestion.Name, suggestion.Url),
                    suggestion.Name, suggestion.Trust, suggestion.Caution, add);
                row.AddSuffix(add);
            }
            else
            {
                var website = Gtk.Button.NewWithLabel(T._("Website"));
                website.Valign = Gtk.Align.Center;
                website.TooltipText = suggestion.Homepage;
                website.OnClicked += (_, _) => Open(ui, suggestion.Homepage);
                row.AddSuffix(website);
            }
            group.Add(row);
        }
        page.Add(group);
        return Widgets.Page(T._("Popular sources"), page);
    }

    public static Adw.NavigationPage CreateByAddress(Ui ui)
    {
        var page = Adw.PreferencesPage.New();
        var manager = ui.Host.SourceManagerFor("flatpak");

        var warning = Widgets.Group(T._("Before you add a source"));
        warning.Add(Widgets.Paragraph(
            T._("A source decides which apps you are offered and delivers their updates. Whoever runs it could offer " +
                "you a harmful app, so only add sources from people you trust, such as the maker of an app you want.\n\n" +
                "I can add Flatpak sources here. Their address ends in .flatpakrepo and you usually find it on the " +
                "source's website. Adding one installs nothing by itself.")));
        page.Add(warning);

        var form = Widgets.Group(T._("The source's address"));
        var address = Adw.EntryRow.New();
        address.Title = T._("Address, starting with https://");
        form.Add(address);
        var name = Adw.EntryRow.New();
        name.Title = T._("Short name (optional)");
        form.Add(name);

        var verdict = Adw.ActionRow.New();
        verdict.UseMarkup = false;
        var add = Gtk.Button.NewWithLabel(T._("Add this source"));
        add.Valign = Gtk.Align.Center;
        add.AddCssClass("suggested-action");
        verdict.AddSuffix(add);
        form.Add(verdict);
        page.Add(form);

        void Check()
        {
            var text = address.GetText();
            var (request, problem) = SourceAddress.Check(text, name.GetText());
            add.Sensitive = request is not null && manager is { CanAdd: true };
            if (manager is not { CanAdd: true })
            {
                verdict.Title = T._("Flatpak is not set up on this computer, so I cannot add sources.");
                verdict.Subtitle = "";
            }
            else if (request is null)
            {
                verdict.Title = text.Trim().Length == 0 ? T._("Paste an address above") : T._("I can't use this address");
                verdict.Subtitle = text.Trim().Length == 0 ? "" : T._(problem ?? "");
            }
            else
            {
                verdict.Title = T.F("I would add it as “{0}”, from {1}", request.Name, new Uri(request.Url).Host);
                verdict.Subtitle = T._("I don't know this source, so I cannot tell you whether it is safe.");
            }
        }
        address.OnChanged += (_, _) => Check();
        name.OnChanged += (_, _) => Check();
        Check();

        add.OnClicked += (_, _) =>
        {
            var (request, _) = SourceAddress.Check(address.GetText(), name.GetText());
            if (request is not null && manager is not null)
                ConfirmAdd(ui, manager, request, request.Name, TrustLevel.Unknown, caution: null, add);
        };
        return Widgets.Page(T._("Add a source by address"), page);
    }

    private static void ConfirmAdd(
        Ui ui, ISourceManager manager, SourceRequest request, string displayName, TrustLevel trust, string? caution,
        Gtk.Button button)
    {
        var body = trust == TrustLevel.Unknown
            ? T.F("I don't know this source ({0}). Apps from it could be harmful. Only add it if you trust who runs it.",
                new Uri(request.Url).Host)
            : T._("Its apps will show up in the Store. Nothing is installed until you choose to.");
        if (caution is not null)
            body += "\n\n" + caution;

        Widgets.Confirm(ui, T.F("Add {0}?", displayName), body, T._("Add"), destructive: false,
            () => Add(ui, manager, request, displayName, button));
    }

    private static async void Add(Ui ui, ISourceManager manager, SourceRequest request, string displayName, Gtk.Button button)
    {
        button.Sensitive = false;
        try
        {
            await manager.AddAsync(request, CancellationToken.None);
            ui.Toast(T.F("I've added {0}. Its apps will appear in the Store shortly.", displayName));
            ui.Refresh();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            button.Sensitive = true;
            ui.Toast(T.F("Hmm, I couldn't add it: {0}", Short(e.Message)));
        }
    }

    internal static string Short(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? message;
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private static async void Open(Ui ui, string url)
    {
        try
        {
            await Gtk.UriLauncher.New(url).LaunchAsync(ui.Window);
        }
        catch (GLib.GException)
        {
            ui.Toast(T._("Hmm, I couldn't open that."));
        }
    }
}
