namespace Yar.Core;

/// <summary>
/// Works out where disk space goes, from the point of view of apps: each app with its data, the
/// parts apps share, what removed apps left behind, and temporary files. It measures; it removes nothing.
/// </summary>
public sealed class StorageAnalyzer(
    IStorageProbe probe, IReadOnlyList<ISoftwareProvider> providers, string cacheHome, string home)
{
    // Data folders can be large; give each a fair but bounded look.
    private const int MaxFilesPerFolder = 200_000;
    private static readonly TimeSpan MaxTimePerFolder = TimeSpan.FromSeconds(4);

    public async Task<StorageOverview> AnalyzeAsync(CatalogSnapshot snapshot, CancellationToken ct)
    {
        var disks = await Tolerant(() => probe.GetDisksAsync(ct), ct).ConfigureAwait(false) ?? [];

        var shared = new List<SharedPart>();
        foreach (var provider in providers.OfType<ISharedPartsProvider>())
            shared.AddRange(await Tolerant(() => provider.GetSharedPartsAsync(ct), ct).ConfigureAwait(false) ?? []);

        var measured = await Task.Run(() => Measure(snapshot, ct), ct).ConfigureAwait(false);
        return Build(disks, snapshot, measured.Data, shared, measured.Cache, home);
    }

    /// <summary>Puts the pieces together. Pure, so the arithmetic can be tested without a disk.</summary>
    public static StorageOverview Build(
        IReadOnlyList<DiskInfo> disks,
        CatalogSnapshot snapshot,
        IReadOnlyDictionary<(string ProviderId, string AppId), (long Bytes, bool Complete)> dataSizes,
        IReadOnlyList<SharedPart> sharedParts,
        (long Bytes, bool Complete, string Path)? cache,
        string home)
    {
        var items = new List<StorageItem>();

        foreach (var app in snapshot.Apps)
        {
            dataSizes.TryGetValue((app.ProviderId, app.Id), out var data);
            var bytes = (app.SizeBytes ?? 0) + data.Bytes;
            // An app nobody can put a number on would be an invisible block; it is left out, not shown as zero.
            if (bytes <= 0)
                continue;
            items.Add(new(
                StorageKind.App, app.Name, bytes, data.Bytes,
                AtLeast: app.SizeBytes is null || (data.Bytes > 0 && !data.Complete),
                app.ProviderId, app.Id, app.DataPath ?? app.InstallPath));
        }

        items.AddRange(sharedParts.Where(part => part.Bytes > 0)
            .Select(part => new StorageItem(StorageKind.SharedParts, part.Name, part.Bytes, ProviderId: part.ProviderId)));

        items.AddRange(snapshot.Leftovers.Select(leftover => new StorageItem(
            StorageKind.Leftover, leftover.Id, leftover.SizeBytes ?? 0, AtLeast: leftover.SizeBytes is null,
            ProviderId: leftover.ProviderId, Path: leftover.Path)));

        if (cache is { Bytes: > 0 } temporary)
            items.Add(new(StorageKind.Cache, "Temporary files", temporary.Bytes, AtLeast: !temporary.Complete, Path: temporary.Path));

        // What Yar cannot attribute: used space on the disks holding the system and the home folder,
        // minus everything above. Never negative; the pieces above can overlap or sit on other disks.
        var used = HomeAndSystem(disks, home).Sum(partition => partition.UsedBytes ?? 0);
        var accounted = items.Sum(item => item.Bytes);
        if (used > accounted)
            items.Add(new(StorageKind.Other, "Everything else", used - accounted));

        return new(disks, [.. items.OrderByDescending(item => item.Bytes)]);
    }

    /// <summary>The partitions that hold the system and the user's home folder, each once.</summary>
    public static IReadOnlyList<PartitionInfo> HomeAndSystem(IReadOnlyList<DiskInfo> disks, string home)
    {
        var partitions = disks.SelectMany(disk => disk.Partitions).Where(partition => partition.InUse).ToList();
        PartitionInfo? Holding(string path) => partitions
            .SelectMany(partition => partition.MountPoints.Select(mount => (partition, mount)))
            .Where(pair => pair.mount == "/" || path == pair.mount || path.StartsWith(pair.mount.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(pair => pair.mount.Length)
            .Select(pair => pair.partition)
            .FirstOrDefault();

        return new[] { Holding("/"), Holding(home) }.Where(p => p is not null).Select(p => p!).Distinct().ToList();
    }

    private (Dictionary<(string, string), (long, bool)> Data, (long, bool, string)? Cache) Measure(
        CatalogSnapshot snapshot, CancellationToken ct)
    {
        var data = new Dictionary<(string, string), (long, bool)>();
        foreach (var app in snapshot.Apps.Where(a => a.DataPath is not null))
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(app.DataPath))
                data[(app.ProviderId, app.Id)] = FolderSize.MeasureUpTo(app.DataPath!, MaxFilesPerFolder, MaxTimePerFolder, ct);
        }

        (long, bool, string)? cache = null;
        if (Directory.Exists(cacheHome))
        {
            var (bytes, complete) = FolderSize.MeasureUpTo(cacheHome, MaxFilesPerFolder * 2, MaxTimePerFolder * 2, ct);
            cache = (bytes, complete, cacheHome);
        }
        return (data, cache);
    }

    // A part of the picture that cannot be read leaves a gap; it does not take the picture down.
    private static async Task<T?> Tolerant<T>(Func<Task<T>> read, CancellationToken ct)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            return null;
        }
    }
}
