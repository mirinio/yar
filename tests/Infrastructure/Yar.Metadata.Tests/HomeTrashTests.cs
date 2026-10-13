namespace Yar.Metadata.Tests;

public sealed class HomeTrashTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("yar-trash-").FullName;
    private readonly HomeTrash _trash;

    public HomeTrashTests() => _trash = new HomeTrash(_home, Path.Combine(_home, ".local", "share"));

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Make(string relative, bool folder = false)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(folder ? path : Path.GetDirectoryName(path)!);
        if (folder)
            File.WriteAllText(Path.Combine(path, "inside.txt"), "data");
        else
            File.WriteAllText(path, "data");
        return path;
    }

    [Fact]
    public void A_folder_goes_to_the_Trash_with_a_note_and_comes_back()
    {
        var folder = Make("Applications/My App (1)", folder: true);

        var item = _trash.Move(folder);

        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(item.TrashPath, "inside.txt")));
        var note = File.ReadAllLines(item.InfoPath);
        Assert.Equal("[Trash Info]", note[0]);
        Assert.Equal($"Path={_home}/Applications/My%20App%20%281%29", note[1]);
        Assert.Matches(@"^DeletionDate=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$", note[2]);
        Assert.True(_trash.Holds(item));

        _trash.Restore(item);

        Assert.True(File.Exists(Path.Combine(folder, "inside.txt")));
        Assert.False(File.Exists(item.InfoPath));
        Assert.False(_trash.Holds(item));
    }

    [Fact]
    public void Two_things_with_the_same_name_do_not_clash()
    {
        var first = _trash.Move(Make("a/app.AppImage"));
        var second = _trash.Move(Make("b/app.AppImage"));

        Assert.NotEqual(first.TrashPath, second.TrashPath);
        Assert.Equal(Path.GetFileName(second.TrashPath) + ".trashinfo", Path.GetFileName(second.InfoPath));
        Assert.True(_trash.Holds(first) && _trash.Holds(second));
    }

    [Fact]
    public void Restoring_never_overwrites_what_is_there_now()
    {
        var file = Make("a/app.AppImage");
        var item = _trash.Move(file);
        File.WriteAllText(file, "newer");

        Assert.Throws<InvalidOperationException>(() => _trash.Restore(item));
        Assert.Equal("newer", File.ReadAllText(file));
        Assert.True(_trash.Holds(item));
    }

    [Fact]
    public void Links_and_things_outside_home_are_refused()
    {
        var target = Make("real", folder: true);
        var link = Path.Combine(_home, "link");
        Directory.CreateSymbolicLink(link, target);
        var throughLink = Path.Combine(link, "inside.txt");

        Assert.NotNull(_trash.WhyNot(link));
        Assert.NotNull(_trash.WhyNot(throughLink));
        Assert.NotNull(_trash.WhyNot("/etc/hostname"));
        Assert.NotNull(_trash.WhyNot(_home));
        Assert.NotNull(_trash.WhyNot(Path.Combine(_home, "a", "..", "real")));
        Assert.NotNull(_trash.WhyNot(Path.Combine(_home, "missing")));
        Assert.Throws<InvalidOperationException>(() => _trash.Move(link));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void A_made_up_record_cannot_move_things_that_are_not_in_the_Trash()
    {
        var victim = Make("victim.txt");

        var forged = new Yar.Core.TrashedItem(Path.Combine(_home, "elsewhere.txt"), victim, victim);

        Assert.False(_trash.Holds(forged));
        Assert.Throws<InvalidOperationException>(() => _trash.Restore(forged));
        Assert.True(File.Exists(victim));
    }
}
