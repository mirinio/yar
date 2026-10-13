namespace Yar.Core.Tests;

public class StorageTests
{
    [Fact]
    public void Blocks_fill_the_space_in_proportion_without_overlapping()
    {
        double[] weights = [6, 6, 4, 3, 2, 2, 1];
        var rects = Treemap.Layout(weights, 600, 400, minFraction: 0);

        Assert.Equal(weights.Length, rects.Count);
        Assert.Equal(600 * 400, rects.Sum(r => r.Area), precision: 6);
        foreach (var rect in rects)
        {
            Assert.Equal(weights[rect.Index] / weights.Sum() * 600 * 400, rect.Area, precision: 6);
            Assert.InRange(rect.X, 0, 600);
            Assert.InRange(rect.Y, 0, 400);
            Assert.InRange(rect.X + rect.Width, 0, 600 + 1e-9);
            Assert.InRange(rect.Y + rect.Height, 0, 400 + 1e-9);
        }
        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i];
                var b = rects[j];
                var overlapX = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                var overlapY = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Assert.False(overlapX > 1e-6 && overlapY > 1e-6, $"blocks {a.Index} and {b.Index} overlap");
            }
        }
        // Squarified: no block of this classic example is a sliver.
        Assert.All(rects, r => Assert.InRange(Math.Max(r.Width / r.Height, r.Height / r.Width), 1, 4));
    }

    [Fact]
    public void Tiny_items_are_merged_into_one_block()
    {
        var weights = new[] { 1000.0, 500.0 }.Concat(Enumerable.Repeat(1.0, 40)).ToArray();

        var rects = Treemap.Layout(weights, 300, 200, minFraction: 0.01);

        Assert.Equal(3, rects.Count);
        var others = Assert.Single(rects, r => r.Index == -1);
        Assert.Equal(40.0 / 1540 * 300 * 200, others.Area, precision: 6);
        Assert.Equal(300 * 200, rects.Sum(r => r.Area), precision: 6);
    }

    [Fact]
    public void A_single_small_item_is_shown_as_itself()
    {
        var rects = Treemap.Layout([1000, 1], 100, 100, minFraction: 0.01);

        Assert.Equal([0, 1], rects.Select(r => r.Index).Order());
    }

    [Theory]
    [InlineData(new double[0], 100, 100)]
    [InlineData(new[] { 0.0, -5.0 }, 100, 100)]
    [InlineData(new[] { 1.0 }, 0, 100)]
    public void Nothing_to_draw_gives_no_blocks(double[] weights, double width, double height)
    {
        Assert.Empty(Treemap.Layout(weights, width, height));
    }

    [Fact]
    public void One_item_takes_everything_and_can_be_found_by_position()
    {
        var rect = Assert.Single(Treemap.Layout([5], 80, 60));

        Assert.Equal(new TreemapRect(0, 0, 0, 80, 60), rect);
        Assert.True(rect.Contains(79.9, 59.9));
        Assert.False(rect.Contains(80, 10));
    }

    private static AppInfo App(string provider, string id, long? size, string? data = null) => new(
        provider, id, id, null, null, null, null, null, size, null, [], null, new Dictionary<string, string>())
    {
        DataPath = data,
    };

    private static readonly DiskInfo Disk = new("sda", "Test disk", 1000, [
        new("sda1", "vfat", null, ["/boot/efi"], 10, 4, 6, false),
        new("sda3", "btrfs", "fedora", ["/home", "/"], 800, 500, 300, false),
        new("sda5", "ntfs", null, [], 190, null, null, false),
    ], 0);

    [Fact]
    public void Adds_up_apps_shared_parts_leftovers_and_the_unexplained_rest()
    {
        var snapshot = new CatalogSnapshot([], [], [
            App("flatpak", "mail", 300, "/home/ada/.var/app/mail"),
            App("packagekit", "editor", 40),
            App("manual", "unknown-size", null),
            App("snap", "only-data", null, "/home/ada/snap/x"),
        ], [], [], [], [])
        {
            Leftovers = [new("flatpak", "com.gone.App", "/home/ada/.var/app/com.gone.App", 20), new("flatpak", "com.huge.App", "/x", null)],
        };
        var data = new Dictionary<(string, string), (long, bool)>
        {
            [("flatpak", "mail")] = (100, true),
            [("snap", "only-data")] = (30, false),
        };

        var overview = StorageAnalyzer.Build(
            [Disk], snapshot, data, [new("flatpak", "rt", "Platform 26.08", 50), new("flatpak", "empty", "Empty", 0)],
            [new("mozilla", "/home/ada/.cache/mozilla", 45, true), new("pip", "/home/ada/.cache/pip", 15, false), new("empty", "/home/ada/.cache/empty", 0, true)],
            "/home/ada");

        var mail = overview.Items.Single(i => i.Title == "mail");
        Assert.Equal((400, 100, false), (mail.Bytes, mail.DataBytes, mail.AtLeast));
        Assert.Equal(40, overview.Items.Single(i => i.Title == "editor").Bytes);
        // An app nobody can measure is left out rather than shown as zero.
        Assert.DoesNotContain(overview.Items, i => i.Title == "unknown-size");
        Assert.True(overview.Items.Single(i => i.Title == "only-data").AtLeast);
        Assert.Equal(50, Assert.Single(overview.Items, i => i.Kind == StorageKind.SharedParts).Bytes);
        Assert.Equal(2, overview.Items.Count(i => i.Kind == StorageKind.Leftover));
        // Temporary files are listed folder by folder, and are not counted among what belongs to apps.
        Assert.Equal(["mozilla", "pip"], overview.Items.Where(i => i.Kind == StorageKind.Cache).Select(i => i.Title));
        Assert.True(overview.Items.Single(i => i.Title == "pip").AtLeast);
        Assert.DoesNotContain(overview.AppItems, i => i.Kind == StorageKind.Cache);
        Assert.Equal(400 + 40 + 30 + 50 + 20, overview.AppItems.Sum(i => i.Bytes));

        Assert.Equal(400 + 40 + 30 + 50 + 20 + 60, overview.AccountedBytes);
        // 500 are in use and 600 are explained (pieces can overlap), so there is no unexplained rest.
        Assert.DoesNotContain(overview.Items, i => i.Kind == StorageKind.Other);
        Assert.Equal(overview.Items.OrderByDescending(i => i.Bytes).Select(i => i.Title), overview.Items.Select(i => i.Title));
    }

    [Fact]
    public void What_cannot_be_explained_is_the_rest_of_the_used_space()
    {
        var snapshot = new CatalogSnapshot([], [], [App("flatpak", "mail", 300)], [], [], [], []);

        var overview = StorageAnalyzer.Build([Disk], snapshot, new Dictionary<(string, string), (long, bool)>(), [], [], "/home/ada");

        Assert.Equal(200, overview.Items.Single(i => i.Kind == StorageKind.Other).Bytes);
        Assert.Equal(300, overview.AccountedBytes);
    }

    [Fact]
    public void Many_small_temporary_folders_become_one_entry()
    {
        var snapshot = new CatalogSnapshot([], [], [], [], [], [], []);
        var caches = Enumerable.Range(1, 12).Select(n => new CacheFolder($"c{n:00}", $"/home/ada/.cache/c{n:00}", n * 10, n != 3)).ToList();

        var temporary = StorageAnalyzer.Build([], snapshot, new Dictionary<(string, string), (long, bool)>(), [], caches, "/home/ada")
            .Items.Where(i => i.Kind == StorageKind.Cache).ToList();

        Assert.Equal(StorageAnalyzer.MaxCacheFolders + 1, temporary.Count);
        Assert.Equal("c12", temporary[0].Title);
        var rest = temporary.Single(i => i.Title == StorageAnalyzer.OtherCaches);
        Assert.Equal(10 + 20 + 30 + 40, rest.Bytes);
        Assert.True(rest.AtLeast);
        Assert.Equal("/home/ada/.cache", rest.Path);
    }

    [Fact]
    public void Temporary_folders_are_measured_one_by_one_without_following_links()
    {
        var root = Directory.CreateTempSubdirectory("yar-caches-").FullName;
        try
        {
            var cache = Directory.CreateDirectory(Path.Combine(root, "cache")).FullName;
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(cache, "browser")).FullName, "blob"), new byte[700]);
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(cache, "tool")).FullName, "blob"), new byte[30]);
            File.WriteAllBytes(Path.Combine(cache, "loose-file"), new byte[9]);
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            File.WriteAllBytes(Path.Combine(outside, "big"), new byte[5000]);
            Directory.CreateSymbolicLink(Path.Combine(cache, "linked"), outside);

            var measured = StorageAnalyzer.MeasureCaches(cache, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal([("browser", 700L, true), ("tool", 30L, true)], measured.Select(c => (c.Name, c.Bytes, c.Complete)));
            Assert.Empty(StorageAnalyzer.MeasureCaches(Path.Combine(root, "missing"), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Finds_the_partitions_that_hold_the_system_and_the_home_folder()
    {
        var separate = new DiskInfo("sdb", null, 100, [
            new("sdb1", "ext4", null, ["/"], 40, 10, 30, false),
            new("sdb2", "ext4", null, ["/home"], 60, 20, 40, false),
            new("sdb3", "ext4", null, ["/mnt/backup"], 60, 5, 55, false),
        ], 0);

        Assert.Equal(["sda3"], StorageAnalyzer.HomeAndSystem([Disk], "/home/ada").Select(p => p.Name));
        Assert.Equal(["sdb1", "sdb2"], StorageAnalyzer.HomeAndSystem([separate], "/home/ada").Select(p => p.Name));
        Assert.Equal(["sdb1"], StorageAnalyzer.HomeAndSystem([separate], "/root").Select(p => p.Name));
        Assert.Empty(StorageAnalyzer.HomeAndSystem([], "/home/ada"));
    }

    [Fact]
    public void Counting_a_folder_stops_at_its_limits_and_does_not_follow_links()
    {
        var root = Directory.CreateTempSubdirectory("yar-size-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "data", "sub")).Parent!.FullName;
            for (var i = 0; i < 10; i++)
                File.WriteAllBytes(Path.Combine(folder, i % 2 == 0 ? "" : "sub", $"f{i}"), new byte[100]);
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            File.WriteAllBytes(Path.Combine(outside, "big"), new byte[5000]);
            Directory.CreateSymbolicLink(Path.Combine(folder, "link"), outside);
            var token = TestContext.Current.CancellationToken;

            Assert.Equal(1000, FolderSize.Measure(folder, token));
            var partial = FolderSize.MeasureUpTo(folder, maxFiles: 4, TimeSpan.FromSeconds(5), token);
            Assert.False(partial.Complete);
            Assert.Equal(400, partial.Bytes);
            Assert.Equal((0, false), FolderSize.MeasureUpTo(Path.Combine(root, "missing"), 10, TimeSpan.FromSeconds(1), token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
