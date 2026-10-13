namespace Yar.Core;

/// <summary>What a running change can report and add to its own record.</summary>
public sealed class OperationContext(IProgress<OperationProgress> progress, CancellationToken ct)
{
    private readonly List<Func<ChangeRecord, ChangeRecord>> _amendments = [];

    public IProgress<OperationProgress> Progress => progress;

    public CancellationToken Ct => ct;

    /// <summary>Adds to the record once the work knows more, such as what went to the Trash.</summary>
    public void Amend(Func<ChangeRecord, ChangeRecord> change)
    {
        lock (_amendments)
            _amendments.Add(change);
    }

    internal ChangeRecord Apply(ChangeRecord record)
    {
        lock (_amendments)
            return _amendments.Aggregate(record, (current, change) => change(current));
    }
}

/// <summary>The change being carried out right now, for the window to show.</summary>
public sealed record RunningOperation(ChangeRecord Record, OperationProgress Progress, int Waiting);

/// <summary>
/// Carries out changes to the system one at a time and writes each into the journal: once when
/// it starts, so that a crash leaves a trace, and again with how it ended.
/// </summary>
public sealed class OperationRunner(IChangeJournal journal) : IDisposable
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Lock _gate = new();
    private RunningOperation? _current;
    private CancellationTokenSource? _cancel;
    private int _waiting;

    /// <summary>Raised on any thread whenever <see cref="Current"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Raised on any thread after a change ended, however it ended.</summary>
    public event Action<ChangeRecord>? Finished;

    public RunningOperation? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>Asks the running change to stop. Whether it can depends on how far it has come.</summary>
    public void CancelCurrent()
    {
        lock (_gate)
            _cancel?.Cancel();
    }

    /// <summary>
    /// Runs <paramref name="work"/> when it is its turn. Never throws for a failed change: the
    /// returned record says how it ended.
    /// </summary>
    public async Task<ChangeRecord> RunAsync(ChangeRecord record, Func<OperationContext, Task> work)
    {
        Interlocked.Increment(ref _waiting);
        await _turn.WaitAsync().ConfigureAwait(false);
        Interlocked.Decrement(ref _waiting);

        using var cancel = new CancellationTokenSource();
        var context = new OperationContext(new Reporter(this), cancel.Token);
        ChangeRecord ended;
        try
        {
            lock (_gate)
            {
                _cancel = cancel;
                _current = new(record, new(OperationPhase.Waiting), Volatile.Read(ref _waiting));
            }
            journal.Write(record);
            Changed?.Invoke();

            try
            {
                await work(context).ConfigureAwait(false);
                ended = context.Apply(record) with { Outcome = ChangeOutcome.Done };
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                ended = context.Apply(record) with { Outcome = ChangeOutcome.Cancelled };
            }
            // Whatever a backend throws, the change failed and the user is told why; it must not end Yar.
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ended = context.Apply(record) with { Outcome = ChangeOutcome.Failed, Problem = e.Message };
            }
            journal.Write(ended);
        }
        finally
        {
            lock (_gate)
            {
                _cancel = null;
                _current = null;
            }
            _turn.Release();
        }

        Changed?.Invoke();
        Finished?.Invoke(ended);
        return ended;
    }

    public void Dispose() => _turn.Dispose();

    private void Report(OperationProgress progress)
    {
        lock (_gate)
        {
            if (_current is null)
                return;
            _current = _current with { Progress = progress, Waiting = Volatile.Read(ref _waiting) };
        }
        Changed?.Invoke();
    }

    // Not Progress<T>: that posts to whichever thread created it, and reports would arrive late and out of order.
    private sealed class Reporter(OperationRunner runner) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => runner.Report(value);
    }
}
