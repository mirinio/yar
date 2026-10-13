namespace Yar.Core;

/// <summary>One part of a disk. <see cref="UsedBytes"/> and <see cref="FreeBytes"/> are only known while it is in use.</summary>
public sealed record PartitionInfo(
    string Name,
    string? FileSystem,
    string? Label,
    IReadOnlyList<string> MountPoints,
    long SizeBytes,
    long? UsedBytes,
    long? FreeBytes,
    bool ReadOnly)
{
    public bool InUse => MountPoints.Count > 0;
}

/// <param name="UnallocatedBytes">Space on the disk that belongs to no partition.</param>
public sealed record DiskInfo(
    string Name, string? Model, long SizeBytes, IReadOnlyList<PartitionInfo> Partitions, long UnallocatedBytes);

public enum StorageKind { App, SharedParts, Leftover, Cache, Other }

/// <summary>Something that takes up space, as one block of the picture.</summary>
/// <param name="DataBytes">For an app: how much of <paramref name="Bytes"/> is its own data rather than the program.</param>
/// <param name="AtLeast">True when counting stopped early, so the real size is larger.</param>
public sealed record StorageItem(
    StorageKind Kind,
    string Title,
    long Bytes,
    long DataBytes = 0,
    bool AtLeast = false,
    string? ProviderId = null,
    string? AppId = null,
    string? Path = null);

/// <summary>A building block several apps share, such as a Flatpak runtime.</summary>
public sealed record SharedPart(string ProviderId, string Id, string Name, long Bytes);

public sealed record StorageOverview(IReadOnlyList<DiskInfo> Disks, IReadOnlyList<StorageItem> Items)
{
    public long AccountedBytes => Items.Where(item => item.Kind != StorageKind.Other).Sum(item => item.Bytes);
}

/// <summary>Reads which disks and partitions this computer has. It only looks.</summary>
public interface IStorageProbe
{
    Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken ct);
}

/// <summary>Optional: a provider whose apps share parts that take space of their own.</summary>
public interface ISharedPartsProvider
{
    Task<IReadOnlyList<SharedPart>> GetSharedPartsAsync(CancellationToken ct);
}
