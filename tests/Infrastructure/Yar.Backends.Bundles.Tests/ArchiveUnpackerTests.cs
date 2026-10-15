using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace Yar.Backends.Bundles.Tests;

public sealed class ArchiveUnpackerTests : IDisposable
{
    private static readonly string RealProgram = new[] { "/usr/bin/true", "/bin/true" }.First(File.Exists);

    private readonly string _root = Directory.CreateTempSubdirectory("yar-unpack-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string PlainTar()
    {
        var path = Path.Combine(_root, "app.tar");
        using var file = File.Create(path);
        using var tar = new TarWriter(file, TarEntryFormat.Pax);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "app/bin/app")
        {
            DataStream = new MemoryStream(File.ReadAllBytes(RealProgram)),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
        });
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "app/readme.txt") { DataStream = new MemoryStream("hi"u8.ToArray()) });
        return path;
    }

    [Theory]
    [InlineData("tar.xz", "xz")]
    [InlineData("tar.bz2", "bzip2")]
    [InlineData("tar.zst", "zstd")]
    public void Archives_compressed_by_the_systems_tools_are_unpacked(string format, string tool)
    {
        var program = new[] { $"/usr/bin/{tool}", $"/bin/{tool}" }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(program is null, $"{tool} is not installed here");

        var tar = PlainTar();
        var compressed = $"{tar}.{format.Split('.')[1]}";
        using (var process = Process.Start(new ProcessStartInfo(program!, ["-k", "-q", tar]) { RedirectStandardError = true })!)
            process.WaitForExit();
        Assert.True(File.Exists(compressed));

        var result = ArchiveUnpacker.Unpack(compressed, format, Path.Combine(_root, "out"), 10_000_000, Token);

        Assert.Equal(2, result.Files);
        Assert.True(File.GetUnixFileMode(Path.Combine(_root, "out", "app", "bin", "app")).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(_root, "out", "app", "readme.txt")));
    }

    [Fact]
    public void In_a_zip_without_permissions_programs_are_made_runnable_and_nothing_else()
    {
        var zipPath = Path.Combine(_root, "app.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in new (string, byte[])[]
            {
                ("app/app", File.ReadAllBytes(RealProgram)), ("app/start.sh", "#!/bin/sh\necho hi\n"u8.ToArray()), ("app/readme.txt", "hi"u8.ToArray()),
            })
            {
                var entry = zip.CreateEntry(name);
                // As a zip made on Windows: no Unix permissions at all.
                entry.ExternalAttributes = 0;
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        ArchiveUnpacker.Unpack(zipPath, "zip", Path.Combine(_root, "out"), 10_000_000, Token);

        bool Runnable(string name) =>
            File.GetUnixFileMode(Path.Combine(_root, "out", "app", name)).HasFlag(UnixFileMode.UserExecute);
        Assert.True(Runnable("app"));
        Assert.True(Runnable("start.sh"));
        Assert.False(Runnable("readme.txt"));
    }
}
