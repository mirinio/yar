namespace Yar.Core;

/// <summary>One thing a change would add or take away, as a line of the preview.</summary>
/// <param name="Detail">A short note, such as "shared by other apps" or a version.</param>
public sealed record PlanItem(string Name, string? Detail = null, long? Bytes = null);

/// <summary>A place an app can be installed to.</summary>
/// <param name="Id">The provider's own name for it, e.g. "user" or "system".</param>
public sealed record InstallTarget(string Id, string Label, string Explanation, bool NeedsPassword);

/// <summary>
/// What a change would do, worked out before anything is touched. The user sees exactly this,
/// and the provider then carries out exactly this: the plan is handed back to it unchanged.
/// </summary>
public sealed record ChangePlan(string ProviderId, string Target, string Name, IReadOnlyList<PlanItem> Items)
{
    public long? DownloadBytes { get; init; }

    /// <summary>Room taken once installed, or freed once removed.</summary>
    public long? DiskBytes { get; init; }

    public bool NeedsPassword { get; init; }

    /// <summary>Things the user should know before saying yes. Shown prominently.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Plain facts worth a line, such as where the files will go.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Set when Yar refuses to do this. The reason is shown instead of a confirm button.</summary>
    public string? Blocked { get; init; }

    /// <summary>The app's own data, for a removal: where it is and how big. Null when unknown.</summary>
    public string? DataPath { get; init; }

    public long? DataBytes { get; init; }

    /// <summary>Where the app comes from, for the record.</summary>
    public string? Origin { get; init; }

    /// <summary>The provider's own working data for carrying the plan out, and for undoing it later.</summary>
    public IReadOnlyDictionary<string, string> Technical { get; init; } = new Dictionary<string, string>();

    public static ChangePlan Refused(string providerId, string target, string name, string reason) =>
        new(providerId, target, name, []) { Blocked = reason };
}

public enum OperationPhase { Waiting, Downloading, Checking, Installing, Removing, Finishing }

/// <param name="Fraction">Between 0 and 1, or null when the backend cannot say how far along it is.</param>
/// <param name="CanCancel">False once the backend has reached a step that must not be interrupted.</param>
public sealed record OperationProgress(
    OperationPhase Phase, double? Fraction = null, string? Detail = null, bool CanCancel = true);
