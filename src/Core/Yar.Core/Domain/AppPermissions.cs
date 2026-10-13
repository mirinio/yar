namespace Yar.Core;

/// <summary>What a sandboxed app is allowed to reach, in the terms Flatpak uses.</summary>
public sealed record RawPermissions(
    IReadOnlyList<string> Shared,
    IReadOnlyList<string> Sockets,
    IReadOnlyList<string> Devices,
    IReadOnlyList<string> Filesystems,
    IReadOnlyList<string> SessionBus,
    IReadOnlyList<string> SystemBus)
{
    public static RawPermissions None { get; } = new([], [], [], [], [], []);
}

public enum PermissionWeight
{
    /// <summary>Ordinary things nearly every app needs, such as drawing its window.</summary>
    Minor,
    /// <summary>Real access, but to something specific: the internet, one folder, the microphone.</summary>
    Some,
    /// <summary>Access so wide that the app is hardly kept apart at all.</summary>
    Broad,
}

/// <param name="Text">A sentence for the user. <paramref name="Detail"/> names the exact thing, where that helps.</param>
public sealed record PermissionNote(PermissionWeight Weight, string Text, string? Detail = null);

public sealed record PermissionReport(PermissionWeight Verdict, IReadOnlyList<PermissionNote> Notes);

/// <summary>Turns Flatpak's permission names into sentences, and weighs how much they open up.</summary>
public static class FlatpakPermissions
{
    private static readonly Dictionary<string, string> UserFolders = new(StringComparer.Ordinal)
    {
        ["xdg-desktop"] = "Desktop",
        ["xdg-documents"] = "Documents",
        ["xdg-download"] = "Downloads",
        ["xdg-music"] = "Music",
        ["xdg-pictures"] = "Pictures",
        ["xdg-videos"] = "Videos",
        ["xdg-public-share"] = "Public",
        ["xdg-templates"] = "Templates",
    };

    public static PermissionReport Describe(RawPermissions raw)
    {
        var notes = new List<PermissionNote>();

        foreach (var entry in raw.Filesystems)
        {
            // "!home" takes access away again; it grants nothing.
            if (entry.StartsWith('!'))
                continue;
            var readOnly = entry.EndsWith(":ro", StringComparison.Ordinal);
            var path = entry.Split(':')[0];
            var verb = readOnly ? "Can read" : "Can read and change";

            if (path is "host" or "host-os" or "host-etc")
                notes.Add(new(PermissionWeight.Broad, $"{verb} all the files on this computer"));
            else if (path == "home")
                notes.Add(new(PermissionWeight.Broad, $"{verb} everything in your home folder"));
            else if (path is "xdg-config" or "xdg-data" or "xdg-cache")
                notes.Add(new(PermissionWeight.Broad, $"{verb} the settings and data of your other apps"));
            else if (UserFolders.TryGetValue(path, out var folder))
                notes.Add(new(PermissionWeight.Some, $"{verb} your {folder} folder"));
            else if (path.StartsWith("xdg-run/", StringComparison.Ordinal) || path.StartsWith("/run/", StringComparison.Ordinal))
                notes.Add(new(PermissionWeight.Minor, "Can reach a specific system service", path));
            else
                notes.Add(new(PermissionWeight.Some, $"{verb} one specific folder", path));
        }

        foreach (var device in raw.Devices)
        {
            notes.Add(device switch
            {
                "all" => new(PermissionWeight.Broad, "Can use every device, including your camera and game controllers"),
                "input" => new(PermissionWeight.Some, "Can use game controllers and other input devices"),
                "dri" => new(PermissionWeight.Minor, "Can use your graphics card"),
                _ => new(PermissionWeight.Minor, "Can use a specific kind of device", device),
            });
        }

        if (raw.Shared.Contains("network"))
            notes.Add(new(PermissionWeight.Some, "Can use the internet"));

        foreach (var socket in raw.Sockets)
        {
            switch (socket)
            {
                case "x11":
                    notes.Add(new(PermissionWeight.Some, "Can see and control other windows (it uses the older display system)"));
                    break;
                case "pulseaudio":
                    notes.Add(new(PermissionWeight.Some, "Can play sound and listen to your microphone"));
                    break;
                case "session-bus":
                case "system-bus":
                    notes.Add(new(PermissionWeight.Broad, "Can talk to every other program and system service"));
                    break;
                case "ssh-auth":
                    notes.Add(new(PermissionWeight.Some, "Can use your SSH keys to sign in to other computers"));
                    break;
                case "gpg-agent":
                    notes.Add(new(PermissionWeight.Some, "Can use your encryption keys"));
                    break;
                case "cups":
                    notes.Add(new(PermissionWeight.Minor, "Can print"));
                    break;
                case "wayland":
                case "fallback-x11":
                    notes.Add(new(PermissionWeight.Minor, "Can show its own windows"));
                    break;
                default:
                    notes.Add(new(PermissionWeight.Minor, "Can use a specific system connection", socket));
                    break;
            }
        }

        foreach (var name in raw.SessionBus)
        {
            if (name is "org.freedesktop.Flatpak" or "org.freedesktop.Flatpak.*")
                notes.Add(new(PermissionWeight.Broad, "Can start programs outside its protected space"));
            else if (name.StartsWith("org.freedesktop.secrets", StringComparison.Ordinal))
                notes.Add(new(PermissionWeight.Some, "Can use your saved passwords"));
            else
                notes.Add(new(PermissionWeight.Minor, "Can talk to another program", name));
        }

        foreach (var name in raw.SystemBus)
            notes.Add(new(PermissionWeight.Minor, "Can talk to a system service", name));

        var ordered = notes
            .DistinctBy(note => (note.Text, note.Detail))
            .OrderByDescending(note => note.Weight)
            .ToList();
        return new(ordered.Count == 0 ? PermissionWeight.Minor : ordered[0].Weight, ordered);
    }
}

/// <summary>Optional: a provider that can say what one of its installed apps is allowed to reach.</summary>
public interface IPermissionProvider
{
    /// <summary>Null when the app is not one this provider confines.</summary>
    Task<RawPermissions?> GetPermissionsAsync(AppInfo app, CancellationToken ct);
}
