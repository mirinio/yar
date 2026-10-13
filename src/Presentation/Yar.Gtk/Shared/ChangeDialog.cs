using Yar.Core;

namespace Yar.App;

/// <summary>
/// The preview before an install or a removal: everything that will come or go, what it costs in
/// space, what to be aware of. Nothing happens until the user has seen this and said yes.
/// </summary>
internal static class ChangeDialog
{
    public static void Install(Ui ui, StoreApp app, StoreOffer offer)
    {
        if (ui.Host.Ability<IAppInstaller>(offer.ProviderId) is not { } installer ||
            installer.TargetsFor(offer, ui.Snapshot.Sources) is not { Count: > 0 } targets)
        {
            ui.Toast(T._("I can't install this one yet."));
            return;
        }

        Present(
            ui, T.F("Install {0}?", app.Name), T._("Install"), destructive: false, targets,
            (target, ct) => installer.PlanInstallAsync(offer, target!, app.Name, ct),
            async (plan, _) => Report(ui, await ui.Host.Make.InstallAsync(plan), T.F("{0} is installed.", app.Name)));
    }

    public static void Remove(Ui ui, AppInfo app)
    {
        if (ui.Host.Ability<IAppRemover>(app.ProviderId) is not { } remover)
            return;

        Present(
            ui, T.F("Remove {0}?", app.Name), T._("Remove"), destructive: true, targets: [],
            (_, ct) => remover.PlanRemovalAsync(app, ct),
            async (plan, removeData) => Report(
                ui, await ui.Host.Make.RemoveAsync(plan, removeData), T.F("{0} is removed. You can undo this under History.", app.Name)));
    }

    /// <summary>Reverses a change from the history, after showing what that comes down to.</summary>
    public static async void Undo(Ui ui, ChangeRecord record, Gtk.Button button)
    {
        button.Sensitive = false;
        UndoStep step;
        try
        {
            step = await ui.Host.Make.PlanUndoAsync(record, ui.Snapshot, CancellationToken.None);
        }
        // Whatever stops the preview from being worked out, nothing was changed; say why.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            button.Sensitive = true;
            ui.Toast(T.F("I can't undo that: {0}", AddSourcePages.Short(e.Message)));
            return;
        }
        button.Sensitive = true;

        async void Run() =>
            Report(ui, await ui.Host.Make.UndoAsync(step, ui.Snapshot), T._("Done. That change is undone."));

        if (step.Plan is null)
        {
            Widgets.Confirm(ui, T._("Undo this?"), T._(step.Summary), T._("Undo"), destructive: false, Run);
            return;
        }
        Present(
            ui, T._("Undo this?"), T._("Undo"), destructive: record.Kind is ChangeKind.InstallApp or ChangeKind.AddApp,
            targets: [], (_, _) => Task.FromResult(step.Plan), (_, _) => Run(), intro: T._(step.Summary));
    }

    private static void Report(Ui ui, ChangeRecord done, string success)
    {
        ui.Toast(done.Outcome switch
        {
            ChangeOutcome.Done => success,
            ChangeOutcome.Cancelled => T._("Cancelled. Nothing more was changed."),
            _ => T.F("Hmm, that didn't work: {0}", AddSourcePages.Short(done.Problem ?? "")),
        });
        ui.Refresh();
    }

    private static void Present(
        Ui ui, string heading, string yes, bool destructive, IReadOnlyList<InstallTarget> targets,
        Func<InstallTarget?, CancellationToken, Task<ChangePlan>> makePlan, Action<ChangePlan, bool> confirmed,
        string? intro = null)
    {
        var dialog = Adw.Dialog.New();
        dialog.Title = heading;
        dialog.ContentWidth = 520;
        dialog.ContentHeight = 560;

        var cancel = Gtk.Button.NewWithLabel(T._("Cancel"));
        var confirm = Gtk.Button.NewWithLabel(yes);
        confirm.AddCssClass(destructive ? "destructive-action" : "suggested-action");
        confirm.Sensitive = false;

        var header = Adw.HeaderBar.New();
        header.ShowStartTitleButtons = false;
        header.ShowEndTitleButtons = false;
        header.PackStart(cancel);
        header.PackEnd(confirm);

        var content = Gtk.Stack.New();
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(header);
        toolbar.Content = content;
        dialog.Child = toolbar;

        var target = targets.Count > 0 ? targets[0] : null;
        ChangePlan? plan = null;
        Adw.SwitchRow? dataSwitch = null;
        // Ends a preview still being worked out when the dialog closes; disposed there, not when this method returns.
        var closed = new CancellationTokenSource();
        var token = closed.Token;
        var round = 0;

        // async void: every outcome is shown inside the dialog.
        async void Work()
        {
            var mine = ++round;
            plan = null;
            confirm.Sensitive = false;
            var waiting = Adw.StatusPage.New();
            waiting.Title = T._("Working out what this involves…");
            var spinner = Gtk.Spinner.New();
            spinner.Spinning = true;
            spinner.SetSizeRequest(32, 32);
            spinner.Halign = Gtk.Align.Center;
            waiting.Child = spinner;
            Swap(content, waiting);

            ChangePlan made;
            try
            {
                made = await makePlan(target, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // Working out a preview asks backends and the network; whatever fails, nothing was changed.
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (mine != round)
                    return;
                var failed = Adw.StatusPage.New();
                failed.IconName = "dialog-warning-symbolic";
                failed.Title = T._("Hmm, I couldn't work that out");
                failed.Description = Widgets.Escape(AddSourcePages.Short(e.Message));
                Swap(content, failed);
                return;
            }
            // The user picked another place while this was being worked out.
            if (mine != round)
                return;

            plan = made;
            (var page, dataSwitch) = Describe(ui, made, targets, target, intro, chosen =>
            {
                target = chosen;
                Work();
            });
            Swap(content, page);
            confirm.Sensitive = made.Blocked is null;
        }

        cancel.OnClicked += (_, _) => dialog.Close();
        confirm.OnClicked += (_, _) =>
        {
            if (plan is not { Blocked: null } agreed)
                return;
            var removeData = dataSwitch?.Active == true;
            dialog.Close();
            confirmed(agreed, removeData);
        };
        dialog.OnClosed += (_, _) =>
        {
            round = -1;
            closed.Cancel();
            closed.Dispose();
        };

        Work();
        dialog.Present(ui.Window);
    }

    private static void Swap(Gtk.Stack stack, Gtk.Widget child)
    {
        var old = stack.VisibleChild;
        stack.AddChild(child);
        stack.VisibleChild = child;
        if (old is not null)
            stack.Remove(old);
    }

    private static (Adw.PreferencesPage Page, Adw.SwitchRow? DataSwitch) Describe(
        Ui ui, ChangePlan plan, IReadOnlyList<InstallTarget> targets, InstallTarget? target, string? intro,
        Action<InstallTarget> retarget)
    {
        var page = Adw.PreferencesPage.New();
        Adw.SwitchRow? dataSwitch = null;

        if (plan.Blocked is { } blocked)
        {
            var refused = Widgets.Group(T._("I won't do this"));
            refused.Add(Note("dialog-warning-symbolic", T._(blocked)));
            page.Add(refused);
        }
        else if (intro is not null)
        {
            var what = Widgets.Group();
            what.Add(Note("edit-undo-symbolic", intro));
            page.Add(what);
        }

        if (plan.Warnings.Count > 0)
        {
            var warnings = Widgets.Group(T._("Before you say yes"));
            foreach (var warning in plan.Warnings)
                warnings.Add(Note("dialog-warning-symbolic", T._(warning)));
            page.Add(warnings);
        }

        if (targets.Count > 0 && target is not null)
        {
            var where = Widgets.Group(T._("Where it goes"));
            if (targets.Count == 1)
            {
                where.Add(Note("folder-symbolic", $"{T._(target.Label)}\n{T._(target.Explanation)}"));
            }
            else
            {
                var choice = Adw.ComboRow.New();
                choice.Title = T._("Install");
                choice.Model = Gtk.StringList.New(targets.Select(t => T._(t.Label)).ToArray());
                choice.Selected = (uint)Math.Max(0, targets.ToList().IndexOf(target));
                choice.Subtitle = T._(target.Explanation);
                choice.SubtitleLines = 0;
                choice.OnNotify += (_, args) =>
                {
                    if (args.Pspec.GetName() == "selected" && choice.Selected < targets.Count &&
                        targets[(int)choice.Selected] != target)
                    {
                        retarget(targets[(int)choice.Selected]);
                    }
                };
                where.Add(choice);
            }
            page.Add(where);
        }

        if (plan.Items.Count > 0)
        {
            var sizes = new List<string>();
            if (plan.DownloadBytes is { } download)
                sizes.Add(T.F("{0} to download", Presentation.FormatSize(download)));
            if (plan.DiskBytes is { } disk)
                sizes.Add(T.F("{0} on your disk", Presentation.FormatSize(disk)));
            var items = Widgets.Group(
                T._("What this involves"),
                sizes.Count == 0 ? null : string.Join(" · ", sizes));
            foreach (var item in plan.Items)
            {
                var row = Adw.ActionRow.New();
                row.UseMarkup = false;
                row.Title = item.Name;
                if (item.Detail is not null)
                    row.Subtitle = T._(item.Detail);
                row.SubtitleLines = 0;
                if (item.Bytes is { } bytes)
                {
                    var size = Gtk.Label.New(Presentation.FormatSize(bytes));
                    size.AddCssClass("dim-label");
                    row.AddSuffix(size);
                }
                items.Add(row);
            }
            page.Add(items);
        }

        if (plan.DataPath is not null && plan.Blocked is null)
        {
            var data = Widgets.Group(
                T._("Its settings and data"),
                help: T._("An app keeps what you made and set up in it apart from the program. Left alone, it is " +
                    "there again if you install the app later, and it shows up under Storage as a leftover. " +
                    "If you have me remove it, it goes to your Trash, not away for good."));
            dataSwitch = Adw.SwitchRow.New();
            dataSwitch.Title = T._("Also move its data to the Trash");
            dataSwitch.Subtitle = plan.DataBytes is { } dataBytes
                ? $"{plan.DataPath}\n{Presentation.FormatSize(dataBytes)}"
                : plan.DataPath;
            dataSwitch.SubtitleLines = 0;
            dataSwitch.Active = false;
            data.Add(dataSwitch);
            page.Add(data);
        }

        var notes = plan.Notes.ToList();
        if (plan.NeedsPassword)
            notes.Add("Your system will ask for your password. I never see it.");
        if (notes.Count > 0)
        {
            var good = Widgets.Group(T._("Good to know"));
            foreach (var note in notes)
                good.Add(Note("dialog-information-symbolic", T._(note)));
            page.Add(good);
        }

        if (ui.Technical && plan.Technical.Count > 0)
        {
            var technical = Widgets.Group(T._("Technical details"));
            foreach (var (key, value) in plan.Technical.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                technical.Add(Widgets.Property(key, value));
            page.Add(technical);
        }
        return (page, dataSwitch);
    }

    private static Adw.ActionRow Note(string icon, string text)
    {
        var row = Adw.ActionRow.New();
        row.UseMarkup = false;
        row.Title = text;
        row.TitleLines = 0;
        row.AddPrefix(Gtk.Image.NewFromIconName(icon));
        return row;
    }
}
