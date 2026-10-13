namespace Yar.Core;

[Flags]
public enum ChangeKinds
{
    None = 0,
    Apps = 1,
    Sources = 2,
    Updates = 4,
    All = Apps | Sources | Updates,
}

/// <summary>Something that notices when software on this computer changes while Yar is open.</summary>
public interface IChangeSource : IDisposable
{
    /// <summary>Raised on any thread, possibly many times for one change.</summary>
    event Action<ChangeKinds>? Changed;

    /// <summary>Begins watching. Must not throw: a source that cannot watch simply stays silent.</summary>
    void Start();
}
