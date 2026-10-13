namespace Yar.Core;

/// <summary>Optional: a provider that can install the apps its sources offer.</summary>
public interface IAppInstaller
{
    /// <summary>Where this offer can be installed to, the recommended place first. Empty when it cannot be installed.</summary>
    IReadOnlyList<InstallTarget> TargetsFor(StoreOffer offer, IReadOnlyList<SourceInfo> sources);

    /// <summary>Works out what installing would bring along. Changes nothing.</summary>
    Task<ChangePlan> PlanInstallAsync(StoreOffer offer, InstallTarget target, string name, CancellationToken ct);

    /// <summary>Carries out a plan from <see cref="PlanInstallAsync"/>. The system may ask for the user's password.</summary>
    Task InstallAsync(ChangePlan plan, IProgress<OperationProgress> progress, CancellationToken ct);

    /// <summary>Reinstalls what a removal took away, from what its record kept.</summary>
    Task<ChangePlan> PlanReinstallAsync(ChangeRecord removal, CancellationToken ct);
}

/// <summary>What <see cref="IAppRemover.RemoveAsync"/> did beyond removing the app itself.</summary>
public sealed record RemovalResult(IReadOnlyList<TrashedItem> Trashed);

/// <summary>Optional: a provider that can remove its installed apps.</summary>
public interface IAppRemover
{
    /// <summary>Why this app cannot be removed from Yar, in plain words; null when it can.</summary>
    string? WhyNotRemovable(AppInfo app);

    /// <summary>Works out what removing would take away. Changes nothing.</summary>
    Task<ChangePlan> PlanRemovalAsync(AppInfo app, CancellationToken ct);

    /// <param name="removeData">Also move the app's own data (<see cref="ChangePlan.DataPath"/>) to the Trash.</param>
    Task<RemovalResult> RemoveAsync(
        ChangePlan plan, bool removeData, IProgress<OperationProgress> progress, CancellationToken ct);
}
