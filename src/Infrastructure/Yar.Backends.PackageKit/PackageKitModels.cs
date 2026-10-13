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

/// <summary>The PackageKit operations Yar needs. All of them only read.</summary>
public interface IPackageKitClient
{
    /// <summary>Returns null when PackageKit is not available on this system.</summary>
    Task<PkBackend?> GetBackendAsync(CancellationToken ct);

    Task<IReadOnlyList<PkRepo>> GetRepoListAsync(CancellationToken ct);

    /// <summary>The installed packages with these names, in every architecture they are installed for.</summary>
    Task<IReadOnlyList<PkPackage>> ResolveInstalledAsync(IReadOnlyList<string> names, CancellationToken ct);

    /// <summary>The installed packages owning <paramref name="path"/>; normally one or none.</summary>
    Task<IReadOnlyList<PkPackage>> SearchInstalledFileAsync(string path, CancellationToken ct);

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
}
