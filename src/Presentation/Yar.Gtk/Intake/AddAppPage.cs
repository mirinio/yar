using Yar.Core;

namespace Yar.App;

/// <summary>
/// The preview of an app the user brought: what it is, whether it can run here, and everything
/// about how it will be set up, all of it adjustable before anything happens.
/// </summary>
internal static class AddAppPage
{
    /// <param name="downloaded">True when Yar fetched the file itself; it is then thrown away again when no longer needed.</param>
    public static async void Open(Ui ui, string path, string? originUrl = null, bool downloaded = false)
    {
        var stack = Gtk.Stack.New();
        var busy = Adw.StatusPage.New();
        busy.Title = T.F("Looking at {0}…", Path.GetFileName(path));
        busy.Description = T._("I'm checking what it is and whether it can run on this computer. I don't start it to find out.");
        var spinner = Gtk.Spinner.New();
        spinner.Spinning = true;
        spinner.SetSizeRequest(32, 32);
        spinner.Halign = Gtk.Align.Center;
        busy.Child = spinner;
        stack.AddChild(busy);

        var navigation = Widgets.Page(T._("Add an app"), stack);
        // Leaving the page before adding means the user decided against it: stop, and keep nothing.
        var cancel = new CancellationTokenSource();
        IntakeReport? report = null;
        var added = false;
        navigation.OnHidden += (_, _) =>
        {
            cancel.Cancel();
            if (added)
                return;
            if (report is not null)
                ui.Host.Intake.Discard(report);
            if (downloaded)
                ui.Host.Downloader.Discard(path);
        };
        ui.Push(navigation);

        try
        {
            report = await ui.Host.Intake.InspectAsync(path, originUrl, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        // Looking at a stranger's file can fail in ways nobody listed; say so instead of ending Yar.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            busy.Child = null;
            busy.IconName = "dialog-warning-symbolic";
            busy.Title = T._("Hmm, I couldn't look at that file");
            busy.Description = Widgets.Escape(AddSourcePages.Short(e.Message));
            return;
        }
        if (cancel.IsCancellationRequested)
        {
            ui.Host.Intake.Discard(report);
            return;
        }

        Gtk.Widget preview = report.Blocked is null
            ? Preview(ui, report, downloaded, () => added = true)
            : Refusal(report);
        stack.AddChild(preview);
        stack.VisibleChild = preview;
        navigation.Title = report.Blocked is null ? T.F("Add {0}", report.SuggestedName) : T._("Add an app");
    }

    private static Adw.StatusPage Refusal(IntakeReport report)
    {
        var status = Adw.StatusPage.New();
        status.IconName = "dialog-information-symbolic";
        status.Title = Widgets.Escape(report.KindLabel);
        status.Description = Widgets.Escape($"{report.FileName}\n\n{T._(report.Blocked!)}");
        return status;
    }

    private static Adw.PreferencesPage Preview(Ui ui, IntakeReport report, bool downloaded, Action onAdded)
    {
        var page = Adw.PreferencesPage.New();
        var iconPath = report.IconPath;
        var folder = ui.AppsFolder;
        var hashVerified = false;
        var hashDiffers = false;

        // Header: the picture and the name, both changeable.
        var iconBox = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        iconBox.Halign = Gtk.Align.Center;
        var iconNote = Gtk.Label.New("");
        iconNote.AddCssClass("dim-label");
        iconNote.AddCssClass("caption");
        iconNote.Wrap = true;
        iconNote.Justify = Gtk.Justification.Center;
        Gtk.Image? shown = null;
        void ShowIcon()
        {
            if (shown is not null)
                iconBox.Remove(shown);
            shown = Widgets.Icon(iconPath, null, 96);
            iconBox.Prepend(shown);
            iconNote.SetText(iconPath is not null ? ""
                : report.IconComesWithIt ? T._("It brings its own picture, which appears once it is installed.")
                : T._("I found no picture for this app, so it gets this plain one. You can choose your own."));
            iconNote.Visible = iconNote.GetText().Length > 0;
        }
        var header = Adw.PreferencesGroup.New();
        header.Add(iconBox);
        iconBox.Append(iconNote);
        if (report.FolderIsChoosable)
        {
            var choose = Gtk.Button.NewWithLabel(T._("Choose a picture…"));
            choose.Halign = Gtk.Align.Center;
            choose.AddCssClass("pill");
            choose.OnClicked += async (_, _) =>
            {
                if (await PickAsync(ui, T._("Choose a picture for this app"), folderOnly: false) is not { } picked)
                    return;
                // Checked the same way the app's own picture would be: a PNG or SVG of sensible size.
                var plan = await ui.Host.Intake.PlanAsync(report, new IntakeChoices("x", folder) { IconPath = picked }, CancellationToken.None);
                if (plan.Blocked is not null && plan.Blocked.Contains("picture", StringComparison.Ordinal))
                {
                    ui.Toast(T._(plan.Blocked));
                    return;
                }
                iconPath = picked;
                ShowIcon();
            };
            iconBox.Append(choose);
        }
        ShowIcon();
        page.Add(header);

        var identity = Widgets.Group();
        var name = Adw.EntryRow.New();
        name.Title = T._("Name");
        name.SetText(report.SuggestedName);
        name.Sensitive = report.FolderIsChoosable;
        identity.Add(name);
        if (report.Summary is not null)
            identity.Add(Widgets.Property(T._("What it says about itself"), report.Summary));
        page.Add(identity);

        // What it is, and whether it can run here.
        var what = Widgets.Group(
            T._("What I found"),
            help: T._("I work this out by reading the file, never by starting it. A tick means that part is fine. " +
                "An exclamation mark means it may still work, but I can't promise."));
        what.Add(Widgets.Property(T._("Kind"), T._(report.KindLabel)));
        what.Add(Widgets.Property(
            T._("File"),
            $"{report.FileName} · {Presentation.FormatSize(report.FileBytes)}" +
            (report.Version is null ? "" : $" · {T.F("version {0}", report.Version)}")));
        if (report.OriginUrl is not null && Uri.TryCreate(report.OriginUrl, UriKind.Absolute, out var origin))
            what.Add(Widgets.Property(T._("Downloaded from"), origin.Host));
        foreach (var check in report.Checks)
        {
            var row = Adw.ActionRow.New();
            row.UseMarkup = false;
            row.Title = T._(check.Text);
            row.TitleLines = 0;
            row.AddPrefix(Gtk.Image.NewFromIconName(check.State switch
            {
                CheckState.Good => "emblem-ok-symbolic",
                CheckState.Warning => "dialog-warning-symbolic",
                _ => "dialog-error-symbolic",
            }));
            what.Add(row);
        }
        page.Add(what);

        // The publisher's hash.
        var genuine = Widgets.Group(
            T._("Is it the genuine file?"),
            T._("Careful publishers print a hash next to their download: a long code that is different for every file. " +
                "Paste it here and I compare."),
            T._("If even one byte of the file was changed on the way to you, its hash comes out completely " +
                "different. So a match means you have exactly the file the publisher describes. Copy the hash " +
                "from the publisher's own website, not from the place you got the file. You can paste a single " +
                "hash or a whole list of them; I pick the line for this file."));
        var pasted = Adw.EntryRow.New();
        pasted.Title = T._("Paste the publisher's hash (optional)");
        genuine.Add(pasted);
        var verdict = Adw.ActionRow.New();
        verdict.UseMarkup = false;
        verdict.TitleLines = 0;
        verdict.SubtitleLines = 0;
        var verdictIcon = Gtk.Image.NewFromIconName("dialog-question-symbolic");
        verdict.AddPrefix(verdictIcon);
        genuine.Add(verdict);
        var own = Widgets.Property(T._("This file's SHA-256 hash"), report.Sha256);
        genuine.Add(own);
        page.Add(genuine);

        // Where it goes.
        Adw.ActionRow? where = null;
        Adw.ComboRow? programs = null;
        var menu = Adw.SwitchRow.New();
        var move = Adw.SwitchRow.New();
        if (report.FolderIsChoosable)
        {
            var place = Widgets.Group(
                T._("Where it goes"),
                help: T._("Applications in your home folder is the usual place: easy to find, only yours, no password " +
                    "needed. A hidden folder such as .local/opt keeps your home tidy but is harder to find again. " +
                    "A folder on a removable disk works, but the app is gone whenever the disk is unplugged. " +
                    "Folders for everyone on the computer, such as /opt, need administrator rights, which I never " +
                    "use. You can change the usual place for all apps in the settings."));
            where = Adw.ActionRow.New();
            where.UseMarkup = false;
            where.SubtitleLines = 0;
            void ShowFolder()
            {
                where.Title = folder;
                var parts = new List<string>
                {
                    folder == ui.Host.DefaultAppsFolder ? T._("The recommended place")
                        : folder == ui.AppsFolder ? T._("Your usual place for added apps")
                        : T._("Only for this app"),
                };
                if (report.InstalledBytes is { } needs)
                    parts.Add(T.F("needs {0}", Presentation.FormatSize(needs)));
                if (FreeSpace(folder) is { } free)
                    parts.Add(T.F("{0} free there", Presentation.FormatSize(free)));
                where.Subtitle = string.Join(" · ", parts);
            }
            var change = Gtk.Button.NewWithLabel(T._("Change…"));
            change.Valign = Gtk.Align.Center;
            change.OnClicked += async (_, _) =>
            {
                if (await PickAsync(ui, T._("Choose where this app goes"), folderOnly: true) is { } picked)
                {
                    folder = picked;
                    ShowFolder();
                }
            };
            where.AddSuffix(change);
            ShowFolder();
            place.Add(where);

            if (report.Programs.Count > 1)
            {
                programs = Adw.ComboRow.New();
                programs.Title = T._("Program that starts it");
                programs.Subtitle = T._("I picked the most likely one. If the app doesn't start, try another.");
                programs.Model = Gtk.StringList.New(report.Programs.Select(p => p.Label).ToArray());
                place.Add(programs);
            }
            page.Add(place);

            var shortcuts = Widgets.Group(T._("Finding it later"));
            menu.Title = T._("Show in the app menu");
            menu.Subtitle = T._("So you can start it like any other app.");
            menu.Active = true;
            shortcuts.Add(menu);
            if (!downloaded)
            {
                move.Title = report.Kind == BundleKind.Archive
                    ? T._("Move the archive to the Trash afterwards")
                    : T._("Move the file instead of copying it");
                move.Subtitle = T.F("Otherwise {0} stays where it is.", report.FileName);
                shortcuts.Add(move);
            }
            page.Add(shortcuts);
        }
        else
        {
            var place = Widgets.Group(T._("Where it goes"));
            place.Add(Widgets.Paragraph(report.Kind == BundleKind.Package
                ? T._("A package has no folder to choose: your distribution decides where each of its files goes, and it brings its own app-menu entry.")
                : T._("Flatpak keeps its apps in a folder of its own in your home folder and adds the app-menu entry itself.")));
            page.Add(place);
        }

        var go = Gtk.Button.NewWithLabel(T._("Add this app…"));
        go.AddCssClass("suggested-action");
        go.AddCssClass("pill");
        go.Halign = Gtk.Align.Center;
        var action = Adw.PreferencesGroup.New();
        action.Add(go);
        page.Add(action);

        // Checks what was pasted against the file, afresh on every change.
        var round = 0;
        async void CheckHash()
        {
            var mine = ++round;
            var (published, problem) = HashCheck.Parse(pasted.GetText(), report.FileName);
            hashVerified = false;
            hashDiffers = false;
            if (published is null)
            {
                verdictIcon.SetFromIconName(problem is null ? "dialog-question-symbolic" : "dialog-warning-symbolic");
                verdict.Title = problem is null ? T._("Not checked") : T._(problem);
                verdict.Subtitle = problem is null
                    ? T._("Without the publisher's hash I can tell that the file is complete, not that it is the real thing.")
                    : "";
                go.Sensitive = true;
                return;
            }

            verdict.Title = T._("Comparing…");
            verdict.Subtitle = "";
            go.Sensitive = false;
            bool matches;
            try
            {
                matches = await HashCheck.MatchesAsync(report.SourcePath, published, CancellationToken.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (mine != round)
                    return;
                verdict.Title = T._("I couldn't read the file to compare it.");
                return;
            }
            if (mine != round)
                return;

            hashVerified = matches;
            hashDiffers = !matches;
            go.Sensitive = matches;
            verdictIcon.SetFromIconName(matches ? "emblem-ok-symbolic" : "dialog-error-symbolic");
            verdict.Title = matches
                ? T.F("Matches the {0} hash you pasted", published.KindName)
                : T._("Does not match: this is not the file the publisher describes");
            verdict.Subtitle = !matches
                ? T._("Don't install it. Download it again from the publisher's own website, and check that you copied the hash for the right file and version.")
                : published.Weak
                    ? T.F("{0} is an old kind of hash that someone determined could fake. If the publisher also gives a SHA-256, use that.", published.KindName)
                    : T._("You have exactly the file the publisher describes.");
        }
        pasted.OnChanged += (_, _) => CheckHash();
        CheckHash();

        go.OnClicked += async (_, _) =>
        {
            if (hashDiffers)
                return;
            var choices = new IntakeChoices(name.GetText(), folder)
            {
                IconPath = iconPath,
                Program = programs is not null && programs.Selected < report.Programs.Count
                    ? report.Programs[(int)programs.Selected].Path
                    : null,
                AppMenu = menu.Active,
                DeleteOriginal = downloaded || move.Active,
                HashVerified = hashVerified,
            };
            ChangePlan plan;
            try
            {
                plan = await ui.Host.Intake.PlanAsync(report, choices, CancellationToken.None);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ui.Toast(T.F("Hmm, I couldn't work that out: {0}", AddSourcePages.Short(e.Message)));
                return;
            }
            ChangeDialog.Confirm(ui, T.F("Add {0}?", plan.Name), T._("Add"), plan, async () =>
            {
                onAdded();
                var done = await ui.Host.Make.AddAppAsync(ui.Host.Intake, report, choices, plan);
                if (done.Outcome != ChangeOutcome.Done)
                    ui.Host.Intake.Discard(report);
                if (downloaded)
                    ui.Host.Downloader.Discard(report.SourcePath);
                ChangeDialog.Report(ui, done, T.F("{0} is added. You find it under My Apps and in your app menu.", plan.Name));
            });
        };

        return page;
    }

    private static long? FreeSpace(string folder)
    {
        try
        {
            var existing = folder;
            while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
                existing = parent;
            return new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Lets the user pick a file or a folder; null when they closed the dialog.</summary>
    internal static async Task<string?> PickAsync(Ui ui, string title, bool folderOnly)
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.Title = title;
            var chosen = folderOnly ? await dialog.SelectFolderAsync(ui.Window) : await dialog.OpenAsync(ui.Window);
            return chosen?.GetPath();
        }
        catch (GLib.GException)
        {
            return null;
        }
    }
}
