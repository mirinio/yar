namespace Yar.Core.Tests;

public class ChangeMonitorTests
{
    private sealed class FakeSource : IChangeSource
    {
        public event Action<ChangeKinds>? Changed;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public void Start() => Started = true;
        public void Dispose() => Disposed = true;
        public void Raise(ChangeKinds kinds) => Changed?.Invoke(kinds);
    }

    /// <summary>A clock whose timers only fire when the test says the quiet period has passed.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public int Restarts => _timers.Sum(t => t.Restarts);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }

        public void LetQuietPeriodPass()
        {
            foreach (var timer in _timers)
                timer.FireIfArmed();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _armed;
            private bool _disposed;

            public int Restarts { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _armed = dueTime != Timeout.InfiniteTimeSpan;
                Restarts++;
                return true;
            }

            public void FireIfArmed()
            {
                if (!_armed || _disposed)
                    return;
                _armed = false;
                callback(state);
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public void A_burst_of_changes_becomes_one_notice_with_all_kinds()
    {
        var files = new FakeSource();
        var daemon = new FakeSource();
        var time = new ManualTime();
        using var monitor = new ChangeMonitor([files, daemon], TimeSpan.FromSeconds(2), time);
        var notices = new List<ChangeKinds>();
        monitor.RefreshNeeded += notices.Add;

        for (var i = 0; i < 50; i++)
            files.Raise(ChangeKinds.Apps);
        daemon.Raise(ChangeKinds.Updates);
        Assert.Empty(notices);
        Assert.Equal(51, time.Restarts);

        time.LetQuietPeriodPass();
        Assert.Equal([ChangeKinds.Apps | ChangeKinds.Updates], notices);

        // Nothing new happened: no second notice.
        time.LetQuietPeriodPass();
        Assert.Single(notices);

        daemon.Raise(ChangeKinds.Sources);
        time.LetQuietPeriodPass();
        Assert.Equal(ChangeKinds.Sources, notices[1]);
    }

    [Fact]
    public void Starts_and_disposes_its_sources()
    {
        var source = new FakeSource();
        var time = new ManualTime();
        var monitor = new ChangeMonitor([source], TimeSpan.FromSeconds(2), time);
        var notices = new List<ChangeKinds>();
        monitor.RefreshNeeded += notices.Add;

        Assert.False(source.Started);
        monitor.Start();
        Assert.True(source.Started);

        source.Raise(ChangeKinds.Apps);
        monitor.Dispose();
        monitor.Dispose();

        Assert.True(source.Disposed);
        // A change that was waiting when Yar closed is dropped, and later ones are not heard.
        time.LetQuietPeriodPass();
        source.Raise(ChangeKinds.Apps);
        time.LetQuietPeriodPass();
        Assert.Empty(notices);
    }

    [Fact]
    public void An_empty_notice_is_ignored()
    {
        var source = new FakeSource();
        var time = new ManualTime();
        using var monitor = new ChangeMonitor([source], TimeSpan.FromSeconds(2), time);
        var notices = new List<ChangeKinds>();
        monitor.RefreshNeeded += notices.Add;

        source.Raise(ChangeKinds.None);
        time.LetQuietPeriodPass();

        Assert.Empty(notices);
    }
}
