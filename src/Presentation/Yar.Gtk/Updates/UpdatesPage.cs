using System.Diagnostics;
using Yar.Core;

namespace Yar.App;

/// <summary>"Updates": what is waiting to be installed, one by one or all at once.</summary>
internal sealed class UpdatesPage
{
    // Programs that can install updates, with the arguments that open their updates view.
    private static readonly (string Path, string Name, string[] Arguments)[] Updaters =
    [
        ("/usr/bin/plasma-discover", "Discover", ["--mode", "update"]),
        ("/usr/bin/gnome-software", "Software", ["--mode=updates"]),
    ];

    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Adw.StatusPage _upToDate = Adw.StatusPage.New();
    private Adw.PreferencesPage? _page;

    public UpdatesPage()
    {
        _upToDate.IconName = "emblem-ok-symbolic";
        _upToDate.Title = T._("Everything is up to date");
        _stack.AddNamed(_upToDate, "up-to-date");
    }

    public Gtk.Widget Widget => _stack;

    public void Show(Ui ui)
    {
        if (_page is not null)
            _stack.Remove(_page);
        _page = Adw.PreferencesPage.New();
        _stack.AddNamed(_page, "list");

        var updates = ui.Snapshot.Updates;
        var failed = ui.Snapshot.Providers.Where(r => r.UpdateCheckError is not null).ToList();
        var checkedAt = ui.Snapshot.Providers
            .Where(r => r.UpdatesRefreshedAt is not null).Select(r => r.UpdatesRefreshedAt!.Value)
            .DefaultIfEmpty().Min();
        var freshness = checkedAt == default
            ? null
            : T.F("Your computer last looked for system updates {0}.", Presentation.Ago(checkedAt));

        if (updates.Count == 0 && failed.Count == 0)
        {
            _upToDate.Description = freshness;
            _stack.VisibleChildName = "up-to-date";
            return;
        }

        var appUpdates = updates.Where(u => u.AppId is not null)
            .OrderBy(u => u.Severity).ThenBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var systemUpdates = updates.Where(u => u.AppId is null).ToList();
        var security = updates.Count(u => u.Severity == UpdateSeverity.Security);

        var summary = Widgets.Group(
            T.F("{0} updates are waiting", updates.Count),
            string.Join(" ", new[]
            {
                T.F("{0} for your apps and {1} for parts of the system.", appUpdates.Count, systemUpdates.Count),
                security > 0 ? T.F("{0} of them fix security problems.", security) : null,
                freshness,
            }.Where(text => text is not null)));
        var updatable = updates.Where(u => ui.Host.Ability<IAppUpdater>(u.ProviderId) is not null).ToList();
        if (updatable.Count > 0)
        {
            var everything = Adw.ActionRow.New();
            everything.UseMarkup = false;
            everything.Title = T._("Install all of them");
            everything.Subtitle = updatable.Count == updates.Count
                ? T._("I show you everything this involves first.")
                : T.F("{0} of them I can't install myself; your software centre can.", updates.Count - updatable.Count);
            var all = Gtk.Button.NewWithLabel(T._("Update all…"));
            all.Valign = Gtk.Align.Center;
            all.AddCssClass("suggested-action");
            all.OnClicked += (_, _) => ChangeDialog.Update(
                ui, T.F(updatable.Count == 1 ? "{0} update" : "all {0} updates", updatable.Count), updatable);
            everything.AddSuffix(all);
            summary.Add(everything);
        }
        else if (Updaters.FirstOrDefault(u => File.Exists(u.Path)) is { Path: not null } updater)
        {
            var row = Adw.ActionRow.New();
            row.UseMarkup = false;
            row.Title = T._("I can't install these updates myself");
            row.Subtitle = T.F("To install them, open {0}.", updater.Name);
            var button = Gtk.Button.NewWithLabel(T.F("Open {0}", updater.Name));
            button.Valign = Gtk.Align.Center;
            button.AddCssClass("suggested-action");
            button.OnClicked += (_, _) => Launch(ui, updater.Path, updater.Arguments);
            row.AddSuffix(button);
            summary.Add(row);
        }
        foreach (var report in failed)
        {
            summary.Add(Widgets.Property(
                ui.Presentation.ProviderTitle(report.ProviderId),
                T.F("I couldn't check for updates here, so there may be more: {0}", report.UpdateCheckError ?? "")));
        }
        _page.Add(summary);

        if (appUpdates.Count > 0)
        {
            var group = Widgets.Group(T._("Your apps"));
            var theirs = appUpdates.Where(u => ui.Host.Ability<IAppUpdater>(u.ProviderId) is not null).ToList();
            if (theirs.Count > 1)
            {
                var onlyApps = Gtk.Button.NewWithLabel(T._("Update these…"));
                onlyApps.Valign = Gtk.Align.Center;
                onlyApps.TooltipText = T._("Only your apps, without the system updates below");
                onlyApps.OnClicked += (_, _) => ChangeDialog.Update(ui, T.F("{0} app updates", theirs.Count), theirs);
                group.HeaderSuffix = onlyApps;
            }
            foreach (var update in appUpdates)
                group.Add(UpdateRow(ui, update, offerUpdate: true));
            _page.Add(group);
        }

        if (systemUpdates.Count > 0)
        {
            var group = Widgets.Group(
                T._("System updates"),
                help: T._("These are not apps but parts of the system that apps rely on: drivers, building blocks " +
                    "shared between apps, and the system itself. You rarely notice them, but they matter, " +
                    "especially the security fixes."));
            foreach (var kind in systemUpdates.GroupBy(u => u.ProviderId))
            {
                var expander = Adw.ExpanderRow.New();
                expander.UseMarkup = false;
                expander.Title = T.F("{0}: {1} updates", ui.Presentation.ProviderTitle(kind.Key), kind.Count());
                expander.Subtitle = string.Join(" · ", kind.GroupBy(u => u.Severity).OrderBy(g => g.Key)
                    .Select(g => $"{g.Count()} × {Presentation.SeverityName(g.Key)}"));
                foreach (var update in kind.OrderBy(u => u.Severity).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
                    expander.AddRow(UpdateRow(ui, update));
                group.Add(expander);
            }
            _page.Add(group);
        }

        _stack.VisibleChildName = "list";
    }

    private static Adw.ActionRow UpdateRow(Ui ui, UpdateInfo update, bool offerUpdate = false)
    {
        var row = Adw.ActionRow.New();
        row.UseMarkup = false;
        row.Title = update.Name;
        row.Subtitle = (update.CurrentVersion, update.NewVersion) switch
        {
            ({ } current, { } next) => $"{current} → {next}",
            (null, { } next) => next,
            _ => "",
        };
        row.AddSuffix(Widgets.Tag(
            Presentation.SeverityName(update.Severity), attention: update.Severity == UpdateSeverity.Security));

        if (offerUpdate && ui.Host.Ability<IAppUpdater>(update.ProviderId) is not null)
        {
            var one = Gtk.Button.NewWithLabel(T._("Update"));
            one.Valign = Gtk.Align.Center;
            one.OnClicked += (_, _) => ChangeDialog.Update(ui, update.Name, [update]);
            row.AddSuffix(one);
        }

        if (ui.Presentation.AppFor(update) is { } app)
        {
            row.AddPrefix(Presentation.IconFor(app, 32));
            row.Activatable = true;
            row.AddSuffix(Gtk.Image.NewFromIconName("go-next-symbolic"));
            row.OnActivated += (_, _) => ui.ShowApp(app);
        }
        return row;
    }

    private static void Launch(Ui ui, string program, string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            ui.Toast(T._("Hmm, I couldn't open that."));
        }
    }
}
