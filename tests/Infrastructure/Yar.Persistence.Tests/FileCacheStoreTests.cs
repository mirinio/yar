namespace Yar.Persistence.Tests;

public sealed class FileCacheStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("yar-cache-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir => Path.Combine(_root, "yar");

    [Fact]
    public void Round_trips_and_replaces_entries()
    {
        var store = new FileCacheStore(Dir);

        Assert.Null(store.Read("snapshot.json"));
        store.Write("snapshot.json", "first");
        store.Write("snapshot.json", "second \u00e4\u00f6");

        Assert.Equal("second \u00e4\u00f6", store.Read("snapshot.json"));
        Assert.Equal(["snapshot.json"], Directory.EnumerateFiles(Dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Entries_are_private_to_the_user()
    {
        new FileCacheStore(Dir).Write("a.json", "x");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Dir, "a.json")));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Dir));
    }

    [Fact]
    public void Oversized_entries_are_ignored()
    {
        Directory.CreateDirectory(Dir);
        using (var file = File.Create(Path.Combine(Dir, "big.json")))
            file.SetLength(FileCacheStore.MaxEntryBytes + 1);

        Assert.Null(new FileCacheStore(Dir).Read("big.json"));
    }

    [Fact]
    public void A_directory_that_cannot_be_written_is_not_an_error()
    {
        // A file where the cache directory should be: creating the directory fails.
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "");
        var store = new FileCacheStore(blocked);

        store.Write("a.json", "x");

        Assert.Null(store.Read("a.json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape.json")]
    [InlineData("/etc/passwd")]
    [InlineData(".hidden")]
    [InlineData("a/b.json")]
    public void Entry_names_cannot_leave_the_directory(string name)
    {
        var store = new FileCacheStore(Dir);

        Assert.Throws<ArgumentException>(() => store.Write(name, "x"));
        Assert.Throws<ArgumentException>(() => store.Read(name));
    }
}
