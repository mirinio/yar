namespace Yar.Core;

/// <summary>
/// The user's Trash. Things Yar removes go here where possible, so that they can be looked at
/// and put back, by Yar or from the file manager.
/// </summary>
public interface ITrash
{
    /// <summary>Why this path cannot go to the Trash, in plain words; null when it can.</summary>
    string? WhyNot(string path);

    /// <exception cref="InvalidOperationException">The path cannot be trashed; the message says why.</exception>
    TrashedItem Move(string path);

    /// <summary>Puts a trashed item back where it was. Never overwrites what is there now.</summary>
    /// <exception cref="InvalidOperationException">It cannot be put back; the message says why.</exception>
    void Restore(TrashedItem item);

    /// <summary>True while the item is still in the Trash.</summary>
    bool Holds(TrashedItem item);
}
