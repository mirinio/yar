using Yar.Core;

namespace Yar.App;

/// <summary>"History": everything Yar changed on this computer, newest first, with a way back where there is one.</summary>
internal sealed class HistoryPage
{
    // More than anyone scrolls through; the file itself keeps everything.
    private const int Shown = 200;

    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Adw.StatusPage _empty = Adw.StatusPage.New();
    private Adw.PreferencesPage? _page;

    public HistoryPage()
    {
        _empty.IconName = "document-open-recent-symbolic";
        _empty.Title = T._("Nothing changed yet");
        _empty.Description = T._(
            "Whenever I install, remove or switch something for you, I write it down here, so you can see what " +
            "happened and undo it.");
        _stack.AddNamed(_empty, "empty");
    }

    public Gtk.Widget Widget => _stack;

    public void Show(Ui ui)
    {
        if (_page is not null)
            _stack.Remove(_page);
        _page = Adw.PreferencesPage.New();
        _stack.AddNamed(_page, "list");

        var history = ui.Host.Journal.Read();
        if (history.Count == 0)
        {
            _stack.VisibleChildName = "empty";
            return;
        }

        var group = Widgets.Group(
            T._("What I changed"),
            T._("Only changes made through me are listed. What other tools did is not."),
            T._("Undo reverses one change: an installed app is removed again, a removed one is installed again " +
                "and its data comes back from the Trash if you had me remove that too. Some things can't be " +
                "undone; then I say why."));
        foreach (var record in history.Reverse().Take(Shown))
            group.Add(Row(ui, record, history));
        _page.Add(group);
        _stack.VisibleChildName = "list";
    }

    private static Adw.ActionRow Row(Ui ui, ChangeRecord record, IReadOnlyList<ChangeRecord> history)
    {
        var row = Adw.ActionRow.New();
        // Names come from files and sources; never let them be read as markup.
        row.UseMarkup = false;
        // Only a change that went through is told in the past tense.
        row.Title = record.Outcome == ChangeOutcome.Done ? ChangeText.Done(record) : ChangeText.Running(record);
        row.SubtitleLines = 0;

        var lines = new List<string> { Presentation.Ago(record.At) };
        if (record.UndoOf is not null)
            lines[0] += " · " + T._("this was an undo");
        if (ChangeText.Ending(record) is { } ending)
            lines.Add(ending);
        if (record.Trashed.Count > 0)
            lines.Add(T.F(record.Trashed.Count == 1 ? "{0} thing went to the Trash" : "{0} things went to the Trash", record.Trashed.Count));
        if (ui.Technical && record.Origin is not null)
            lines.Add(record.Origin);

        row.AddPrefix(Gtk.Image.NewFromIconName(record.Outcome switch
        {
            ChangeOutcome.Done => "emblem-ok-symbolic",
            ChangeOutcome.Cancelled => "process-stop-symbolic",
            _ => "dialog-warning-symbolic",
        }));

        var whyNot = ui.Host.Make.WhyNoUndo(record, history);
        if (whyNot is null)
        {
            var undo = Gtk.Button.NewWithLabel(T._("Undo"));
            undo.Valign = Gtk.Align.Center;
            undo.OnClicked += (_, _) => ChangeDialog.Undo(ui, record, undo);
            row.AddSuffix(undo);
        }
        else if (record.Outcome == ChangeOutcome.Done)
        {
            lines.Add(T._(whyNot));
        }
        row.Subtitle = string.Join("\n", lines);
        return row;
    }
}
