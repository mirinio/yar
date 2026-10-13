namespace Yar.Core;

public enum ThirdPartyChoice
{
    /// <summary>Nobody has said yes or no yet. The sources stay off until someone does.</summary>
    NotChosen,
    Allowed,
    Declined,
}

/// <param name="Sources">The sources the choice covers, as the distribution names them, with their kind ("dnf", "flatpak").</param>
public sealed record ThirdPartyStatus(ThirdPartyChoice Choice, IReadOnlyList<(string Name, string Kind)> Sources);

/// <summary>
/// Some distributions ship a short list of sources for software they do not make themselves and
/// leave them off until the user agrees. This is that one decision, where the distribution has it.
/// </summary>
public interface IThirdPartyPolicy
{
    /// <summary>False on distributions that have no such opt-in.</summary>
    bool IsAvailable { get; }

    Task<ThirdPartyStatus> QueryAsync(CancellationToken ct);

    /// <summary>Records the choice and switches the covered sources. The system asks for the user's password.</summary>
    Task SetAllowedAsync(bool allowed, CancellationToken ct);
}

/// <summary>
/// What to show for the third-party choice: the distribution's record of it, set against what
/// the covered sources are actually switched to. The two can differ, for example when no answer
/// was ever recorded but someone switched a source on by hand.
/// </summary>
public sealed record ThirdPartySummary(
    ThirdPartyChoice Recorded, bool ShownOn, IReadOnlyList<string> On, IReadOnlyList<string> Off)
{
    /// <summary>True when the sources are not all in the state the recorded answer implies.</summary>
    public bool Mixed => On.Count > 0 && Off.Count > 0;

    /// <summary>
    /// What giving an answer would do: which sources would change, and which are already as asked.
    /// </summary>
    public (IReadOnlyList<string> Changing, IReadOnlyList<string> Already) Effect(bool allow) =>
        allow ? (Off, On) : (On, Off);

    /// <param name="isOn">Whether a covered source is switched on right now; null if it is not set up at all.</param>
    public static ThirdPartySummary From(ThirdPartyStatus status, Func<(string Name, string Kind), bool?> isOn)
    {
        var on = status.Sources.Where(source => isOn(source) == true).Select(source => source.Name).ToList();
        var off = status.Sources.Where(source => isOn(source) != true).Select(source => source.Name).ToList();
        var shownOn = status.Choice switch
        {
            ThirdPartyChoice.Allowed => true,
            ThirdPartyChoice.Declined => false,
            // No recorded answer: show what is really the case. Only "everything on" counts as on.
            _ => on.Count > 0 && off.Count == 0,
        };
        return new(status.Choice, shownOn, on, off);
    }
}
