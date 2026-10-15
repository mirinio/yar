namespace Yar.Core;

public enum ChangeKind
{
    InstallApp,
    RemoveApp,

    /// <summary>An app the user brought themselves, as a file or a link.</summary>
    AddApp,
    AddSource,
    RemoveSource,
    SwitchSourceOn,
    SwitchSourceOff,
    AllowThirdParty,
    DeclineThirdParty,

    /// <summary>Newer versions installed over what was there. Appended last: the names are what the saved history holds.</summary>
    Update,
}

public enum ChangeOutcome
{
    /// <summary>Begun and never reported back: Yar was closed or stopped in the middle.</summary>
    Started,
    Done,
    Failed,
    Cancelled,
}

/// <summary>Something that was moved to the Trash, with what is needed to put it back.</summary>
public sealed record TrashedItem(string OriginalPath, string TrashPath, string InfoPath);

/// <summary>
/// One thing Yar changed on this computer, or tried to. The record is what the History page shows
/// and what undo works from, so it carries everything needed to reverse the change.
/// </summary>
/// <param name="Target">What was changed, as its provider names it: an app ID, a source ID.</param>
/// <param name="Name">What to call it when talking to the user.</param>
public sealed record ChangeRecord(
    string Id, DateTimeOffset At, ChangeKind Kind, string ProviderId, string Target, string Name)
{
    public ChangeOutcome Outcome { get; init; } = ChangeOutcome.Started;

    /// <summary>Why it failed, in the backend's own words. Null unless <see cref="Outcome"/> is Failed.</summary>
    public string? Problem { get; init; }

    /// <summary>Where the thing came from: a source ID, a file name or a link.</summary>
    public string? Origin { get; init; }

    /// <summary>The SHA-256 of a file the user brought, and whether it was compared with the publisher's.</summary>
    public string? Checksum { get; init; }

    public bool ChecksumVerified { get; init; }

    /// <summary>The ID of the record this one reverses, when it is an undo.</summary>
    public string? UndoOf { get; init; }

    /// <summary>What the provider needs to reverse the change. Empty when it cannot be reversed.</summary>
    public IReadOnlyDictionary<string, string> Undo { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<TrashedItem> Trashed { get; init; } = [];

    public static ChangeRecord Begin(
        ChangeKind kind, string providerId, string target, string name, TimeProvider? time = null) =>
        new(Guid.NewGuid().ToString("N")[..12], (time ?? TimeProvider.System).GetUtcNow(), kind, providerId, target, name);
}
