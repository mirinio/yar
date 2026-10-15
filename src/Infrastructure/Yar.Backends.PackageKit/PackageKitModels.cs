using Yar.Core;

namespace Yar.Backends.PackageKit;

public sealed record PkBackend(string Name, string DistroId);

public sealed record PkRepo(string Id, string Description, bool Enabled);

/// <summary>A package as PackageKit identifies it: <c>name;version;arch;data</c>.</summary>
public sealed record PkPackage(
    string PackageId, string Name, string Version, string Arch, string Data, string Summary, uint Info = 0)
{
    /// <summary>
    /// The repository an installed package came from, when the backend reports it.
    /// The dnf5 backend sends <c>installed:updates</c>, and <c>installed:&lt;unknown&gt;</c> when it does not know.
    /// </summary>
    public string? OriginRepo
    {
        get
        {
            var separator = Data.IndexOf(':');
            if (separator < 0)
                return null;
            var origin = Data[(separator + 1)..];
            return origin.Length == 0 || origin.StartsWith('<') || origin.StartsWith('@') ? null : origin;
        }
    }

    public static PkPackage? Parse(string packageId, string summary, uint info = 0)
    {
        var parts = packageId.Split(';');
        return parts.Length == 4 && parts[0].Length > 0
            ? new(packageId, parts[0], parts[1], parts[2], parts[3], summary, info)
            : null;
    }
}

public sealed class PackageKitException(uint code, string details)
    : Exception($"PackageKit reported a problem ({code}): {details}")
{
    public uint Code { get; } = code;
}

/// <summary>How far a running install or removal has come, in PackageKit's own terms.</summary>
/// <param name="Status">A PK_STATUS_ENUM value from pk-enum.h.</param>
/// <param name="Percentage">0 to 100, or null while the daemon cannot say.</param>
public sealed record PkProgress(uint Status, uint? Percentage, bool AllowCancel)
{
    // PK_STATUS_ENUM values from pk-enum.h.
    public const uint Remove = 6, Download = 8, Install = 9, Update = 10, Cleanup = 11, DepResolve = 13,
        SigCheck = 14, TestCommit = 15, Commit = 16, Finished = 18, DownloadRepository = 20,
        DownloadPackagelist = 21, DownloadFilelist = 22, DownloadChangelog = 23, DownloadGroup = 24,
        DownloadUpdateinfo = 25, LoadingCache = 27;

    public OperationPhase Phase(OperationPhase working) => Status switch
    {
        Download or DownloadRepository or DownloadPackagelist or DownloadFilelist or DownloadChangelog
            or DownloadGroup or DownloadUpdateinfo => OperationPhase.Downloading,
        DepResolve or SigCheck or TestCommit or LoadingCache => OperationPhase.Checking,
        Install or Update or Commit or Remove => working,
        Cleanup or Finished => OperationPhase.Finishing,
        _ => OperationPhase.Waiting,
    };
}

/// <summary>The PackageKit operations Yar needs.</summary>
public interface IPackageKitClient
{
    /// <summary>Returns null when PackageKit is not available on this system.</summary>
    Task<PkBackend?> GetBackendAsync(CancellationToken ct);

    Task<IReadOnlyList<PkRepo>> GetRepoListAsync(CancellationToken ct);

    /// <summary>The installed packages with these names, in every architecture they are installed for.</summary>
    Task<IReadOnlyList<PkPackage>> ResolveInstalledAsync(IReadOnlyList<string> names, CancellationToken ct);

    /// <summary>The installed packages owning <paramref name="path"/>; normally one or none.</summary>
    Task<IReadOnlyList<PkPackage>> SearchInstalledFileAsync(string path, CancellationToken ct);

    /// <summary>
    /// Tells the daemon that packages were changed behind its back, so that it looks again.
    /// Other tools do this after installing; a daemon that was not told can answer from an old picture.
    /// </summary>
    Task NotifyPackagesChangedAsync(CancellationToken ct);

    /// <summary>Switches a repository on or off. PackageKit asks the user for their password if needed.</summary>
    Task SetRepoEnabledAsync(string repoId, bool enabled, CancellationToken ct);

    /// <summary>Packages with a newer version waiting; <see cref="PkPackage.Info"/> says what kind of update.</summary>
    Task<IReadOnlyList<PkPackage>> GetUpdatesAsync(CancellationToken ct);

    /// <summary>How long ago the list of available packages was refreshed; null if never or unknown.</summary>
    Task<TimeSpan?> GetTimeSinceRefreshAsync(CancellationToken ct);

    /// <summary>
    /// Download and installed size of the newest version of a package that is on offer, whether or
    /// not it is installed. Null when no source offers a package of that name.
    /// </summary>
    Task<OfferSize?> GetOfferSizeAsync(string packageName, CancellationToken ct);

    /// <summary>Installed size in bytes per package ID. Packages the backend skips are absent.</summary>
    Task<IReadOnlyDictionary<string, long>> GetSizesAsync(IReadOnlyList<string> packageIds, CancellationToken ct);

    /// <summary>The newest not-yet-installed packages with this name, for this computer's processor type.</summary>
    Task<IReadOnlyList<PkPackage>> ResolveAvailableAsync(string name, CancellationToken ct);

    /// <summary>Download and installed size per package ID. Packages the backend skips are absent.</summary>
    Task<IReadOnlyDictionary<string, OfferSize>> GetOfferSizesAsync(IReadOnlyList<string> packageIds, CancellationToken ct);

    /// <summary>
    /// Everything installing these packages would install, update or remove, without doing it.
    /// <see cref="PkPackage.Info"/> says which.
    /// </summary>
    Task<IReadOnlyList<PkPackage>> SimulateInstallAsync(IReadOnlyList<string> packageIds, CancellationToken ct);

    /// <summary>Installs packages from the system's sources. Only signed packages; the system asks for the password.</summary>
    Task InstallAsync(IReadOnlyList<string> packageIds, IProgress<PkProgress> progress, CancellationToken ct);

    /// <summary>Everything removing these packages would take away, without doing it.</summary>
    Task<IReadOnlyList<PkPackage>> SimulateRemoveAsync(IReadOnlyList<string> packageIds, CancellationToken ct);

    /// <param name="allowDependents">Also remove the packages that cannot stay without these.</param>
    Task RemoveAsync(
        IReadOnlyList<string> packageIds, bool allowDependents, IProgress<PkProgress> progress, CancellationToken ct);

    /// <summary>Everything installing these package files would install or remove, without doing it.</summary>
    Task<IReadOnlyList<PkPackage>> SimulateInstallFilesAsync(IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>
    /// Installs package files the user brought. Unlike <see cref="InstallAsync"/> this accepts packages
    /// nobody the system knows has signed, so it is only for files the user explicitly vouched for.
    /// </summary>
    Task InstallFilesAsync(IReadOnlyList<string> paths, IProgress<PkProgress> progress, CancellationToken ct);

    /// <summary>Everything updating these packages would install, update or remove, without doing it.</summary>
    Task<IReadOnlyList<PkPackage>> SimulateUpdateAsync(IReadOnlyList<string> packageIds, CancellationToken ct);

    /// <summary>Installs the newer versions named by these package IDs. Only signed packages.</summary>
    Task UpdateAsync(IReadOnlyList<string> packageIds, IProgress<PkProgress> progress, CancellationToken ct);
}
