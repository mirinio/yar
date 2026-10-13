using System.Formats.Tar;
using System.IO.Compression;
using Yar.Core;
using Yar.Metadata;

namespace Yar.Backends.Bundles.Tests;

public sealed class IntakeTests : IDisposable
{
    // A small real program that every Linux system has, used as the "app".
    private static readonly string RealProgram = new[] { "/usr/bin/true", "/bin/true" }.First(File.Exists);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[64]];

    private readonly string _home = Directory.CreateTempSubdirectory("yar-intake-").FullName;
    private readonly XdgDirs _dirs;
    private readonly HomeTrash _trash;
    private readonly ManifestStore _manifests;
    private readonly BundleIntake _intake;

    public IntakeTests()
    {
        _dirs = new XdgDirs(_home, _ => null);
        _trash = new HomeTrash(_home, _dirs.DataHome);
        _manifests = new ManifestStore(Path.Combine(_dirs.DataHome, "yar", "apps"));
        _intake = new BundleIntake(
            new IntakePlaces(_home, Path.Combine(_dirs.CacheHome, "yar"), _dirs.UserApplicationsDir, Path.Combine(_dirs.DataHome, "yar", "icons")),
            _manifests, new ProcessRunner(), _trash, new BundleInspector(new LibraryFinder()));
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly IProgress<OperationProgress> Quiet = new Progress<OperationProgress>();

    private string Apps => Path.Combine(_home, "Applications");

    private string Downloads(string name) => Path.Combine(Directory.CreateDirectory(Path.Combine(_home, "Downloads")).FullName, name);

    private ManualAppsProvider Provider() => ManualAppsProvider.ForUser(_dirs, "/usr/bin:/bin", _manifests, _trash);

    private async Task<IReadOnlyList<AppInfo>> ClaimAsync()
    {
        var launchers = new DesktopEntryScanner(_dirs).Scan();
        return await Provider().ClaimAppsAsync(launchers, Token);
    }

    private string MakeTarGz(string name, Action<TarWriter> fill)
    {
        var path = Downloads(name);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        fill(tar);
        return path;
    }

    private static void AddFile(TarWriter tar, string name, byte[] content, bool runnable = false)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(content),
            Mode = runnable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.SetUser
                : UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        tar.WriteEntry(entry);
    }

    private string MakeAppArchive() => MakeTarGz("cool-tool-2.4.1-linux-x86_64.tar.gz", tar =>
    {
        AddFile(tar, "cool-tool-2.4.1/bin/cool-tool", File.ReadAllBytes(RealProgram), runnable: true);
        AddFile(tar, "cool-tool-2.4.1/cool-tool.png", Png);
        AddFile(tar, "cool-tool-2.4.1/README.md", "hello"u8.ToArray());
        AddFile(tar, "cool-tool-2.4.1/cool-tool.desktop",
            "[Desktop Entry]\nType=Application\nName=Cool Tool\nComment=Does cool things\nExec=cool-tool %F\nIcon=cool-tool\nCategories=Utility;\n"u8.ToArray());
        tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "cool-tool-2.4.1/run") { LinkName = "bin/cool-tool" });
        tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "cool-tool-2.4.1/escape") { LinkName = "../../../../etc/passwd" });
    });

    private string MakeAppImage(string name = "Shiny_App-1.2.3-x86_64.AppImage")
    {
        var bytes = File.ReadAllBytes(RealProgram);
        bytes[8] = (byte)'A';
        bytes[9] = (byte)'I';
        bytes[10] = 2;
        var path = Downloads(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData("krita-5.2.9-x86_64.AppImage", "Krita")]
    [InlineData("Obsidian-1.5.3.AppImage", "Obsidian")]
    [InlineData("balenaEtcher-linux-x64-1.19.21.zip", "balenaEtcher")]
    [InlineData("cool-tool-2.4.1-linux-x86_64.tar.gz", "Cool tool")]
    [InlineData("LM_Studio.AppImage", "LM Studio")]
    [InlineData("1.2.3.zip", "App")]
    public void A_name_is_made_from_the_file_name(string fileName, string expected)
    {
        Assert.Equal(expected, BundleIntake.CleanName(fileName));
    }

    [Fact]
    public void Files_are_told_apart_by_what_is_in_them_not_by_their_name()
    {
        static BundleKind Kind(byte[] start, string name) => FileIdentifier.Identify(start, name).Kind;
        var elf = File.ReadAllBytes(RealProgram)[..64];
        var appImage = (byte[])elf.Clone();
        "AI\u0002"u8.CopyTo(appImage.AsSpan(8));

        Assert.Equal(BundleKind.Program, Kind(elf, "innocent.zip"));
        Assert.Equal(BundleKind.AppImage, Kind(appImage, "x"));
        Assert.Equal(BundleKind.Archive, Kind("PK\u0003\u0004rest"u8.ToArray(), "x.AppImage"));
        Assert.Equal(BundleKind.Archive, Kind([0x1F, 0x8B, 8, 0], "x"));
        Assert.Equal(BundleKind.Package, Kind([0xED, 0xAB, 0xEE, 0xDB, 3, 0], "x"));
        Assert.Equal(BundleKind.Package, Kind("!<arch>\ndebian-binary   "u8.ToArray(), "x"));
        Assert.Equal(BundleKind.FlatpakFile, Kind("[Flatpak Ref]\nName=org.x.Y\n"u8.ToArray(), "x"));
        Assert.Equal(BundleKind.FlatpakFile, Kind([1, 2, 3, 4], "thing.flatpak"));

        foreach (var (start, name, said) in new (byte[], string, string)[]
        {
            ("MZ\u0090\u0000"u8.ToArray(), "setup.zip", "Windows"),
            ("#!/bin/sh\necho hi"u8.ToArray(), "install.run", "script"),
            ("<!DOCTYPE html><html>"u8.ToArray(), "app.AppImage", "web page"),
            ("hsqs\u0001\u0002"u8.ToArray(), "x.snap", "Snap"),
            ("[Flatpak Repo]\nUrl=x"u8.ToArray(), "x", "source"),
            ("just some text"u8.ToArray(), "notes.txt", "can't tell"),
        })
        {
            var identity = FileIdentifier.Identify(start, name);
            Assert.Equal(BundleKind.Unsupported, identity.Kind);
            Assert.Contains(said, identity.WhyNot);
        }
    }

    [Fact]
    public void A_program_says_which_processor_it_is_for_and_what_it_asks_for()
    {
        var real = ElfFile.Read("/usr/bin/env") ?? ElfFile.Read("/bin/ls");
        var garbage = Downloads("garbage");
        File.WriteAllBytes(garbage, [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, .. Enumerable.Repeat((byte)0xFF, 200)]);

        Assert.NotNull(real);
        Assert.Equal(BundleInspector.ThisMachine, real.Machine);
        Assert.True(real.IsProgram);
        Assert.Contains(real.Needed, name => name.StartsWith("libc.", StringComparison.Ordinal));
        Assert.True(File.Exists(real.Interpreter));
        Assert.True(new LibraryFinder().Has(real.Needed[0], real));
        Assert.False(new LibraryFinder().Has("libyar-does-not-exist.so.9", real));
        // A file that only starts like a program is read without trouble, and tells nothing.
        Assert.Empty(ElfFile.Read(garbage)?.Needed ?? []);
    }

    [Fact]
    public async Task An_archive_becomes_an_app_in_its_own_folder_with_a_menu_entry_and_a_note()
    {
        var archive = MakeAppArchive();

        var report = await _intake.InspectAsync(archive, null, Token);

        Assert.Null(report.Blocked);
        Assert.Equal(BundleKind.Archive, report.Kind);
        Assert.Equal("Cool Tool", report.SuggestedName);
        Assert.Equal("Does cool things", report.Summary);
        Assert.Equal("bin/cool-tool", report.Programs[0].Path);
        Assert.EndsWith("cool-tool.png", report.IconPath);
        Assert.Contains(report.Checks, c => c.State == CheckState.Good && c.Text.Contains("processor"));
        Assert.Contains(report.Checks, c => c.State == CheckState.Warning && c.Text.Contains("left out 1"));
        Assert.False(Directory.Exists(Apps));

        var choices = new IntakeChoices("Cool Tool", Apps) { IconPath = report.IconPath, HashVerified = true };
        var plan = await _intake.PlanAsync(report, choices, Token);
        var added = await _intake.AddAsync(report, choices, plan, Quiet, Token);

        var folder = Path.Combine(Apps, "cool-tool");
        var program = Path.Combine(folder, "bin", "cool-tool");
        Assert.Equal("yar-cool-tool.desktop", added.AppId);
        Assert.True(File.Exists(program));
        // Runnable, but the set-user-ID bit the archive asked for is gone.
        Assert.True(File.GetUnixFileMode(program).HasFlag(UnixFileMode.UserExecute));
        Assert.False(File.GetUnixFileMode(program).HasFlag(UnixFileMode.SetUser));
        Assert.False(File.Exists(Path.Combine(folder, "escape")));
        Assert.Equal("bin/cool-tool", new FileInfo(Path.Combine(folder, "run")).LinkTarget);

        var launcher = DesktopEntryParser.ParseFile(Path.Combine(_dirs.UserApplicationsDir, "yar-cool-tool.desktop"), "yar-cool-tool.desktop")!;
        Assert.Equal("Cool Tool", launcher.Name);
        Assert.Equal(program, DesktopExec.ResolveProgram(launcher.Exec, []));
        Assert.Equal(Path.Combine(_dirs.DataHome, "yar", "icons", "cool-tool.png"), launcher.Icon);
        Assert.Equal(["Utility"], launcher.Categories);
        // The archive is still where the user had it, and nothing is left in the cache.
        Assert.True(File.Exists(archive));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dirs.CacheHome, "yar", "intake")));

        var app = Assert.Single(await ClaimAsync(), a => a.Name == "Cool Tool");
        Assert.Equal(folder, app.InstallPath);
        Assert.Equal("true", app.Technical["hash-verified"]);
        Assert.Equal(report.Sha256, app.Technical["sha256"]);
        Assert.NotNull(app.InstalledAt);
    }

    [Fact]
    public async Task An_added_app_is_removed_exactly_to_the_Trash_and_can_come_back()
    {
        var report = await _intake.InspectAsync(MakeAppArchive(), null, Token);
        var choices = new IntakeChoices("Cool Tool", Apps) { IconPath = report.IconPath };
        await _intake.AddAsync(report, choices, await _intake.PlanAsync(report, choices, Token), Quiet, Token);
        var provider = Provider();
        var app = Assert.Single(await ClaimAsync(), a => a.Name == "Cool Tool");

        Assert.Null(provider.WhyNotRemovable(app));
        var plan = await provider.PlanRemovalAsync(app, Token);
        var removed = await provider.RemoveAsync(plan, removeData: false, Quiet, Token);

        Assert.Equal(4, removed.Trashed.Count);
        Assert.False(Directory.Exists(Path.Combine(Apps, "cool-tool")));
        Assert.Empty(Directory.EnumerateFiles(_dirs.UserApplicationsDir));
        Assert.DoesNotContain(await ClaimAsync(), a => a.Name == "Cool Tool");

        foreach (var item in removed.Trashed)
            _trash.Restore(item);

        Assert.Single(await ClaimAsync(), a => a.Name == "Cool Tool" && a.Technical.ContainsKey("added-by-yar"));
    }

    [Fact]
    public async Task An_AppImage_is_one_runnable_file_and_a_second_one_of_the_same_name_gets_its_own()
    {
        var file = MakeAppImage();
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var report = await _intake.InspectAsync(file, "https://example.org/Shiny.AppImage", Token);
        var choices = new IntakeChoices(report.SuggestedName, Apps);
        var plan = await _intake.PlanAsync(report, choices, Token);
        var first = await _intake.AddAsync(report, choices, plan, Quiet, Token);
        var again = await _intake.InspectAsync(file, null, Token);
        var second = await _intake.AddAsync(again, choices, await _intake.PlanAsync(again, choices, Token), Quiet, Token);

        Assert.Equal(BundleKind.AppImage, report.Kind);
        Assert.Equal("Shiny App", report.SuggestedName);
        Assert.Null(report.Blocked);
        Assert.Contains("not compared with a hash", string.Join(' ', plan.Notes));
        var installed = Path.Combine(Apps, "shiny-app.AppImage");
        Assert.True(File.GetUnixFileMode(installed).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal("yar-shiny-app.desktop", first.AppId);
        Assert.Equal("yar-shiny-app-2.desktop", second.AppId);
        Assert.True(File.Exists(Path.Combine(Apps, "shiny-app-2.AppImage")));
        Assert.Contains("Exec=\"" + installed + "\" %U", File.ReadAllText(Path.Combine(_dirs.UserApplicationsDir, "yar-shiny-app.desktop")));
    }

    [Fact]
    public async Task A_changed_file_is_noticed_when_checked_again()
    {
        var report = await _intake.InspectAsync(MakeAppImage(), null, Token);
        var choices = new IntakeChoices(report.SuggestedName, Apps) { DeleteOriginal = true };
        await _intake.AddAsync(report, choices, await _intake.PlanAsync(report, choices, Token), Quiet, Token);
        var provider = Provider();
        var app = Assert.Single(await ClaimAsync(), a => a.Name == "Shiny App");

        var before = await provider.CheckAsync(app, Token);
        File.AppendAllText(app.InstallPath!, "tampered");
        var after = await provider.CheckAsync(app, Token);

        // Moved rather than copied, as asked.
        Assert.False(File.Exists(report.SourcePath));
        Assert.True(before.Unchanged);
        Assert.False(after.Unchanged);
        Assert.NotEqual(after.Sha256WhenAdded, after.Sha256Now);
    }

    [Fact]
    public async Task Source_code_and_Windows_programs_are_explained_not_installed()
    {
        var source = MakeTarGz("thing-1.0.tar.gz", tar =>
        {
            AddFile(tar, "thing-1.0/configure", "#!/bin/sh\n"u8.ToArray());
            AddFile(tar, "thing-1.0/Makefile", "all:\n"u8.ToArray());
            AddFile(tar, "thing-1.0/main.c", "int main(){}"u8.ToArray());
        });
        var windows = Downloads("setup.zip");
        using (var zip = ZipFile.Open(windows, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("App/app.exe").Open();
            entry.Write("MZ"u8);
        }

        var sourceReport = await _intake.InspectAsync(source, null, Token);
        var windowsReport = await _intake.InspectAsync(windows, null, Token);
        var plan = await _intake.PlanAsync(sourceReport, new IntakeChoices("Thing", Apps), Token);

        Assert.Contains("source code", sourceReport.Blocked);
        Assert.Contains("Windows", windowsReport.Blocked);
        Assert.NotNull(plan.Blocked);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _intake.AddAsync(sourceReport, new IntakeChoices("Thing", Apps), plan, Quiet, Token));
        Assert.False(Directory.Exists(Apps));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/tmp/yar-absolute.txt")]
    [InlineData("app/../../outside.txt")]
    public async Task An_archive_that_reaches_outside_its_folder_is_refused_and_leaves_nothing(string hostileName)
    {
        var zipPath = Downloads("hostile.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var good = zip.CreateEntry("app/readme.txt").Open())
                good.Write("fine"u8);
            using var bad = zip.CreateEntry(hostileName).Open();
            bad.Write("gotcha"u8);
        }

        var report = await _intake.InspectAsync(zipPath, null, Token);

        Assert.Contains("outside its own folder", report.Blocked);
        Assert.False(File.Exists(Path.Combine(_dirs.CacheHome, "yar", "outside.txt")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dirs.CacheHome, "yar", "intake")));
    }

    [Fact]
    public void Unpacking_stops_at_the_size_limit_and_never_writes_through_a_link()
    {
        var big = MakeTarGz("big.tar.gz", tar => AddFile(tar, "big.bin", new byte[200_000]));
        var sneaky = MakeTarGz("sneaky.tar.gz", tar =>
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "inside") { LinkName = "." });
            AddFile(tar, "inside/planted.txt", "x"u8.ToArray());
        });

        Assert.Throws<InvalidDataException>(() => ArchiveUnpacker.Unpack(big, "tar.gz", Path.Combine(_home, "u1"), 100_000, Token));
        Assert.Throws<InvalidDataException>(() => ArchiveUnpacker.Unpack(sneaky, "tar.gz", Path.Combine(_home, "u2"), 100_000, Token));
        Assert.False(File.Exists(Path.Combine(_home, "u2", "planted.txt")));
    }

    [Fact]
    public async Task Odd_folders_and_missing_names_are_refused_in_the_plan()
    {
        var report = await _intake.InspectAsync(MakeAppImage(), null, Token);

        Assert.NotNull((await _intake.PlanAsync(report, new IntakeChoices("  ", Apps), Token)).Blocked);
        Assert.NotNull((await _intake.PlanAsync(report, new IntakeChoices("X", "relative/folder"), Token)).Blocked);
        Assert.NotNull((await _intake.PlanAsync(report, new IntakeChoices("X", _home + "/we\"ird"), Token)).Blocked);
        Assert.NotNull((await _intake.PlanAsync(report, new IntakeChoices("X", Apps) { IconPath = report.SourcePath }, Token)).Blocked);
        Assert.Contains("outside your home folder", string.Join(' ', (await _intake.PlanAsync(report, new IntakeChoices("X", "/tmp/yar-elsewhere"), Token)).Warnings));
    }

    [Fact]
    public async Task Of_an_app_Yar_did_not_add_only_what_is_certainly_its_own_is_removed()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_home, "tools", "thing")).FullName;
        File.Copy(RealProgram, Path.Combine(folder, "thing"));
        File.WriteAllText(Path.Combine(folder, "my-notes.txt"), "precious");
        Directory.CreateDirectory(_dirs.UserApplicationsDir);
        var launcher = Path.Combine(_dirs.UserApplicationsDir, "thing.desktop");
        File.WriteAllText(launcher, $"[Desktop Entry]\nType=Application\nName=Thing\nExec={folder}/thing\n");
        var provider = Provider();
        var app = Assert.Single(await ClaimAsync(), a => a.Name == "Thing");

        var plan = await provider.PlanRemovalAsync(app, Token);
        var forged = plan with { Technical = new Dictionary<string, string> { ["paths"] = Path.Combine(folder, "my-notes.txt"), ["note"] = "" } };

        Assert.Equal(launcher, Assert.Single(plan.Items).Detail);
        Assert.Contains(folder, string.Join(' ', plan.Notes));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RemoveAsync(forged, false, Quiet, Token));
        await provider.RemoveAsync(plan, false, Quiet, Token);
        Assert.False(File.Exists(launcher));
        Assert.True(File.Exists(Path.Combine(folder, "my-notes.txt")));
    }

    [Fact]
    public async Task When_adding_fails_half_way_nothing_is_left_and_a_moved_file_goes_back()
    {
        var file = MakeAppImage();
        var report = await _intake.InspectAsync(file, null, Token);
        var choices = new IntakeChoices(report.SuggestedName, Apps) { DeleteOriginal = true };
        var plan = await _intake.PlanAsync(report, choices, Token);
        // The app-menu folder cannot be written to, so the last step fails.
        Directory.CreateDirectory(Path.GetDirectoryName(_dirs.UserApplicationsDir)!);
        File.WriteAllText(_dirs.UserApplicationsDir, "in the way");

        await Assert.ThrowsAnyAsync<IOException>(() => _intake.AddAsync(report, choices, plan, Quiet, Token));

        Assert.True(File.Exists(file));
        Assert.False(File.Exists(Path.Combine(Apps, "shiny-app.AppImage")));
        Assert.Empty(_manifests.ReadAll());
    }

    [Fact]
    public async Task Something_that_appears_at_the_destination_after_the_preview_is_never_deleted()
    {
        var report = await _intake.InspectAsync(MakeAppImage(), null, Token);
        var choices = new IntakeChoices(report.SuggestedName, Apps);
        var plan = await _intake.PlanAsync(report, choices, Token);
        var other = await _intake.InspectAsync(report.SourcePath, null, Token);
        await _intake.AddAsync(other, choices, await _intake.PlanAsync(other, choices, Token), Quiet, Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _intake.AddAsync(report, choices, plan, Quiet, Token));

        Assert.True(File.Exists(Path.Combine(Apps, "shiny-app.AppImage")));
        Assert.Single(_manifests.ReadAll());
    }

    [Fact]
    public async Task Only_the_one_wrapping_folder_is_taken_off_an_archive()
    {
        var archive = MakeTarGz("tiny-1.0.tar.gz", tar => AddFile(tar, "tiny-1.0/bin/tiny", File.ReadAllBytes(RealProgram), runnable: true));

        var report = await _intake.InspectAsync(archive, null, Token);

        Assert.Equal("bin/tiny", Assert.Single(report.Programs).Path);
    }
}
