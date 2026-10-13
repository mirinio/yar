namespace Yar.Core;

/// <summary>
/// A place to keep things Yar can work out again: saved results that make the next start faster.
/// Losing an entry is never an error, and nothing read from here is trusted without checking.
/// </summary>
public interface ICacheStore
{
    /// <summary>Returns null when the entry is absent, unreadable or unreasonably large.</summary>
    string? Read(string name);

    /// <summary>Replaces the entry as a whole or not at all. Failing to write is not an error.</summary>
    void Write(string name, string content);
}
