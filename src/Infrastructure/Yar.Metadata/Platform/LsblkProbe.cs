using System.Globalization;
using System.Text.Json;
using Yar.Core;

namespace Yar.Metadata;

/// <summary>Reads the disks and partitions of this computer from <c>lsblk</c>, which needs no special rights.</summary>
public sealed class LsblkProbe(IProcessRunner runner, string lsblkPath = "/usr/bin/lsblk") : IStorageProbe
{
    // Space outside partitions below this is alignment slack, not something a person can use.
    public const long MinUnallocatedBytes = 1024L * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken ct)
    {
        if (!File.Exists(lsblkPath))
            return [];

        var result = await runner.RunAsync(
            lsblkPath, ["--bytes", "--json", "--output", "NAME,TYPE,SIZE,FSTYPE,LABEL,MOUNTPOINTS,FSUSED,FSAVAIL,RO,MODEL"],
            Timeout, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"lsblk failed: {result.StdErr.Trim()}");
        return Parse(result.StdOut);
    }

    public static IReadOnlyList<DiskInfo> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("blockdevices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var disks = new List<DiskInfo>();
            foreach (var device in devices.EnumerateArray())
            {
                // Only real disks: not compressed memory (zram), disk images (loop) or optical drives (rom).
                if (device.ValueKind != JsonValueKind.Object || Text(device, "type") != "disk" ||
                    Text(device, "name") is not { } name || name.StartsWith("zram", StringComparison.Ordinal) ||
                    Number(device, "size") is not { } size || size <= 0)
                {
                    continue;
                }

                var children = Children(device);
                var partitions = new List<PartitionInfo>();
                if (children.Count == 0)
                {
                    // A disk formatted as a whole, without a partition table.
                    if (Text(device, "fstype") is not null)
                        partitions.Add(ToPartition(device));
                }
                else
                {
                    foreach (var child in children)
                        partitions.AddRange(Leaves(child));
                }

                var inPartitions = children.Sum(child => Number(child, "size") ?? 0);
                var unallocated = children.Count > 0 ? size - inPartitions : 0;
                disks.Add(new(
                    name, Text(device, "model"), size, partitions,
                    unallocated >= MinUnallocatedBytes ? unallocated : 0));
            }
            return disks;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // An encrypted or volume-managed partition shows its contents as children; those are what is in use.
    private static IEnumerable<PartitionInfo> Leaves(JsonElement node)
    {
        var children = Children(node);
        if (children.Count == 0)
            return [ToPartition(node)];
        return children.SelectMany(Leaves);
    }

    private static PartitionInfo ToPartition(JsonElement node)
    {
        var mounts = node.TryGetProperty("mountpoints", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String)
                .Select(m => m.GetString()!).Where(m => m.StartsWith('/')).ToList()
            : [];
        return new(
            Text(node, "name") ?? "?",
            Text(node, "fstype"),
            Text(node, "label"),
            mounts,
            Number(node, "size") ?? 0,
            Number(node, "fsused"),
            Number(node, "fsavail"),
            node.TryGetProperty("ro", out var readOnly) && readOnly.ValueKind == JsonValueKind.True);
    }

    private static List<JsonElement> Children(JsonElement node) =>
        node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array
            ? children.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static string? Text(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;

    // Older versions of lsblk print sizes as text.
    private static long? Number(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var number) => number,
                JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            }
            : null;
}
