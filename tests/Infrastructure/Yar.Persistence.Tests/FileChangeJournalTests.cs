using Yar.Core;

namespace Yar.Persistence.Tests;

public sealed class FileChangeJournalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("yar-journal-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File_ => Path.Combine(_root, "yar", "history.jsonl");

    private static ChangeRecord Record(string id, ChangeOutcome outcome = ChangeOutcome.Started) =>
        new(id, new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero), ChangeKind.RemoveApp, "flatpak", "user:app/x/y/z", "Ä pp")
        {
            Outcome = outcome,
            Origin = "user:flathub",
            Undo = new Dictionary<string, string> { ["ref"] = "app/x/y/z" },
            Trashed = [new("/home/u/.var/app/x", "/home/u/.local/share/Trash/files/x", "/home/u/.local/share/Trash/info/x.trashinfo")],
        };

    [Fact]
    public void Records_come_back_as_written_with_the_last_state_of_each()
    {
        var journal = new FileChangeJournal(File_);
        journal.Write(Record("a"));
        journal.Write(Record("b"));
        journal.Write(Record("a", ChangeOutcome.Done) with { Problem = null });

        var read = new FileChangeJournal(File_).Read();

        Assert.Equal(["a", "b"], read.Select(r => r.Id));
        Assert.Equal(ChangeOutcome.Done, read[0].Outcome);
        Assert.Equal(ChangeOutcome.Started, read[1].Outcome);
        Assert.Equal("Ä pp", read[0].Name);
        Assert.Equal("app/x/y/z", read[0].Undo["ref"]);
        Assert.Equal("/home/u/.var/app/x", read[0].Trashed[0].OriginalPath);
        Assert.Equal(3, File.ReadAllLines(File_).Length);
    }

    [Fact]
    public void The_file_is_private_to_the_user()
    {
        new FileChangeJournal(File_).Write(Record("a"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(File_));
    }

    [Fact]
    public void Damaged_lines_are_skipped_and_the_rest_still_count()
    {
        var journal = new FileChangeJournal(File_);
        journal.Write(Record("a"));
        File.AppendAllText(File_, "{\"id\":\"cut-off\",\"at\":\"2026-\n\nnot json\n{\"id\":\"\"}\n{\"id\":\"x\",\"trashed\":null}\n");
        journal.Write(Record("b"));

        Assert.Equal(["a", "b"], journal.Read().Select(r => r.Id));
    }

    [Fact]
    public void A_missing_file_means_no_history()
    {
        Assert.Empty(new FileChangeJournal(File_).Read());
    }
}
