namespace Yar.Core;

/// <summary>
/// The lasting record of what Yar changed. Unlike the cache, this cannot be worked out again:
/// losing it means losing the history and the ability to undo.
/// </summary>
public interface IChangeJournal
{
    /// <summary>
    /// Adds a record, or the newer state of one written before under the same ID. Failing to write
    /// is not an error for the caller: the change itself matters more than the note about it.
    /// </summary>
    void Write(ChangeRecord record);

    /// <summary>Every record in its latest state, oldest first. Lines that cannot be read are skipped.</summary>
    IReadOnlyList<ChangeRecord> Read();
}
