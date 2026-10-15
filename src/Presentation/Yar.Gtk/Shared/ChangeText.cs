using Yar.Core;

namespace Yar.App;

/// <summary>How changes are put into words: while they run, and afterwards in the history.</summary>
internal static class ChangeText
{
    /// <summary>What happened, in the past tense.</summary>
    public static string Done(ChangeRecord record) => record.Kind switch
    {
        ChangeKind.InstallApp => T.F("Installed {0}", record.Name),
        ChangeKind.RemoveApp => T.F("Removed {0}", record.Name),
        ChangeKind.AddApp => T.F("Added {0}", record.Name),
        ChangeKind.Update => T.F("Updated {0}", record.Name),
        ChangeKind.AddSource => T.F("Added the source {0}", record.Name),
        ChangeKind.RemoveSource => T.F("Removed the source {0}", record.Name),
        ChangeKind.SwitchSourceOn => T.F("Turned on {0}", record.Name),
        ChangeKind.SwitchSourceOff => T.F("Turned off {0}", record.Name),
        ChangeKind.AllowThirdParty => T.F("Allowed {0}", Lower(record.Name)),
        ChangeKind.DeclineThirdParty => T.F("Turned off {0}", Lower(record.Name)),
        _ => record.Name,
    };

    /// <summary>What is happening right now.</summary>
    public static string Running(ChangeRecord record) => record.Kind switch
    {
        ChangeKind.InstallApp => T.F("Installing {0}", record.Name),
        ChangeKind.RemoveApp => T.F("Removing {0}", record.Name),
        ChangeKind.AddApp => T.F("Adding {0}", record.Name),
        ChangeKind.Update => T.F("Updating {0}", record.Name),
        ChangeKind.AddSource => T.F("Adding the source {0}", record.Name),
        ChangeKind.RemoveSource => T.F("Removing the source {0}", record.Name),
        ChangeKind.SwitchSourceOn or ChangeKind.AllowThirdParty => T.F("Turning on {0}", Lower(record.Name)),
        ChangeKind.SwitchSourceOff or ChangeKind.DeclineThirdParty => T.F("Turning off {0}", Lower(record.Name)),
        _ => record.Name,
    };

    public static string Phase(OperationProgress progress)
    {
        var phase = progress.Phase switch
        {
            OperationPhase.Waiting => T._("Getting ready. Your system may ask for your password."),
            OperationPhase.Downloading => T._("Downloading"),
            OperationPhase.Checking => T._("Checking"),
            OperationPhase.Installing => T._("Installing"),
            OperationPhase.Removing => T._("Removing"),
            _ => T._("Finishing"),
        };
        return progress.Detail is null ? phase : $"{phase} · {progress.Detail}";
    }

    /// <summary>How a change ended, when that is anything other than "it worked".</summary>
    public static string? Ending(ChangeRecord record) => record.Outcome switch
    {
        ChangeOutcome.Failed => T.F("Did not work: {0}", Widgets.Short(record.Problem ?? "")),
        ChangeOutcome.Cancelled => T._("Cancelled before it finished"),
        ChangeOutcome.Started => T._("Never finished. I was closed or stopped in the middle, so check that it is as you expect."),
        _ => null,
    };

    // "Software from outside Fedora" reads better mid-sentence without its capital.
    private static string Lower(string name) =>
        name.StartsWith("Software ", StringComparison.Ordinal) ? char.ToLowerInvariant(name[0]) + name[1..] : name;
}
