using System.Text.RegularExpressions;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>
/// Pins launchers on Plasma's task manager and on GNOME's dash, the two desktops that offer a
/// way to do so from outside. Each keeps its own list; an entry is added to it or taken out,
/// and the rest of the list is written back exactly as it was read.
/// </summary>
public sealed partial class DesktopPinner(IProcessRunner runner, IReadOnlyList<string> currentDesktops) : IAppPinner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly string[] Gdbus = ["/usr/bin/gdbus", "/bin/gdbus"];
    private static readonly string[] Gsettings = ["/usr/bin/gsettings", "/bin/gsettings"];

    // Runs inside Plasma. It changes one list, and only if that list could be read as a list.
    private const string PlasmaScript = """
        var id = "applications:%ID%";
        var add = %ADD%;
        var found = 0, changed = 0, unreadable = 0;
        var all = panels();
        for (var p = 0; p < all.length; p++) {
            var widgets = all[p].widgets();
            for (var w = 0; w < widgets.length; w++) {
                var widget = widgets[w];
                if (widget.type !== "org.kde.plasma.icontasks" && widget.type !== "org.kde.plasma.taskmanager") continue;
                found++;
                widget.currentConfigGroup = ["General"];
                var read = widget.readConfig("launchers");
                var list;
                if (typeof read === "string") list = read.length > 0 ? read.split(",") : [];
                else if (read && typeof read.length === "number") { list = []; for (var i = 0; i < read.length; i++) list.push(String(read[i])); }
                else { unreadable++; continue; }
                var at = list.indexOf(id);
                if (add && at < 0) { list.push(id); widget.writeConfig("launchers", list); changed++; }
                if (!add && at >= 0) { list.splice(at, 1); widget.writeConfig("launchers", list); changed++; }
            }
        }
        print("yar-pin found=" + found + " changed=" + changed + " unreadable=" + unreadable);
        """;

    private bool IsPlasma => currentDesktops.Contains("KDE", StringComparer.OrdinalIgnoreCase);

    private bool IsGnome => currentDesktops.Any(d => d.Contains("GNOME", StringComparison.OrdinalIgnoreCase));

    public string? WhyNot => IsPlasma || IsGnome
        ? null
        : "I only know how to pin apps on Plasma and GNOME. On this desktop, pin it yourself from the app menu.";

    public Task PinAsync(string desktopId, CancellationToken ct) => ChangeAsync(desktopId, add: true, ct);

    public Task UnpinAsync(string desktopId, CancellationToken ct) => ChangeAsync(desktopId, add: false, ct);

    private async Task ChangeAsync(string desktopId, bool add, CancellationToken ct)
    {
        if (WhyNot is { } reason)
            throw new InvalidOperationException(reason);
        // The name goes into a script and a settings value, so only plain names are accepted.
        if (!PlainDesktopId().IsMatch(desktopId))
            throw new InvalidOperationException("That launcher has a name I won't pass on to the desktop.");

        if (IsPlasma)
            await ChangePlasmaAsync(desktopId, add, ct).ConfigureAwait(false);
        else
            await ChangeGnomeAsync(desktopId, add, ct).ConfigureAwait(false);
    }

    private async Task ChangePlasmaAsync(string desktopId, bool add, CancellationToken ct)
    {
        var script = PlasmaScript.Replace("%ID%", desktopId, StringComparison.Ordinal)
            .Replace("%ADD%", add ? "true" : "false", StringComparison.Ordinal);
        var result = await runner.RunAsync(
            Gdbus.FirstOrDefault(File.Exists) ?? Gdbus[0],
            ["call", "--session", "--dest", "org.kde.plasmashell", "--object-path", "/PlasmaShell",
                "--method", "org.kde.PlasmaShell.evaluateScript", script],
            Timeout, ct).ConfigureAwait(false);
        var report = PlasmaReport().Match(result.StdOut);
        if (result.ExitCode != 0 || !report.Success)
            throw new InvalidOperationException("Plasma didn't let me change the taskbar. You can pin the app yourself: right-click it in the app menu.");
        if (!add)
            return;
        if (report.Groups["found"].Value == "0")
            throw new InvalidOperationException("I found no taskbar on your panels to pin it to.");
        if (report.Groups["unreadable"].Value != "0" && report.Groups["changed"].Value == "0")
            throw new InvalidOperationException("I couldn't read your taskbar's list of pinned apps, so I left it alone. You can pin the app yourself: right-click it in the app menu.");
    }

    private async Task ChangeGnomeAsync(string desktopId, bool add, CancellationToken ct)
    {
        var tool = Gsettings.FirstOrDefault(File.Exists) ?? Gsettings[0];
        var read = await runner.RunAsync(tool, ["get", "org.gnome.shell", "favorite-apps"], Timeout, ct).ConfigureAwait(false);
        if (read.ExitCode != 0 || ParseStringList(read.StdOut) is not { } list)
            throw new InvalidOperationException("I couldn't read GNOME's list of pinned apps, so I left it alone.");

        if (add == list.Contains(desktopId))
            return;
        if (add)
            list.Add(desktopId);
        else
            list.Remove(desktopId);
        var written = await runner.RunAsync(
            tool, ["set", "org.gnome.shell", "favorite-apps", "[" + string.Join(", ", list.Select(item => $"'{item}'")) + "]"],
            Timeout, ct).ConfigureAwait(false);
        if (written.ExitCode != 0)
            throw new InvalidOperationException("GNOME didn't accept the change to its pinned apps.");
    }

    /// <summary>
    /// Reads a list as gsettings prints it: <c>['a.desktop', 'b.desktop']</c> or <c>@as []</c>.
    /// Null when it holds anything that could not be written back unchanged.
    /// </summary>
    public static List<string>? ParseStringList(string printed)
    {
        var text = printed.Trim();
        if (text.StartsWith("@as ", StringComparison.Ordinal))
            text = text["@as ".Length..].Trim();
        if (text.Length < 2 || text[0] != '[' || text[^1] != ']')
            return null;
        var inner = text[1..^1].Trim();
        if (inner.Length == 0)
            return [];

        var items = new List<string>();
        foreach (var raw in inner.Split(','))
        {
            var item = raw.Trim();
            if (item.Length < 2 || item[0] != '\'' || item[^1] != '\'' || item[1..^1].Any(c => c is '\'' or '\\' or '"'))
                return null;
            items.Add(item[1..^1]);
        }
        return items;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.desktop$")]
    private static partial Regex PlainDesktopId();

    [GeneratedRegex(@"yar-pin found=(?<found>\d+) changed=(?<changed>\d+) unreadable=(?<unreadable>\d+)")]
    private static partial Regex PlasmaReport();
}
