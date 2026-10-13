namespace Yar.Core;

/// <summary>
/// A read-only view of one software ecosystem. Write operations are added through separate,
/// optional interfaces, so a provider that implements only this one is still a valid provider.
/// </summary>
public interface ISoftwareProvider
{
    /// <summary>Stable identifier, e.g. "packagekit", "flatpak", "snap", "manual".</summary>
    string Id { get; }

    /// <summary>Static facts for the "what is this?" page. Available even when the provider is not installed.</summary>
    ProviderFacts Facts { get; }

    Task<ProviderStatus> ProbeAsync(CancellationToken ct);

    Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken ct);

    /// <summary>
    /// Returns the installed apps this provider owns. Every launcher it takes from
    /// <paramref name="unclaimed"/> must be listed in the app's <see cref="AppInfo.LauncherPaths"/>.
    /// </summary>
    Task<IReadOnlyList<AppInfo>> ClaimAppsAsync(DesktopEntryIndex unclaimed, CancellationToken ct);
}

/// <summary>Optional: a provider that can say which newer versions are waiting. It only looks; it installs nothing.</summary>
public interface IUpdateProvider
{
    Task<UpdateCheck> GetUpdatesAsync(IReadOnlyList<AppInfo> installed, CancellationToken ct);
}

/// <summary>
/// Optional: a provider whose sources can be changed. These are the first operations in Yar that
/// alter the system, so each one is narrow and says exactly what it does.
/// </summary>
public interface ISourceManager
{
    /// <summary>True when <see cref="SetEnabledAsync"/> can switch this source on or off.</summary>
    bool CanToggle(SourceInfo source);

    /// <summary>Switches a source on or off. The system may ask the user for their password.</summary>
    Task SetEnabledAsync(SourceInfo source, bool enabled, CancellationToken ct);

    /// <summary>True when <see cref="RemoveAsync"/> can remove this source.</summary>
    bool CanRemove(SourceInfo source);

    Task RemoveAsync(SourceInfo source, CancellationToken ct);

    /// <summary>True when this provider can add new sources at all.</summary>
    bool CanAdd { get; }

    Task AddAsync(SourceRequest request, CancellationToken ct);
}
