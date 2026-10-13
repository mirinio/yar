using Yar.Core;

namespace Yar.Metadata.Tests;

public sealed class FileChangeSourceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly string _root = Directory.CreateTempSubdirectory("yar-watch-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<ChangeKinds?> FirstChange(FileChangeSource source, Action act)
    {
        var seen = new TaskCompletionSource<ChangeKinds>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += kinds => seen.TrySetResult(kinds);
        source.Start();
        act();
        var finished = await Task.WhenAny(seen.Task, Task.Delay(Patience, TestContext.Current.CancellationToken));
        return finished == seen.Task ? await seen.Task : null;
    }

    [Fact]
    public async Task A_new_launcher_is_noticed()
    {
        var applications = Path.Combine(_root, "applications");
        Directory.CreateDirectory(Path.Combine(applications, "kde"));
        using var source = new FileChangeSource(
            [applications, Path.Combine(_root, "missing")], "*.desktop", includeSubdirectories: true, ChangeKinds.Apps);

        var change = await FirstChange(source, () =>
            File.WriteAllText(Path.Combine(applications, "kde", "new.desktop"), "[Desktop Entry]"));

        Assert.Equal(ChangeKinds.Apps, change);
    }

    [Fact]
    public async Task Touching_the_flatpak_marker_is_noticed()
    {
        var marker = Path.Combine(_root, ".changed");
        File.WriteAllText(marker, "");
        using var source = FileChangeSource.ForFlatpak([_root]);

        var change = await FirstChange(source, () => File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddMinutes(1)));

        Assert.Equal(ChangeKinds.All, change);
    }

    [Fact]
    public async Task Other_files_are_not_reported()
    {
        var applications = Path.Combine(_root, "applications");
        Directory.CreateDirectory(applications);
        using var source = new FileChangeSource([applications], "*.desktop", includeSubdirectories: true, ChangeKinds.Apps);
        var seen = false;
        source.Changed += _ => seen = true;
        source.Start();

        File.WriteAllText(Path.Combine(applications, "mimeinfo.cache"), "x");
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.False(seen);
    }

    [Fact]
    public void Starting_with_nothing_to_watch_is_fine()
    {
        using var source = new FileChangeSource([Path.Combine(_root, "missing")], "*.desktop", true, ChangeKinds.Apps);

        source.Start();
    }
}
