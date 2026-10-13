using Yar.Core;

namespace Yar.App;

/// <summary>"Add an app": where a file or a download link is handed to Yar.</summary>
internal static class AddAppStart
{
    public static Adw.NavigationPage Create(Ui ui, string? link = null)
    {
        var page = Adw.PreferencesPage.New();

        var file = Widgets.Group(
            T._("An app you downloaded"),
            T._("Drop the file anywhere on this window, or pick it here. I look at it first and show you what I found; " +
                "nothing is installed until you say so."));
        var choose = Adw.ActionRow.New();
        choose.Title = T._("Choose a file…");
        choose.Subtitle = T._("An AppImage, a zip or tar archive, an RPM or Debian package, or a Flatpak file");
        choose.AddPrefix(Gtk.Image.NewFromIconName("document-open-symbolic"));
        choose.AddSuffix(Gtk.Image.NewFromIconName("go-next-symbolic"));
        choose.Activatable = true;
        choose.OnActivated += (_, _) => ChooseFile(ui);
        file.Add(choose);
        page.Add(file);

        var online = Widgets.Group(
            T._("Or a download link"),
            T._("Paste the link of the file itself, not of the page it is on."),
            T._("I download the file into my own temporary folder and then look at it exactly like a file you " +
                "dropped. I only download over a protected connection (https). A protected connection means " +
                "nobody tampered with the file on the way; it does not mean the server is honest. That is what " +
                "the publisher's hash on the next page is for."));
        var address = Adw.EntryRow.New();
        address.Title = T._("Link, starting with https://");
        if (link is not null)
            address.SetText(link);
        online.Add(address);

        var status = Adw.ActionRow.New();
        status.UseMarkup = false;
        status.SubtitleLines = 0;
        var fetch = Gtk.Button.NewWithLabel(T._("Fetch"));
        fetch.Valign = Gtk.Align.Center;
        fetch.AddCssClass("suggested-action");
        status.AddSuffix(fetch);
        online.Add(status);
        page.Add(online);

        void Check()
        {
            var (checkedLink, problem) = DownloadLink.Check(address.GetText());
            fetch.Sensitive = checkedLink is not null;
            status.Title = checkedLink is not null ? T.F("From {0}", checkedLink.Host)
                : problem is not null ? T._("I can't use this link")
                : T._("Paste a link above");
            status.Subtitle = problem is null ? "" : T._(problem);
        }
        address.OnChanged += (_, _) => Check();
        Check();
        fetch.OnClicked += (_, _) =>
        {
            if (DownloadLink.Check(address.GetText()).Link is { } checkedLink)
                Fetch(ui, checkedLink, fetch, status);
        };

        var kinds = Widgets.Group(T._("What happens with each kind of file"));
        foreach (var (title, text) in new[]
        {
            (T._("AppImage or a single program"), T._("The one file goes into your apps folder and gets an app-menu entry.")),
            (T._("Zip or tar archive"), T._("Unpacked into a folder of its own in your apps folder, with an app-menu entry for the program inside.")),
            (T._("RPM or Debian package"), T._("Handed to your system's package manager, which asks for your password.")),
            (T._("Flatpak file"), T._("Handed to Flatpak, installed for you only.")),
            (T._("Windows programs, installer scripts, source code"), T._("Not installed. I tell you what it is and what to look for instead.")),
        })
        {
            var row = Adw.ActionRow.New();
            row.Title = title;
            row.Subtitle = text;
            row.SubtitleLines = 0;
            kinds.Add(row);
        }
        page.Add(kinds);

        return Widgets.Page(T._("Add an app"), page);
    }

    // async void: started by a click; every outcome is shown.
    private static async void ChooseFile(Ui ui)
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.Title = T._("Choose the app you downloaded");
            if (await dialog.OpenAsync(ui.Window) is { } chosen && chosen.GetPath() is { } path)
                AddAppPage.Open(ui, path);
        }
        catch (GLib.GException)
        {
            // The dialog was closed without choosing anything.
        }
    }

    // async void: started by a click; every outcome is shown in the status row.
    private static async void Fetch(Ui ui, Uri link, Gtk.Button fetch, Adw.ActionRow status)
    {
        fetch.Sensitive = false;
        status.Title = T.F("Asking {0} what this is…", link.Host);
        status.Subtitle = "";
        LinkInfo info;
        try
        {
            info = await ui.Host.Downloader.ProbeAsync(link, CancellationToken.None);
        }
        catch (Exception e) when (e is InvalidOperationException or TaskCanceledException)
        {
            status.Title = T._("Hmm, that didn't work");
            status.Subtitle = T._(e.Message);
            fetch.Sensitive = true;
            return;
        }

        var size = info.Bytes is { } bytes ? Presentation.FormatSize(bytes) : T._("of unknown size");
        Widgets.Confirm(
            ui, T.F("Download {0}?", info.FileName),
            T.F("{0}, from {1}.\n\nI will keep it in my temporary folder and look at it before anything is installed. " +
                "I can't tell from the link whether {1} is the app's real maker; check that the address is the one you expect.",
                size, info.Address.Host),
            T._("Download"), destructive: false,
            confirmed: () => Download(ui, info, fetch, status),
            cancelled: () =>
            {
                status.Title = T.F("From {0}", link.Host);
                fetch.Sensitive = true;
            });
    }

    private static async void Download(Ui ui, LinkInfo info, Gtk.Button fetch, Adw.ActionRow status)
    {
        using var cancel = new CancellationTokenSource();
        var stop = Gtk.Button.NewWithLabel(T._("Stop"));
        stop.Valign = Gtk.Align.Center;
        stop.OnClicked += (_, _) => cancel.Cancel();
        status.AddSuffix(stop);
        fetch.Visible = false;

        var shownAt = 0L;
        var progress = new Progress<long>(received =>
        {
            // A line of text a few times a second is plenty.
            if (Environment.TickCount64 - shownAt < 200)
                return;
            shownAt = Environment.TickCount64;
            status.Title = info.Bytes is { } total and > 0
                ? T.F("Downloading… {0} of {1}", Presentation.FormatSize(received), Presentation.FormatSize(total))
                : T.F("Downloading… {0}", Presentation.FormatSize(received));
        });

        string? path = null;
        try
        {
            status.Title = T._("Downloading…");
            path = await ui.Host.Downloader.DownloadAsync(info, progress, cancel.Token);
            status.Title = T.F("Downloaded {0}", info.FileName);
        }
        catch (OperationCanceledException)
        {
            status.Title = T._("Stopped. Nothing was kept.");
        }
        catch (InvalidOperationException e)
        {
            status.Title = T._("Hmm, the download didn't work");
            status.Subtitle = T._(e.Message);
        }
        status.Remove(stop);
        fetch.Visible = true;
        fetch.Sensitive = true;

        if (path is not null)
            AddAppPage.Open(ui, path, info.Address.AbsoluteUri, downloaded: true);
    }
}
