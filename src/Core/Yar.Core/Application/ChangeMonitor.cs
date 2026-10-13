namespace Yar.Core;

/// <summary>
/// Gathers change notices from all sources and passes them on once things have been quiet for a
/// moment. An install touches many files in a burst; that should cause one refresh, not fifty.
/// </summary>
public sealed class ChangeMonitor : IDisposable
{
    private readonly List<IChangeSource> _sources;
    private readonly TimeSpan _quietPeriod;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private ChangeKinds _pending;
    private bool _disposed;

    public ChangeMonitor(IEnumerable<IChangeSource> sources, TimeSpan quietPeriod, TimeProvider? time = null)
    {
        _sources = sources.ToList();
        _quietPeriod = quietPeriod;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (var source in _sources)
            source.Changed += OnChanged;
    }

    /// <summary>Raised on a timer thread with every kind of change seen since the last notice.</summary>
    public event Action<ChangeKinds>? RefreshNeeded;

    public void Start()
    {
        foreach (var source in _sources)
            source.Start();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending = ChangeKinds.None;
        }
        foreach (var source in _sources)
        {
            source.Changed -= OnChanged;
            source.Dispose();
        }
        _timer.Dispose();
    }

    private void OnChanged(ChangeKinds kinds)
    {
        lock (_gate)
        {
            if (_disposed || kinds == ChangeKinds.None)
                return;
            _pending |= kinds;
            // Every new notice pushes the deadline back, so a burst ends in a single flush.
            _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        ChangeKinds kinds;
        lock (_gate)
        {
            kinds = _pending;
            _pending = ChangeKinds.None;
            if (_disposed || kinds == ChangeKinds.None)
                return;
        }
        RefreshNeeded?.Invoke(kinds);
    }
}
