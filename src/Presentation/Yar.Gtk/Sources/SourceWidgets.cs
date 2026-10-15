using Yar.Core;

namespace Yar.App;

/// <summary>A source as a row in a list, with its on/off switch right there.</summary>
internal static class SourceWidgets
{
    /// <summary>
    /// The on/off switch of a source. Turning on a source nobody vouches for asks first, and a
    /// change that fails or is cancelled puts the switch back where it was.
    /// </summary>
    public static Gtk.Switch Switch(Ui ui, SourceInfo source)
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
                Widgets.Confirm(
                    ui, T.F("Turn on {0}?", source.Name), Presentation.TrustExplanation(source.Trust),
                    T._("Turn on"), destructive: false,
                    confirmed: () => SwitchSource(ui, source, wanted, toggle, Revert),
                    cancelled: Revert);
            }
            else if (!wanted && source.Trust == TrustLevel.Distro)
            {
                // The system's own sources are where security updates come from.
                Widgets.Confirm(
                    ui, T.F("Turn off {0}?", source.Name),
                    T._("Your system gets its own apps and security updates from here. With it off, you may " +
                        "stop getting updates. Only turn it off if you know why you need to."),
                    T._("Turn off"), destructive: true,
                    confirmed: () => SwitchSource(ui, source, wanted, toggle, Revert),
                    cancelled: Revert);
            }
            else
            {
                SwitchSource(ui, source, wanted, toggle, Revert);
            }
        };
        return toggle;
    }

    private static async void SwitchSource(
        Ui ui, SourceInfo source, bool enabled, Gtk.Switch toggle, Action revert)
    {
        toggle.Sensitive = false;
        var done = await ui.Host.Make.SwitchSourceAsync(source, enabled);
        if (done.Outcome == ChangeOutcome.Done)
        {
            ui.Toast(enabled ? T.F("{0} is now turned on.", source.Name) : T.F("{0} is now turned off.", source.Name));
            ui.Refresh();
            return;
        }
        // Includes the user closing the password prompt: nothing was changed.
        revert();
        toggle.Sensitive = true;
        ui.Toast(T.F("I left {0} as it was: {1}", source.Name, Widgets.Short(done.Problem ?? T._("it was cancelled"))));
    }

    public static Adw.ActionRow Row(Ui ui, SourceInfo source)
    {
        var apps = ui.Presentation.AppsOf(source).Count;
        var tags = new List<Gtk.Widget>();
        if (apps > 0)
            tags.Add(Widgets.Tag(T.F(apps == 1 ? "{0} app" : "{0} apps", apps)));
        if (source.ProviderId != "manual")
            tags.Add(Widgets.TrustTag(source.Trust));
        // The switch sits right in the list, so nobody has to open each source to find it.
        if (ui.Host.Ability<ISourceManager>(source.ProviderId) is { } manager && manager.CanToggle(source))
            tags.Add(Switch(ui, source));
        else if (!source.Enabled)
            tags.Add(Widgets.Tag(T._("Turned off")));

        var row = Widgets.Navigation(source.Name, source.Description, () => ui.ShowSource(source), [.. tags]);
        row.SubtitleLines = 2;
        if (!source.Enabled)
            row.AddCssClass("dim-label");
        return row;
    }
}
