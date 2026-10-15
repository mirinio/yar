namespace Yar.Core;

/// <summary>How a change from the record would be reversed, worked out before anything is touched.</summary>
/// <param name="Summary">What undoing does, in a sentence for the user.</param>
/// <param name="Plan">The install or removal it comes down to; null for a change with nothing to preview.</param>
public sealed record UndoStep(ChangeRecord Of, string Summary, ChangePlan? Plan, IReadOnlyList<TrashedItem> Restore);

/// <summary>
/// Every change Yar makes to the system goes through here: it is written into the record,
/// takes its turn behind any change already running, and can be undone where that is possible.
/// </summary>
public sealed class AppChanges(
    IReadOnlyList<ISoftwareProvider> providers, OperationRunner runner, ITrash trash, IThirdPartyPolicy? thirdParty = null,
    IAppPinner? pinner = null)
{
    /// <summary>Set in a record's <see cref="ChangeRecord.Undo"/> when the app was added but could not be pinned.</summary>
    public const string PinProblem = "pin-problem";

    public const string ThirdPartyProvider = "third-party";

    public Task<ChangeRecord> SwitchSourceAsync(SourceInfo source, bool enabled, string? undoOf = null) =>
        runner.RunAsync(
            ChangeRecord.Begin(
                enabled ? ChangeKind.SwitchSourceOn : ChangeKind.SwitchSourceOff, source.ProviderId, source.Id, source.Name)
                with
            { UndoOf = undoOf },
            context => Sources(source.ProviderId).SetEnabledAsync(source, enabled, context.Ct));

    public Task<ChangeRecord> AddSourceAsync(string providerId, SourceRequest request, string displayName) =>
        runner.RunAsync(
            ChangeRecord.Begin(ChangeKind.AddSource, providerId, request.Name, displayName) with { Origin = request.Url },
            async context =>
            {
                var id = await Sources(providerId).AddAsync(request, context.Ct).ConfigureAwait(false);
                context.Amend(record => record with { Target = id });
            });

    public Task<ChangeRecord> RemoveSourceAsync(SourceInfo source, string? undoOf = null) =>
        runner.RunAsync(
            ChangeRecord.Begin(ChangeKind.RemoveSource, source.ProviderId, source.Id, source.Name) with
            {
                Origin = source.Url,
                UndoOf = undoOf,
            },
            context => Sources(source.ProviderId).RemoveAsync(source, context.Ct));

    public Task<ChangeRecord> SetThirdPartyAsync(bool allowed, string name, string? undoOf = null) =>
        runner.RunAsync(
            ChangeRecord.Begin(
                allowed ? ChangeKind.AllowThirdParty : ChangeKind.DeclineThirdParty, ThirdPartyProvider, ThirdPartyProvider, name)
                with
            { UndoOf = undoOf },
            context => (thirdParty ?? throw new InvalidOperationException("This computer has no such choice."))
                .SetAllowedAsync(allowed, context.Ct));

    /// <summary>Carries out a plan from <see cref="IAppInstaller.PlanInstallAsync"/>.</summary>
    public Task<ChangeRecord> InstallAsync(
        ChangePlan plan, string? undoOf = null, IReadOnlyList<TrashedItem>? restore = null) =>
        runner.RunAsync(
            Begin(ChangeKind.InstallApp, plan, undoOf),
            async context =>
            {
                Refuse(plan);
                await Ability<IAppInstaller>(plan.ProviderId).InstallAsync(plan, context.Progress, context.Ct)
                    .ConfigureAwait(false);
                // The app is back; so is what removing it had put in the Trash.
                foreach (var item in restore ?? [])
                {
                    if (trash.Holds(item))
                        trash.Restore(item);
                }
            });

    /// <summary>Carries out a plan from <see cref="IAppUpdater.PlanUpdateAsync"/>.</summary>
    public Task<ChangeRecord> UpdateAsync(ChangePlan plan) =>
        runner.RunAsync(
            ChangeRecord.Begin(ChangeKind.Update, plan.ProviderId, plan.Target, plan.Name) with { Origin = plan.Origin },
            context =>
            {
                Refuse(plan);
                return Ability<IAppUpdater>(plan.ProviderId).UpdateAsync(plan, context.Progress, context.Ct);
            });

    /// <summary>Adds an app the user brought as a file, as previewed in <paramref name="plan"/>.</summary>
    public Task<ChangeRecord> AddAppAsync(IAppIntake intake, IntakeReport report, IntakeChoices choices, ChangePlan plan) =>
        runner.RunAsync(
            ChangeRecord.Begin(ChangeKind.AddApp, plan.ProviderId, plan.Target, plan.Name) with
            {
                Origin = report.OriginUrl ?? report.FileName,
                Checksum = report.Sha256.Length > 0 ? report.Sha256 : null,
                ChecksumVerified = choices.HashVerified,
                Undo = plan.Technical,
            },
            async context =>
            {
                Refuse(plan);
                var added = await intake.AddAsync(report, choices, plan, context.Progress, context.Ct).ConfigureAwait(false);
                context.Amend(record => record with { ProviderId = added.ProviderId, Target = added.AppId, Name = added.Name });
                if (!choices.Pin || !IsLauncherId(added.AppId))
                    return;
                // The app is in place either way; a taskbar that can't be changed is told, not treated as a failure.
                try
                {
                    await (pinner ?? throw new InvalidOperationException("I can't pin apps on this desktop."))
                        .PinAsync(added.AppId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is InvalidOperationException or TimeoutException)
                {
                    context.Amend(record => record with
                    {
                        Undo = new Dictionary<string, string>(record.Undo) { [PinProblem] = e.Message },
                    });
                }
            });

    /// <summary>Carries out a plan from <see cref="IAppRemover.PlanRemovalAsync"/>.</summary>
    public Task<ChangeRecord> RemoveAsync(ChangePlan plan, bool removeData, string? undoOf = null) =>
        runner.RunAsync(
            Begin(ChangeKind.RemoveApp, plan, undoOf),
            async context =>
            {
                Refuse(plan);
                var result = await Ability<IAppRemover>(plan.ProviderId)
                    .RemoveAsync(plan, removeData, context.Progress, context.Ct).ConfigureAwait(false);
                context.Amend(record => record with { Trashed = result.Trashed });
                // A launcher that is gone should not stay behind on the taskbar. Not being able to tidy that up is no failure.
                if (pinner is { WhyNot: null } && IsLauncherId(plan.Target))
                {
                    try
                    {
                        await pinner.UnpinAsync(plan.Target, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is InvalidOperationException or TimeoutException)
                    {
                    }
                }
            });

    /// <summary>Why a change in the record cannot be undone, in plain words; null when it can.</summary>
    public string? WhyNoUndo(ChangeRecord record, IReadOnlyList<ChangeRecord> history)
    {
        if (record.Outcome != ChangeOutcome.Done)
            return "It did not go through, so there is nothing to undo.";
        if (history.Any(other => other.UndoOf == record.Id && other.Outcome == ChangeOutcome.Done))
            return "This has been undone already.";

        return record.Kind switch
        {
            ChangeKind.Update =>
                "An update can't be undone from here: sources only keep the newest version on offer.",
            ChangeKind.RemoveSource =>
                "A removed source has to be added again from the Sources tab, with its address.",
            ChangeKind.AddSource or ChangeKind.SwitchSourceOn or ChangeKind.SwitchSourceOff =>
                Find<ISourceManager>(record.ProviderId) is null ? "I can no longer change this kind of source." : null,
            ChangeKind.AllowThirdParty or ChangeKind.DeclineThirdParty =>
                thirdParty is { IsAvailable: true } ? null : "This computer has no such choice.",
            ChangeKind.InstallApp or ChangeKind.AddApp =>
                Find<IAppRemover>(record.ProviderId) is null ? "I can't remove this kind of app." : null,
            ChangeKind.RemoveApp when Find<IAppInstaller>(record.ProviderId) is not null => null,
            // Nothing to install again from, but what went to the Trash can still come back.
            ChangeKind.RemoveApp => record.Trashed.Count == 0
                ? "I have nowhere to get this app from again."
                : record.Trashed.All(trash.Holds) ? null : "It is no longer in the Trash.",
            _ => "I don't know how to undo this.",
        };
    }

    /// <summary>Works out how to reverse a change. Changes nothing.</summary>
    /// <exception cref="InvalidOperationException">It cannot be undone as things stand now; the message says why.</exception>
    public async Task<UndoStep> PlanUndoAsync(ChangeRecord record, CatalogSnapshot now, CancellationToken ct)
    {
        switch (record.Kind)
        {
            case ChangeKind.SwitchSourceOn or ChangeKind.SwitchSourceOff:
                SourceNow(record, now);
                return new(record, record.Kind == ChangeKind.SwitchSourceOn
                    ? $"Turn {record.Name} off again." : $"Turn {record.Name} on again.", null, []);

            case ChangeKind.AddSource:
                SourceNow(record, now);
                return new(record, $"Remove {record.Name} from your sources again.", null, []);

            case ChangeKind.AllowThirdParty or ChangeKind.DeclineThirdParty:
                return new(record, record.Kind == ChangeKind.AllowThirdParty
                    ? "Turn the extra sources off again." : "Turn the extra sources on again.", null, []);

            case ChangeKind.InstallApp or ChangeKind.AddApp:
                var app = now.Apps.FirstOrDefault(a => a.ProviderId == record.ProviderId && a.Id == record.Target)
                    ?? throw new InvalidOperationException($"{record.Name} is not installed any more.");
                var remover = Ability<IAppRemover>(record.ProviderId);
                if (remover.WhyNotRemovable(app) is { } reason)
                    throw new InvalidOperationException(reason);
                return new(record, $"Remove {record.Name} again.",
                    await remover.PlanRemovalAsync(app, ct).ConfigureAwait(false), []);

            case ChangeKind.RemoveApp:
                var back = record.Trashed.Where(trash.Holds).ToList();
                if (Find<IAppInstaller>(record.ProviderId) is not { } installer)
                    return new(record, $"Put {record.Name} back from the Trash.", null, back);
                return new(record, $"Install {record.Name} again.",
                    await installer.PlanReinstallAsync(record, ct).ConfigureAwait(false), back);

            default:
                throw new InvalidOperationException("I don't know how to undo this.");
        }
    }

    /// <summary>Carries out what <see cref="PlanUndoAsync"/> worked out.</summary>
    public Task<ChangeRecord> UndoAsync(UndoStep step, CatalogSnapshot now)
    {
        var record = step.Of;
        switch (record.Kind)
        {
            case ChangeKind.SwitchSourceOn or ChangeKind.SwitchSourceOff:
                return SwitchSourceAsync(SourceNow(record, now), record.Kind == ChangeKind.SwitchSourceOff, record.Id);
            case ChangeKind.AddSource:
                return RemoveSourceAsync(SourceNow(record, now), record.Id);
            case ChangeKind.AllowThirdParty or ChangeKind.DeclineThirdParty:
                return SetThirdPartyAsync(record.Kind == ChangeKind.DeclineThirdParty, record.Name, record.Id);
            case ChangeKind.InstallApp or ChangeKind.AddApp when step.Plan is not null:
                return RemoveAsync(step.Plan, removeData: false, record.Id);
            case ChangeKind.RemoveApp when step.Plan is not null:
                return InstallAsync(step.Plan, record.Id, step.Restore);
            case ChangeKind.RemoveApp:
                return runner.RunAsync(
                    ChangeRecord.Begin(ChangeKind.AddApp, record.ProviderId, record.Target, record.Name) with { UndoOf = record.Id },
                    _ =>
                    {
                        foreach (var item in step.Restore)
                            trash.Restore(item);
                        return Task.CompletedTask;
                    });
            default:
                throw new InvalidOperationException("I don't know how to undo this.");
        }
    }

    // Launchers Yar itself wrote; nothing else is ever pinned or unpinned.
    private static bool IsLauncherId(string id) =>
        id.StartsWith("yar-", StringComparison.Ordinal) && id.EndsWith(".desktop", StringComparison.Ordinal) && !id.Contains('/');

    private static ChangeRecord Begin(ChangeKind kind, ChangePlan plan, string? undoOf) =>
        ChangeRecord.Begin(kind, plan.ProviderId, plan.Target, plan.Name) with
        {
            Origin = plan.Origin,
            Undo = plan.Technical,
            UndoOf = undoOf,
        };

    // A plan Yar refused must never be carried out, whatever a caller does with it.
    private static void Refuse(ChangePlan plan)
    {
        if (plan.Blocked is { } reason)
            throw new InvalidOperationException(reason);
    }

    private static SourceInfo SourceNow(ChangeRecord record, CatalogSnapshot now) =>
        now.Sources.FirstOrDefault(s => s.ProviderId == record.ProviderId && s.Id == record.Target)
            ?? throw new InvalidOperationException($"{record.Name} is not among your sources any more.");

    private ISourceManager Sources(string providerId) => Ability<ISourceManager>(providerId);

    private T? Find<T>(string providerId)
        where T : class => providers.FirstOrDefault(p => p.Id == providerId) as T;

    private T Ability<T>(string providerId)
        where T : class => Find<T>(providerId) ?? throw new InvalidOperationException("I can't do that for this kind of software.");
}
