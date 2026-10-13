namespace Yar.Core;

/// <summary>One way to get an app: a particular source offering it.</summary>
public sealed record StoreOffer(
    string ProviderId,
    string SourceId,
    string ComponentId,
    string? PackageName,
    string? Version,
    bool MakerVerified,
    string? VerifiedBy);

/// <summary>
/// An app that can be installed, as listed in the catalogs on this computer. The same app offered
/// by several sources is one <see cref="StoreApp"/> with several <see cref="Offers"/>.
/// </summary>
public sealed record StoreApp(
    string Id,
    string Name,
    string? Summary,
    string? Description,
    string? Developer,
    string? License,
    string? Homepage,
    string? IconPath,
    string? IconName,
    IReadOnlyList<AppCategory> Categories,
    IReadOnlyList<string> KnownIds,
    IReadOnlyList<StoreOffer> Offers)
{
    /// <summary>Addresses of pictures showing the app, best first. Only from hosts Yar accepts.</summary>
    public IReadOnlyList<string> Screenshots { get; init; } = [];
}

/// <summary>One entry of a catalog before entries for the same app are merged.</summary>
public sealed record CatalogEntry(
    string ProviderId,
    string SourceId,
    string ComponentId,
    string Name,
    string? Summary,
    string? Description,
    string? Developer,
    string? License,
    string? Homepage,
    string? IconPath,
    string? IconName,
    IReadOnlyList<string> DesktopCategories,
    IReadOnlyList<string> OtherIds,
    string? PackageName,
    string? Version,
    bool MakerVerified,
    string? VerifiedBy)
{
    public IReadOnlyList<string> Screenshots { get; init; } = [];
}

/// <summary>An app in one of the store's lists, with how often it was installed last month when that is known.</summary>
public sealed record StoreListing(string AppId, long? InstallsLastMonth = null);

/// <summary>The lists the Store's front page is built from. Any of them may be empty.</summary>
public sealed record StoreCollections(
    IReadOnlyList<StoreListing> Picks,
    IReadOnlyList<StoreListing> Trending,
    IReadOnlyList<StoreListing> MostInstalled,
    IReadOnlyList<StoreListing> RecentlyUpdated)
{
    public static StoreCollections Empty { get; } = new([], [], [], []);
}

public sealed record InstallStats(long Total, long LastMonth, long LastWeek);

/// <summary>How much an offer takes to download and how much room it needs once installed. Either may be unknown.</summary>
public sealed record OfferSize(long? DownloadBytes, long? InstalledBytes);

/// <summary>What a store knows about one of its apps before it is installed.</summary>
public sealed record AppSummary(OfferSize Size, RawPermissions? Permissions);

/// <summary>Optional: a provider that can say how big one of its not-yet-installed apps is.</summary>
public interface IOfferSizeProvider
{
    Task<OfferSize?> GetSizeAsync(StoreOffer offer, CancellationToken ct);
}

public enum FeedPeriod { Week, Month, Year }

/// <summary>A source-code project developers are currently interested in. Not something Yar installs.</summary>
public sealed record DevProject(string FullName, string? Description, string? Language, long Stars, string Url);

public interface IDeveloperFeed
{
    /// <summary>New projects with the most interest, for the given programming languages.</summary>
    Task<IReadOnlyList<DevProject>> GetTrendingAsync(
        IReadOnlyList<string> languages, FeedPeriod period, CancellationToken ct);
}

/// <summary>How people rated an app: counts for one to five stars.</summary>
public sealed record StarRating(int One, int Two, int Three, int Four, int Five)
{
    public int Total => One + Two + Three + Four + Five;

    public double? Average => Total == 0 ? null : (One + 2.0 * Two + 3.0 * Three + 4.0 * Four + 5.0 * Five) / Total;

    public StarRating Plus(StarRating other) =>
        new(One + other.One, Two + other.Two, Three + other.Three, Four + other.Four, Five + other.Five);
}

public sealed record AppReview(
    int Stars, string? Summary, string? Text, string? Reviewer, DateTimeOffset? Date, string? Version, string? Distro);

/// <summary>Ratings, reviews and popularity come from services on the internet. All of it is optional.</summary>
public interface IOnlineStoreData
{
    /// <summary>Picks, trending, most installed and recently updated apps. A list that cannot be fetched is empty.</summary>
    Task<StoreCollections> GetCollectionsAsync(CancellationToken ct);

    /// <summary>How often an app was installed, or null when the service has no numbers for it.</summary>
    Task<InstallStats?> GetInstallStatsAsync(string appId, CancellationToken ct);

    /// <summary>Sizes and permissions of an app as the store lists it, or null when the store does not carry it.</summary>
    Task<AppSummary?> GetAppSummaryAsync(string appId, CancellationToken ct);

    /// <summary>The bytes of a picture. Only addresses <see cref="ScreenshotUrls.IsAllowed"/> accepts are fetched.</summary>
    Task<byte[]?> GetImageAsync(string url, CancellationToken ct);

    /// <summary>Ratings for every app the service knows, by app ID.</summary>
    Task<IReadOnlyDictionary<string, StarRating>> GetRatingsAsync(CancellationToken ct);

    Task<IReadOnlyList<AppReview>> GetReviewsAsync(IReadOnlyList<string> appIds, CancellationToken ct);
}

/// <summary>Reads the app catalogs that are already on this computer. No network involved.</summary>
public interface IStoreCatalogReader
{
    Task<IReadOnlyList<CatalogEntry>> ReadAsync(CancellationToken ct);
}
