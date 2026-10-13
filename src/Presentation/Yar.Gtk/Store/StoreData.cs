using Yar.Core;

namespace Yar.App;

/// <summary>What the Store pages work from: the local catalogs, plus whatever arrived from the internet.</summary>
internal sealed class StoreData(IReadOnlyList<StoreApp> apps)
{
    // Shown in the banner when Flathub's picks are switched off or cannot be fetched.
    private static readonly string[] WellKnown =
    [
        "org.mozilla.firefox", "org.videolan.VLC", "org.libreoffice.LibreOffice", "org.gimp.GIMP", "org.kde.krita",
        "org.inkscape.Inkscape", "org.mozilla.Thunderbird", "org.blender.Blender", "com.obsproject.Studio",
        "org.kde.kdenlive", "org.audacityteam.Audacity", "org.telegram.desktop", "org.signal.Signal",
        "com.bitwarden.desktop", "org.keepassxc.KeePassXC", "org.gnome.Calculator", "org.kde.okular",
        "com.github.tchx84.Flatseal", "org.shotcut.Shotcut", "org.qbittorrent.qBittorrent",
    ];

    private readonly Dictionary<string, StoreApp> _byId = apps
        .SelectMany(app => app.KnownIds.Select(id => (id, app)))
        .GroupBy(pair => pair.id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().app, StringComparer.OrdinalIgnoreCase);

    private StoreCollections _collections = StoreCollections.Empty;
    private Dictionary<string, long> _installs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<StoreApp> Apps => apps;

    public IReadOnlyDictionary<string, StarRating>? Ratings { get; set; }

    public IReadOnlyList<DevProject> DevProjects { get; set; } = [];

    public StoreCollections Collections
    {
        get => _collections;
        set
        {
            _collections = value;
            _installs = value.Trending.Concat(value.MostInstalled).Concat(value.RecentlyUpdated)
                .Where(listing => listing.InstallsLastMonth is not null)
                .GroupBy(listing => StoreCatalog.Normalize(listing.AppId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().InstallsLastMonth!.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The apps of a fetched list that the local catalogs know, in the list's order.</summary>
    public IEnumerable<StoreApp> Resolve(IEnumerable<StoreListing> listings) =>
        listings.Select(listing => _byId.GetValueOrDefault(StoreCatalog.Normalize(listing.AppId)))
            .Where(app => app is not null)
            .Select(app => app!)
            .Distinct();

    public IEnumerable<StoreApp> WellKnownApps() => Resolve(WellKnown.Select(id => new StoreListing(id)));

    /// <summary>How often an app was installed last month, when one of the fetched lists says so.</summary>
    public long? InstallsOf(StoreApp app)
    {
        foreach (var id in app.KnownIds)
        {
            if (_installs.TryGetValue(id, out var installs))
                return installs;
        }
        return null;
    }

    /// <summary>All ratings given to an app under any of the IDs it goes by.</summary>
    public StarRating? RatingOf(StoreApp app)
    {
        if (Ratings is null)
            return null;

        StarRating? total = null;
        foreach (var id in ReviewIdsOf(app))
        {
            if (Ratings.TryGetValue(id, out var rating))
                total = total is null ? rating : total.Plus(rating);
        }
        return total;
    }

    /// <summary>The IDs to ask the review service about: each known ID, with and without ".desktop".</summary>
    public static IReadOnlyList<string> ReviewIdsOf(StoreApp app) =>
        app.KnownIds.SelectMany(id => new[] { id, id + ".desktop" })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The ID Flathub knows an app by, if a Flatpak source offers it.</summary>
    public static string? FlathubIdOf(StoreApp app) =>
        app.Offers.FirstOrDefault(offer => offer.ProviderId == "flatpak")?.ComponentId;
}
