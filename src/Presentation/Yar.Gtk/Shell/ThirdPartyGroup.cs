using Yar.Core;
using Yar.Hosting;

namespace Yar.App;

/// <summary>
/// The settings group for the distribution's opt-in to software it does not make itself.
/// One switch, a short line of context, and the longer explanation behind a "?".
/// </summary>
internal static class ThirdPartyGroup
{
    public static Adw.PreferencesGroup Create(
        YarHost host, CatalogSnapshot? snapshot, string distro, Gtk.Widget parent, Action<string> toast, Action refresh)
    {
        var group = Widgets.Group(
            T.F("Software from outside {0}", distro),
            T.F("{0} prepared a few well-known sources for software it does not make itself, and leaves the choice to you.", distro),
            Explanation(distro),
            helpWidthChars: 54);

        var choice = Adw.ActionRow.New();
        choice.UseMarkup = false;
        choice.Title = T._("Allow these sources");
        choice.Subtitle = T._("Checking what you chose…");
        var toggle = Gtk.Switch.New();
        toggle.Valign = Gtk.Align.Center;
        toggle.Sensitive = false;
        choice.AddSuffix(toggle);
        choice.ActivatableWidget = toggle;
        group.Add(choice);

        var covered = Adw.ExpanderRow.New();
        covered.UseMarkup = false;
        covered.Title = T._("What this switches");
        covered.Subtitle = T._("Saying yes installs nothing. It only makes this software available.");
        group.Add(covered);

        _ = Fill(host, snapshot, distro, parent, choice, toggle, covered, toast, refresh);
        return group;
    }

    private static async Task Fill(
        YarHost host, CatalogSnapshot? snapshot, string distro, Gtk.Widget parent, Adw.ActionRow choice,
        Gtk.Switch toggle, Adw.ExpanderRow coveredRow, Action<string> toast, Action refresh)
    {
        ThirdPartyStatus status;
        try
        {
            status = await host.ThirdParty.QueryAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            choice.Subtitle = T._("Hmm, I couldn't find out what is set.");
            return;
        }

        // Ask afresh which sources are on: what the window last loaded may be a moment out of date,
        // and this group must not report a source as off when it is on.
        IReadOnlyList<SourceInfo> sources = snapshot?.Sources ?? [];
        try
        {
            sources = (await host.Catalog.LoadAppsAsync(CancellationToken.None)).Sources;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException)
        {
        }

        SourceInfo? Find((string Name, string Kind) covered) => sources.FirstOrDefault(s =>
            covered.Kind == "dnf" ? s.ProviderId == "packagekit" && s.Id == covered.Name
            : s.ProviderId == "flatpak" && s.Technical.TryGetValue("remote", out var remote) && remote == covered.Name);
        string Friendly(string name) => Find(status.Sources.First(s => s.Name == name))?.Name ?? name;

        // What the sources are really switched to, which is not always what the recorded answer says.
        var summary = ThirdPartySummary.From(status, covered => Find(covered)?.Enabled);

        var states = new List<Gtk.Label>();
        foreach (var covered in status.Sources)
        {
            var known = Find(covered);
            var row = Adw.ActionRow.New();
            row.UseMarkup = false;
            row.Title = known?.Name ?? covered.Name;
            row.Subtitle = covered.Kind == "flatpak"
                ? T._("Flatpak source. With a yes, the whole catalogue becomes available, not just a selection.")
                : T._("System package source");
            var state = Widgets.Tag("");
            SetState(state, known is null ? null : known.Enabled);
            states.Add(state);
            row.AddSuffix(state);
            coveredRow.AddRow(row);
        }

        var shownOn = summary.ShownOn;
        var recorded = summary.Recorded;
        var settingBack = false;
        void Show()
        {
            settingBack = true;
            toggle.Active = shownOn;
            settingBack = false;

            var answer = recorded switch
            {
                ThirdPartyChoice.Allowed => T._("You said yes."),
                ThirdPartyChoice.Declined => T._("You said no."),
                _ => T.F("{0} has no answer from you on record, so I am showing what is actually set.", distro),
            };
            // Only worth a sentence when reality and the answer do not line up.
            var reality = !summary.Mixed && recorded != ThirdPartyChoice.NotChosen ? null
                : summary.Off.Count == 0 ? T._("All of these sources are on.")
                : summary.On.Count == 0 ? T._("All of these sources are off.")
                : T.F("{0} of {1} are on: {2}.", summary.On.Count, summary.On.Count + summary.Off.Count,
                    string.Join(", ", summary.On.Select(Friendly)));
            choice.Subtitle = string.Join(" ", new[] { answer, reality, T._("Changing this asks for your password.") }
                .Where(part => part is not null));
        }
        Show();
        toggle.Sensitive = true;

        toggle.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() != "active" || settingBack || toggle.Active == shownOn)
                return;

            // Say exactly what is about to change before the password prompt appears.
            var wanted = toggle.Active;
            Widgets.Confirm(
                parent,
                wanted ? T._("Allow these sources?") : T._("Turn these sources off?"),
                ChangePreview(summary, wanted, Friendly),
                wanted ? T._("Allow") : T._("Turn off"), destructive: false,
                confirmed: () => Change(wanted),
                cancelled: Show);
        };

        // async void: every outcome, including failure, is shown to the user inside.
        async void Change(bool wanted)
        {
            toggle.Sensitive = false;
            try
            {
                await host.ThirdParty.SetAllowedAsync(wanted, CancellationToken.None);
                recorded = wanted ? ThirdPartyChoice.Allowed : ThirdPartyChoice.Declined;
                shownOn = wanted;
                // The tool has just switched every covered source to match the answer.
                summary = wanted
                    ? summary with { On = [.. summary.On, .. summary.Off], Off = [] }
                    : summary with { Off = [.. summary.On, .. summary.Off], On = [] };
                foreach (var state in states)
                    SetState(state, wanted);
                toast(wanted
                    ? T._("Done. Those sources are now on; their apps will show up in the Store.")
                    : T._("Done. Those sources are now off."));
                refresh();
            }
            catch (OperationCanceledException)
            {
                toast(T._("Nothing was changed."));
            }
            catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
            {
                toast(T.F("Hmm, that didn't work: {0}", AddSourcePages.Short(e.Message)));
            }
            Show();
            toggle.Sensitive = true;
        }
    }

    /// <summary>What flipping the switch will do, source by source, so nothing changes by surprise.</summary>
    internal static string ChangePreview(ThirdPartySummary summary, bool allow, Func<string, string> friendly)
    {
        var effect = summary.Effect(allow);
        var changing = effect.Changing.Select(friendly).ToList();
        var already = effect.Already.Select(friendly).ToList();
        var lines = new List<string>();
        if (changing.Count > 0)
        {
            lines.Add(allow
                ? T.F("This will switch on: {0}.", string.Join(", ", changing))
                : T.F("This will switch off: {0}.", string.Join(", ", changing)));
        }
        if (already.Count > 0)
        {
            lines.Add(allow
                ? T.F("Already on: {0}.", string.Join(", ", already))
                : T.F("Already off: {0}.", string.Join(", ", already)));
        }
        lines.Add(allow
            ? T._("Nothing is installed by this. Your system will ask for your password.")
            : T._("Apps you already installed from these sources stay, but stop getting updates. Your system will ask for your password."));
        return string.Join("\n\n", lines);
    }

    private static void SetState(Gtk.Label tag, bool? on)
    {
        tag.SetText(on switch { true => T._("On"), false => T._("Off"), null => T._("Not set up") });
        if (on == true)
            tag.AddCssClass("yar-good");
        else
            tag.RemoveCssClass("yar-good");
    }

    private static string Explanation(string distro) => T.F(
        "Software comes in three kinds.\n\n" +
        "Open source: anyone can read how it works, check it and improve it. Almost everything in {0} is like this, " +
        "and {0}'s own people look after it.\n\n" +
        "Closed source (also called proprietary): only its maker can see inside. Google Chrome, Steam and the " +
        "NVIDIA graphics driver are examples. It can be perfectly good software, but nobody outside can check it.\n\n" +
        "Mixed: open at its core with closed parts added, or open but not allowed everywhere for legal reasons, " +
        "like some video formats.\n\n" +
        "{0} only ships what it can inspect and may legally pass on. It will not quietly switch on sources for " +
        "the other two kinds, because then you would be trusting someone else without knowing it. So it asks " +
        "you, once: yes or no.\n\n" +
        "Neither answer is wrong. Yes is convenient if you want apps like Chrome or Steam. No keeps your system " +
        "to what {0} checks itself. You can change your mind here at any time.", distro);
}
