namespace Yar.Core;

/// <summary>
/// Works out where disk space goes, from the point of view of apps: each app with its data, the
/// parts apps share, what removed apps left behind, and temporary files. It measures; it removes nothing.
/// </summary>
public sealed class StorageAnalyzer(
    IStorageProbe probe, IReadOnlyList<ISoftwareProvider> providers, string cacheHome, string home)
{
    public const int MaxCacheFolders = 8;

    /// <summary>The title of the entry that adds up the smaller temporary folders.</summary>
    public const string OtherCaches = "";

    // All temporary folders together get this long; whatever is not reached is left out.
    private static readonly TimeSpan MaxTimeForCaches = TimeSpan.FromSeconds(8);

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
        return Build(disks, snapshot, measured.Data, shared, measured.Caches, home);
    }

    /// <summary>Puts the pieces together. Pure, so the arithmetic can be tested without a disk.</summary>
    public static StorageOverview Build(
        IReadOnlyList<DiskInfo> disks,
        CatalogSnapshot snapshot,
        IReadOnlyDictionary<(string ProviderId, string AppId), (long Bytes, bool Complete)> dataSizes,
        IReadOnlyList<SharedPart> sharedParts,
        IReadOnlyList<CacheFolder> caches,
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

        // Temporary files are listed folder by folder: "16 GB of temporary files" tells nobody anything,
        // "8 GB of debugging downloads" does. The many small ones are added up into one entry.
        var biggest = caches.Where(c => c.Bytes > 0).OrderByDescending(c => c.Bytes).ToList();
        items.AddRange(biggest.Take(MaxCacheFolders)
            .Select(c => new StorageItem(StorageKind.Cache, c.Name, c.Bytes, AtLeast: !c.Complete, Path: c.Path)));
        var smaller = biggest.Skip(MaxCacheFolders).ToList();
        if (smaller.Count > 0)
        {
            items.Add(new(
                StorageKind.Cache, OtherCaches, smaller.Sum(c => c.Bytes), AtLeast: smaller.Any(c => !c.Complete),
                Path: Path.GetDirectoryName(smaller[0].Path)));
        }

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

    private (Dictionary<(string, string), (long, bool)> Data, List<CacheFolder> Caches) Measure(
        CatalogSnapshot snapshot, CancellationToken ct)
    {
        var data = new Dictionary<(string, string), (long, bool)>();
        foreach (var app in snapshot.Apps.Where(a => a.DataPath is not null))
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(app.DataPath))
                data[(app.ProviderId, app.Id)] = FolderSize.MeasureUpTo(app.DataPath!, MaxFilesPerFolder, MaxTimePerFolder, ct);
        }
        return (data, MeasureCaches(cacheHome, MaxTimeForCaches, ct));
    }

    /// <summary>Measures each folder directly inside the temporary-files folder, within a shared time limit.</summary>
    public static List<CacheFolder> MeasureCaches(string cacheHome, TimeSpan budget, CancellationToken ct)
    {
        var caches = new List<CacheFolder>();
        var deadline = DateTime.UtcNow + budget;
        try
        {
            if (!Directory.Exists(cacheHome))
                return caches;

            foreach (var directory in new DirectoryInfo(cacheHome).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var remaining = deadline - DateTime.UtcNow;
                // A link could lead anywhere; only real folders are measured.
                if (directory.LinkTarget is not null || remaining <= TimeSpan.Zero)
                    continue;
                var (bytes, complete) = FolderSize.MeasureUpTo(directory.FullName, MaxFilesPerFolder, remaining, ct);
                caches.Add(new(directory.Name, directory.FullName, bytes, complete));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return caches;
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
