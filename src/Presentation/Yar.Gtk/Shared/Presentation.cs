using System.Globalization;
using Yar.Core;
using Yar.Metadata;

namespace Yar.App;

/// <summary>Turns catalog data into the words and pictures the user sees.</summary>
internal sealed class Presentation(CatalogSnapshot snapshot, OsRelease os)
{
    private const long MaxIconFileBytes = 5 * 1024 * 1024;
    private const string FallbackIcon = "application-x-executable";

    private readonly Dictionary<(string Provider, string Id), SourceInfo> _sources =
        snapshot.Sources.ToDictionary(source => (source.ProviderId, source.Id));

    public SourceInfo? SourceOf(AppInfo app) =>
        app.SourceId is not null && _sources.TryGetValue((app.ProviderId, app.SourceId), out var source) ? source : null;

    /// <summary>Where an app came from, in words a newcomer understands.</summary>
    public string OriginName(AppInfo app)
    {
        if (SourceOf(app) is { } source)
        {
            // "Fedora" and "Fedora updates" are one origin as far as the user is concerned.
            return source.Trust == TrustLevel.Distro && source.ProviderId == "packagekit" && os.Name is not null
                ? os.Name
                : source.Name;
        }

        return app.ProviderId switch
        {
            "packagekit" => T._("Other system packages"),
            "flatpak" => "Flatpak",
            "snap" => T._("Snap Store"),
            _ => T._("Downloaded yourself"),
        };
    }

    /// <summary>Sort order for origins: the system's own first, things nobody vouches for last.</summary>
    public int OriginRank(AppInfo app) => app.ProviderId == "manual"
        ? 10
        : SourceOf(app)?.Trust switch
        {
            TrustLevel.Distro => 0,
            TrustLevel.WellKnown => 1,
            TrustLevel.ThirdParty => 2,
            _ => 3,
        };

    public static string TrustText(TrustLevel trust) => trust switch
    {
        TrustLevel.Distro => T._("Part of your system"),
        TrustLevel.WellKnown => T._("Well known"),
        TrustLevel.ThirdParty => T._("Third party"),
        _ => T._("I don't know this source"),
    };

    public string ProviderTitle(string providerId) => providerId switch
    {
        "packagekit" => os.Name is not null ? T.F("{0} packages", os.Name) : T._("System packages"),
        "flatpak" => "Flatpak",
        "snap" => "Snap",
        _ => T._("Your own downloads"),
    };

    public static string ProviderDescription(string providerId) => providerId switch
    {
        "packagekit" => T._("Apps and system parts from your Linux distribution"),
        "flatpak" => T._("Apps that work on every Linux distribution"),
        "snap" => T._("Apps from the Snap Store"),
        _ => T._("Things you set up by hand"),
    };

    public string DistroName => os.Name ?? T._("your distribution");

    public SourceInfo? SourceOf(StoreOffer offer) =>
        _sources.TryGetValue((offer.ProviderId, offer.SourceId), out var source) ? source : null;

    /// <summary>How well Yar knows the source behind an offer. An offer from a source it cannot find is unknown.</summary>
    public TrustLevel TrustOf(StoreOffer offer) => SourceOf(offer)?.Trust ?? TrustLevel.Unknown;

    public IReadOnlyList<AppInfo> AppsOf(SourceInfo source) => snapshot.Apps
        .Where(app => app.ProviderId == source.ProviderId && app.SourceId == source.Id)
        .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public IReadOnlyList<AppInfo> AppsOf(string providerId) => snapshot.Apps
        .Where(app => app.ProviderId == providerId)
        .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public IReadOnlyList<SourceInfo> SourcesOf(string providerId, bool includeDeveloperSources) => snapshot.Sources
        .Where(source => source.ProviderId == providerId)
        // Sources that only matter to developers stay out of the way unless asked for.
        .Where(source => includeDeveloperSources || !source.Technical.ContainsKey("for-developers"))
        .OrderByDescending(source => source.Enabled)
        .ThenBy(source => source.Trust)
        .ThenBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public UpdateInfo? UpdateFor(AppInfo app) =>
        snapshot.Updates.FirstOrDefault(update => update.ProviderId == app.ProviderId && update.AppId == app.Id);

    public AppInfo? AppFor(UpdateInfo update) => update.AppId is null
        ? null
        : snapshot.Apps.FirstOrDefault(app => app.ProviderId == update.ProviderId && app.Id == update.AppId);

    public static string CategoryName(AppCategory category) => category switch
    {
        AppCategory.Games => T._("Games"),
        AppCategory.Internet => T._("Internet"),
        AppCategory.Office => T._("Office"),
        AppCategory.Graphics => T._("Graphics"),
        AppCategory.SoundAndVideo => T._("Sound & Video"),
        AppCategory.Development => T._("Development"),
        AppCategory.Education => T._("Education"),
        AppCategory.Science => T._("Science"),
        AppCategory.Utilities => T._("Utilities"),
        AppCategory.System => T._("System"),
        AppCategory.Settings => T._("Settings"),
        _ => T._("Other"),
    };

    public static string SeverityName(UpdateSeverity severity) => severity switch
    {
        UpdateSeverity.Security => T._("Security fix"),
        UpdateSeverity.BugFix => T._("Bug fix"),
        UpdateSeverity.Enhancement => T._("Improvement"),
        _ => T._("Update"),
    };

    public static string KindIcon(string providerId) => providerId switch
    {
        "packagekit" => "package-x-generic-symbolic",
        "flatpak" => "application-x-addon-symbolic",
        "snap" => "application-x-addon-symbolic",
        _ => "user-home-symbolic",
    };

    /// <summary>The longer "what is this and what does it do" text for a kind of software.</summary>
    public string KindExplanation(string providerId) => providerId switch
    {
        "packagekit" => T.F(
            "These are the apps and system parts that {0} itself provides. They come as \"packages\": small bundles " +
            "that a package manager downloads from the sources below, installs, and keeps up to date.\n\n" +
            "Packages share parts with each other, so they are small and fit together well. That is also why " +
            "their files are spread over system folders instead of sitting in one place, and why installing " +
            "them asks for your password.",
            os.Name ?? T._("your system")),
        "flatpak" => T._(
            "Flatpak is a way to install apps that works the same on every Linux distribution. Each app comes " +
            "with everything it needs and runs in its own protected space, kept apart from the rest of your system.\n\n" +
            "That makes Flatpak apps easy to add and remove without touching anything else. They take a bit more " +
            "disk space, because apps bring their own parts along. Most Flatpak apps come from Flathub."),
        "snap" => T._(
            "Snap is another way to install apps that works on many Linux distributions. It is made by Canonical, " +
            "the company behind Ubuntu, and all snaps come from one place: the Snap Store.\n\n" +
            "Like Flatpak apps, snaps bring along what they need and are kept apart from the rest of your system. " +
            "They update themselves in the background."),
        _ => T._(
            "These are apps you set up yourself, outside of any store: a program you downloaded from a website, " +
            "an AppImage, a game shortcut, or a menu entry something created for you.\n\n" +
            "I can see them and show you where they are, but nobody keeps them up to date. If you want a newer " +
            "version, you have to get it from wherever you got the app."),
    };

    public static string TrustExplanation(TrustLevel trust) => trust switch
    {
        TrustLevel.Distro => T._(
            "This source belongs to your Linux distribution. Its makers check what goes in, and it is where your " +
            "system gets its own updates from."),
        TrustLevel.WellKnown => T._(
            "This source is not part of your distribution, but it is widely used and has people who look after it."),
        TrustLevel.ThirdParty => T._(
            "This source is run by a person or small team outside your distribution. Apps from here can change " +
            "your whole system, so only keep it if you trust who made it."),
        _ => T._(
            "This source is not on my list, so I cannot tell you who runs it. That does not mean it is bad; many " +
            "software makers offer their own source. Only keep it if you added it on purpose and trust who made it."),
    };

    public static string Ago(DateTimeOffset moment)
    {
        var elapsed = DateTimeOffset.UtcNow - moment;
        return elapsed.TotalMinutes < 2 ? T._("just now")
            : elapsed.TotalHours < 2 ? T.F("{0} minutes ago", (int)elapsed.TotalMinutes)
            : elapsed.TotalDays < 2 ? T.F("{0} hours ago", (int)elapsed.TotalHours)
            : T.F("{0} days ago", (int)elapsed.TotalDays);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "kB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes} {units[0]}")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    public static Gtk.Image IconFor(AppInfo app, int pixelSize)
    {
        var image = CreateIcon(app);
        image.PixelSize = pixelSize;
        return image;
    }

    private static Gtk.Image CreateIcon(AppInfo app)
    {
        // Icon files of manual apps are untrusted: only load regular files of a sane size.
        if (app.IconPath is not null)
        {
            try
            {
                var info = new FileInfo(app.IconPath);
                if (info.Exists && info.Length is > 0 and <= MaxIconFileBytes)
                    return Gtk.Image.NewFromFile(app.IconPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (app.IconName is not null && Gdk.Display.GetDefault() is { } display &&
            Gtk.IconTheme.GetForDisplay(display).HasIcon(app.IconName))
        {
            return Gtk.Image.NewFromIconName(app.IconName);
        }
        return Gtk.Image.NewFromIconName(FallbackIcon);
    }
}
