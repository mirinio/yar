namespace Yar.Core.Tests;

public sealed class AppChangesTests : IDisposable
{
    private sealed class MemoryJournal : IChangeJournal
    {
        public List<ChangeRecord> Lines { get; } = [];

        public void Write(ChangeRecord record)
        {
            lock (Lines)
                Lines.Add(record);
        }

        public IReadOnlyList<ChangeRecord> Read()
        {
            lock (Lines)
                return Lines.GroupBy(r => r.Id).Select(g => g.Last()).ToList();
        }
    }

    private sealed class FakeTrash : ITrash
    {
        public HashSet<string> Held { get; } = [];
        public List<string> Restored { get; } = [];

        public string? WhyNot(string path) => null;

        public TrashedItem Move(string path)
        {
            Held.Add(path);
            return new(path, "/trash/files/" + Path.GetFileName(path), "/trash/info/" + Path.GetFileName(path));
        }

        public bool Holds(TrashedItem item) => Held.Contains(item.OriginalPath);

        public void Restore(TrashedItem item)
        {
            Held.Remove(item.OriginalPath);
            Restored.Add(item.OriginalPath);
        }
    }

    private sealed class FakeProvider(ITrash trash) : ISoftwareProvider, ISourceManager, IAppInstaller, IAppRemover
    {
        public List<string> Did { get; } = [];
        public Func<CancellationToken, Task>? During { get; set; }

        public string Id => "fake";
        public ProviderFacts Facts => new(Id, null, []);
        public Task<ProviderStatus> ProbeAsync(CancellationToken ct) => Task.FromResult(ProviderStatus.Available);
        public Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceInfo>>([]);
        public Task<IReadOnlyList<AppInfo>> ClaimAppsAsync(DesktopEntryIndex unclaimed, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AppInfo>>([]);

        public bool CanToggle(SourceInfo source) => true;
        public bool CanRemove(SourceInfo source) => true;
        public bool CanAdd => true;

        public Task SetEnabledAsync(SourceInfo source, bool enabled, CancellationToken ct)
        {
            Did.Add($"switch {source.Id} {(enabled ? "on" : "off")}");
            return Task.CompletedTask;
        }

        public Task RemoveAsync(SourceInfo source, CancellationToken ct)
        {
            Did.Add($"remove-source {source.Id}");
            return Task.CompletedTask;
        }

        public Task<string> AddAsync(SourceRequest request, CancellationToken ct)
        {
            Did.Add($"add-source {request.Name}");
            return Task.FromResult("user:" + request.Name);
        }

        public IReadOnlyList<InstallTarget> TargetsFor(StoreOffer offer, IReadOnlyList<SourceInfo> sources) =>
            [new("user", "Only for you", "", false)];

        public Task<ChangePlan> PlanInstallAsync(StoreOffer offer, InstallTarget target, string name, CancellationToken ct) =>
            Task.FromResult(Plan(offer.ComponentId, name));

        public Task<ChangePlan> PlanReinstallAsync(ChangeRecord removal, CancellationToken ct) =>
            Task.FromResult(Plan(removal.Undo["ref"], removal.Name));

        public async Task InstallAsync(ChangePlan plan, IProgress<OperationProgress> progress, CancellationToken ct)
        {
            progress.Report(new(OperationPhase.Installing, 0.5));
            if (During is not null)
                await During(ct);
            Did.Add($"install {plan.Technical["ref"]}");
        }

        public string? WhyNotRemovable(AppInfo app) => null;

        public Task<ChangePlan> PlanRemovalAsync(AppInfo app, CancellationToken ct) =>
            Task.FromResult(Plan(app.Id, app.Name) with { DataPath = "/home/u/.var/app/" + app.Id });

        public Task<RemovalResult> RemoveAsync(
            ChangePlan plan, bool removeData, IProgress<OperationProgress> progress, CancellationToken ct)
        {
            Did.Add($"remove {plan.Technical["ref"]}");
            return Task.FromResult(new RemovalResult(removeData ? [trash.Move(plan.DataPath!)] : []));
        }

        private static ChangePlan Plan(string id, string name) =>
            new("fake", id, name, [new(name)]) { Technical = new Dictionary<string, string> { ["ref"] = id } };
    }

    private readonly MemoryJournal _journal = new();
    private readonly FakeTrash _trash = new();
    private readonly FakeProvider _provider;
    private readonly OperationRunner _runner;
    private readonly AppChanges _changes;

    public AppChangesTests()
    {
        _provider = new FakeProvider(_trash);
        _runner = new OperationRunner(_journal);
        _changes = new AppChanges([_provider], _runner, _trash);
    }

    public void Dispose() => _runner.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SourceInfo Source(string id) => new(
        "fake", id, id, null, null, true, SourceScope.User, TrustLevel.Unknown, new Dictionary<string, string>());

    private static AppInfo App(string id) => new(
        "fake", id, id, null, null, null, null, null, null, null, [], null, new Dictionary<string, string>());

    private static CatalogSnapshot Snapshot(IReadOnlyList<SourceInfo>? sources = null, IReadOnlyList<AppInfo>? apps = null) =>
        new([], sources ?? [], apps ?? [], [], [], [], []);

    private static readonly StoreOffer Offer = new("fake", "user:src", "org.example.App", null, null, false, null);

    [Fact]
    public async Task A_change_is_written_when_it_starts_and_again_when_it_ends()
    {
        var plan = await _provider.PlanInstallAsync(Offer, new("user", "", "", false), "App", Token);

        var record = await _changes.InstallAsync(plan);

        Assert.Equal(ChangeOutcome.Done, record.Outcome);
        Assert.Equal([ChangeOutcome.Started, ChangeOutcome.Done], _journal.Lines.Select(line => line.Outcome));
        Assert.Single(_journal.Read());
        Assert.Equal("org.example.App", record.Undo["ref"]);
        Assert.Null(_runner.Current);
    }

    [Fact]
    public async Task A_failing_change_is_recorded_with_its_reason_and_does_not_throw()
    {
        _provider.During = _ => throw new InvalidOperationException("no network");
        var plan = await _provider.PlanInstallAsync(Offer, new("user", "", "", false), "App", Token);

        var record = await _changes.InstallAsync(plan);

        Assert.Equal(ChangeOutcome.Failed, record.Outcome);
        Assert.Equal("no network", record.Problem);
        Assert.Equal("It did not go through, so there is nothing to undo.", _changes.WhyNoUndo(record, _journal.Read()));
    }

    [Fact]
    public async Task A_refused_plan_is_never_carried_out()
    {
        var record = await _changes.RemoveAsync(ChangePlan.Refused("fake", "glibc", "glibc", "Your system needs this."), false);

        Assert.Equal(ChangeOutcome.Failed, record.Outcome);
        Assert.Equal("Your system needs this.", record.Problem);
        Assert.Empty(_provider.Did);
    }

    [Fact]
    public async Task Changes_run_one_at_a_time_and_the_running_one_can_be_cancelled()
    {
        var entered = new TaskCompletionSource();
        _provider.During = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var plan = await _provider.PlanInstallAsync(Offer, new("user", "", "", false), "App", Token);

        var first = _changes.InstallAsync(plan);
        await entered.Task.WaitAsync(Token);
        var second = _changes.SwitchSourceAsync(Source("user:src"), false);

        Assert.Equal(ChangeKind.InstallApp, _runner.Current!.Record.Kind);
        Assert.Equal(0.5, _runner.Current.Progress.Fraction);
        Assert.Empty(_provider.Did);

        _runner.CancelCurrent();

        Assert.Equal(ChangeOutcome.Cancelled, (await first).Outcome);
        Assert.Equal(ChangeOutcome.Done, (await second).Outcome);
        Assert.Equal(["switch user:src off"], _provider.Did);
    }

    [Fact]
    public async Task Undoing_an_install_removes_the_app_and_can_only_be_done_once()
    {
        var plan = await _provider.PlanInstallAsync(Offer, new("user", "", "", false), "App", Token);
        var installed = await _changes.InstallAsync(plan);
        var now = Snapshot(apps: [App("org.example.App")]);

        Assert.Null(_changes.WhyNoUndo(installed, _journal.Read()));
        var step = await _changes.PlanUndoAsync(installed, now, Token);
        var undone = await _changes.UndoAsync(step, now);

        Assert.Equal(ChangeKind.RemoveApp, undone.Kind);
        Assert.Equal(installed.Id, undone.UndoOf);
        Assert.Equal(["install org.example.App", "remove org.example.App"], _provider.Did);
        Assert.Equal("This has been undone already.", _changes.WhyNoUndo(installed, _journal.Read()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _changes.PlanUndoAsync(installed, Snapshot(), Token));
    }

    [Fact]
    public async Task Undoing_a_removal_installs_again_and_brings_the_data_back_from_the_Trash()
    {
        var plan = await _provider.PlanRemovalAsync(App("org.example.App"), Token);
        var removed = await _changes.RemoveAsync(plan, removeData: true);

        Assert.Equal("/home/u/.var/app/org.example.App", Assert.Single(removed.Trashed).OriginalPath);

        var step = await _changes.PlanUndoAsync(removed, Snapshot(), Token);
        var undone = await _changes.UndoAsync(step, Snapshot());

        Assert.Equal(ChangeOutcome.Done, undone.Outcome);
        Assert.Equal(["remove org.example.App", "install org.example.App"], _provider.Did);
        Assert.Equal(["/home/u/.var/app/org.example.App"], _trash.Restored);
    }

    [Fact]
    public async Task Source_changes_are_recorded_and_reversed()
    {
        var added = await _changes.AddSourceAsync("fake", new("mine", "https://example.org/x.flatpakrepo"), "Mine");
        var switched = await _changes.SwitchSourceAsync(Source("user:mine"), false);
        var now = Snapshot(sources: [Source("user:mine")]);

        Assert.Equal("user:mine", added.Target);
        await _changes.UndoAsync(await _changes.PlanUndoAsync(switched, now, Token), now);
        await _changes.UndoAsync(await _changes.PlanUndoAsync(added, now, Token), now);

        Assert.Equal(
            ["add-source mine", "switch user:mine off", "switch user:mine on", "remove-source user:mine"], _provider.Did);
        var removal = _journal.Read()[^1];
        Assert.NotNull(_changes.WhyNoUndo(removal, _journal.Read()));
    }
}
