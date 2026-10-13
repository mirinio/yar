namespace Yar.Metadata.Tests;

public class LsblkProbeTests
{
    // The layout of the development machine, plus a compressed-memory device and a disk with a gap.
    private const string Json = """
        {"blockdevices": [
          {"name":"sda","type":"disk","size":1000204886016,"fstype":null,"label":null,"mountpoints":[],"fsused":null,"fsavail":null,"ro":false,"model":"Samsung SSD 860 EVO 1TB",
           "children":[
             {"name":"sda1","type":"part","size":629145600,"fstype":"vfat","label":null,"mountpoints":["/boot/efi"],"fsused":54296576,"fsavail":573579264,"ro":false,"model":null},
             {"name":"sda3","type":"part","size":836365189120,"fstype":"btrfs","label":"fedora","mountpoints":["/home","/"],"fsused":504591249408,"fsavail":330240430080,"ro":false,"model":null},
             {"name":"sda4","type":"part","size":16777216,"fstype":null,"label":null,"mountpoints":[],"fsused":null,"fsavail":null,"ro":false,"model":null},
             {"name":"sda5","type":"part","size":163193774080,"fstype":"ntfs","label":null,"mountpoints":[null],"fsused":null,"fsavail":null,"ro":false,"model":null}
           ]},
          {"name":"zram0","type":"disk","size":8589934592,"fstype":"swap","mountpoints":["[SWAP]"],"fsused":null,"fsavail":null,"ro":false,"model":null},
          {"name":"loop0","type":"loop","size":100,"fstype":"squashfs","mountpoints":["/snap/x/1"]},
          {"name":"sr0","type":"rom","size":1073741312},
          {"name":"nvme0n1","type":"disk","size":"500000000000","ro":false,"model":"Gappy",
           "children":[
             {"name":"nvme0n1p1","type":"part","size":"300000000000","fstype":"crypto_LUKS","mountpoints":[],
              "children":[{"name":"luks-1","type":"crypt","size":"299900000000","fstype":"ext4","mountpoints":["/data"],"fsused":"100","fsavail":"200","ro":true}]}
           ]},
          {"name":"sdb","type":"disk","size":64000000000,"fstype":"exfat","label":"STICK","mountpoints":["/run/media/ada/STICK"],"fsused":1,"fsavail":2,"ro":false,"model":"Flash"},
          {"name":"sdc","type":"disk","size":0},
          "junk"
        ]}
        """;

    [Fact]
    public void Reads_disks_partitions_and_space_outside_partitions()
    {
        var disks = LsblkProbe.Parse(Json);

        Assert.Equal(["sda", "nvme0n1", "sdb"], disks.Select(d => d.Name));

        var main = disks[0];
        Assert.Equal("Samsung SSD 860 EVO 1TB", main.Model);
        Assert.Equal(0, main.UnallocatedBytes);
        Assert.Equal(["sda1", "sda3", "sda4", "sda5"], main.Partitions.Select(p => p.Name));
        var home = main.Partitions[1];
        Assert.Equal(["/home", "/"], home.MountPoints);
        Assert.Equal((504591249408, 330240430080), (home.UsedBytes, home.FreeBytes));
        Assert.Equal("fedora", home.Label);
        Assert.True(home.InUse);
        Assert.False(main.Partitions[3].InUse);
        Assert.Null(main.Partitions[2].FileSystem);

        // Sizes given as text, an encrypted partition shown by what is inside it, and 200 GB belonging to no partition.
        var gappy = disks[1];
        Assert.Equal(200_000_000_000, gappy.UnallocatedBytes);
        var inside = Assert.Single(gappy.Partitions);
        Assert.Equal(("luks-1", "ext4", true), (inside.Name, inside.FileSystem, inside.ReadOnly));
        Assert.Equal(["/data"], inside.MountPoints);

        // A disk formatted as a whole.
        Assert.Equal("STICK", Assert.Single(disks[2].Partitions).Label);
        Assert.Equal(0, disks[2].UnallocatedBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"blockdevices\": \"nope\"}")]
    public void Unexpected_output_gives_no_disks(string json)
    {
        Assert.Empty(LsblkProbe.Parse(json));
    }
}
