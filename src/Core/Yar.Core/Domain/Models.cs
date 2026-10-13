namespace Yar.Core;

public enum SourceScope { System, User }

public enum TrustLevel { Distro, WellKnown, ThirdParty, Unknown }

/// <summary>A place software comes from: a package repository, a Flatpak remote, the Snap Store.</summary>
public sealed record SourceInfo(
    string ProviderId,
    string Id,
    string Name,
    string? Description,
    string? Url,
    bool Enabled,
    SourceScope Scope,
    TrustLevel Trust,
    IReadOnlyDictionary<string, string> Technical);

/// <summary>
/// An installed app. <see cref="SizeBytes"/> and <see cref="InstalledAt"/> stay null when the
/// backend cannot report a true value.
/// </summary>
public sealed record AppInfo(
    string ProviderId,
    string Id,
    string Name,
    string? Summary,
    string? IconName,
    string? IconPath,
    string? Version,
    string? SourceId,
    long? SizeBytes,
    DateTimeOffset? InstalledAt,
    IReadOnlyList<string> LauncherPaths,
    string? ComponentId,
    IReadOnlyDictionary<string, string> Technical)
{
    /// <summary>The launcher shown for this app, if it has one.</summary>
    public string? PrimaryLauncherPath => LauncherPaths.Count > 0 ? LauncherPaths[0] : null;

    public IReadOnlyList<AppCategory> Categories { get; init; } = [];

    /// <summary>
    /// The folder (or single file) this app lives in. Null when it has no one place,
    /// as with system packages, whose files are spread over many folders.
    /// </summary>
    public string? InstallPath { get; init; }

    /// <summary>True for apps no source manages: the user set them up by hand.</summary>
    public bool IsSelfInstalled => ProviderId == "manual";
}

public enum AppCategory
{
    Games, Internet, Office, Graphics, SoundAndVideo, Development, Education, Science,
    Utilities, System, Settings, Other,
}

public enum UpdateSeverity { Security, BugFix, Enhancement, Normal }

/// <summary>
/// A newer version that is waiting to be installed. <see cref="AppId"/> names the installed
/// <see cref="AppInfo"/> it belongs to; null means a part of the system rather than an app.
/// </summary>
public sealed record UpdateInfo(
    string ProviderId,
    string Id,
    string Name,
    string? CurrentVersion,
    string? NewVersion,
    UpdateSeverity Severity,
    string? SourceId,
    string? AppId);

/// <summary>What a provider found when it looked for updates, and how fresh that knowledge is.</summary>
public sealed record UpdateCheck(IReadOnlyList<UpdateInfo> Updates, DateTimeOffset? LastRefreshed = null);

public sealed record ProviderLocation(string Label, string Path);

/// <summary>Where a kind of software keeps its things on this computer, and where to read about it.</summary>
public sealed record ProviderFacts(string ProviderId, string? HomepageUrl, IReadOnlyList<ProviderLocation> Locations);

public enum ProviderAvailability { Available, NotInstalled, Error }

public sealed record ProviderStatus(ProviderAvailability Availability, string? Reason = null)
{
    public static ProviderStatus Available { get; } = new(ProviderAvailability.Available);
    public static ProviderStatus NotInstalled { get; } = new(ProviderAvailability.NotInstalled);
    public static ProviderStatus Error(string reason) => new(ProviderAvailability.Error, reason);
}
