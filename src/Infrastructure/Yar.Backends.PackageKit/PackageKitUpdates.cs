using Yar.Core;

namespace Yar.Backends.PackageKit;

// Installing the updates PackageKit reports. As with installs, a simulated run first says what
// the update really involves, and that list is what the user is shown.
public sealed partial class PackageKitProvider
{
    // PK_INFO_ENUM_UPDATING and INSTALLING from pk-enum.h.
    private const uint InfoUpdating = 11, InfoInstalling = 12;

    public async Task<ChangePlan> PlanUpdateAsync(IReadOnlyList<UpdateInfo> updates, string name, CancellationToken ct)
    {
        var wanted = updates.Where(u => u.ProviderId == ProviderId).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        // Asked afresh: the newer versions on offer may have moved on since the list was shown.
        var waiting = await _client.GetUpdatesAsync(ct).ConfigureAwait(false);
        var chosen = waiting.Where(p => wanted.Contains(KeyOf(p))).ToList();
        if (chosen.Count == 0)
            return ChangePlan.Refused(ProviderId, name, name, "These updates are not waiting any more. They may have been installed already.");

        var all = await _client.SimulateUpdateAsync(chosen.Select(p => p.PackageId).ToList(), ct).ConfigureAwait(false);
        var coming = all.Where(p => p.Info is not (InfoRemoving or InfoObsoleting)).ToList();
        var comingNames = coming.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        // An update replaces old versions; a part only counts as lost when nothing of that name comes in its place.
        var lost = all.Where(p => p.Info is InfoRemoving or InfoObsoleting && !comingNames.Contains(p.Name)).ToList();
        var sizes = await _client.GetOfferSizesAsync(coming.Select(p => p.PackageId).Distinct(StringComparer.Ordinal).ToList(), ct)
            .ConfigureAwait(false);

        var named = updates.ToDictionary(u => u.Id, u => u, StringComparer.Ordinal);
        var chosenKeys = chosen.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        var items = chosen.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new PlanItem(
            named.GetValueOrDefault(KeyOf(p))?.Name ?? p.Name,
            named.GetValueOrDefault(KeyOf(p))?.CurrentVersion is { } current ? $"{current} → {p.Version}" : p.Version,
            sizes.GetValueOrDefault(p.PackageId)?.DownloadBytes)).ToList();
        items.AddRange(coming.Where(p => !chosenKeys.Contains(KeyOf(p))).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new PlanItem(p.Name, $"Comes along: {p.Version}", sizes.GetValueOrDefault(p.PackageId)?.DownloadBytes)));
        items.AddRange(lost.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new PlanItem(p.Name, "Will be removed: a newer part replaces it")));

        var notes = new List<string>();
        if (updates.Any(u => u.AppId is null) || coming.Count > chosen.Count)
            notes.Add("Some of this belongs to the system itself. Restart your computer afterwards, so that everything uses the new versions.");
        notes.Add("An app that is open keeps running its old version until you close and reopen it.");

        var ids = coming.Select(p => p.PackageId).Distinct(StringComparer.Ordinal).ToList();
        return new(ProviderId, chosen.Count == 1 ? KeyOf(chosen[0]) : name, name, items)
        {
            DownloadBytes = Total(ids, id => sizes.GetValueOrDefault(id)?.DownloadBytes),
            NeedsPassword = true,
            Warnings = lost.Count > 0
                ? [$"This update also removes {lost.Count} {(lost.Count == 1 ? "part" : "parts")} that newer ones replace. They are at the end of the list."]
                : [],
            Notes = notes,
            Blocked = Guard(lost, ""),
            Technical = new Dictionary<string, string>
            {
                ["package-ids"] = string.Join('\n', chosen.Select(p => p.PackageId)),
            },
        };
    }

    public async Task UpdateAsync(ChangePlan plan, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        var ids = (plan.Technical.GetValueOrDefault("package-ids") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // A plan comes back from the window; every entry must again be one well-formed package ID.
        if (plan.ProviderId != ProviderId || ids.Length == 0 || ids.Any(id =>
            id.Length > 1024 || id.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            PkPackage.Parse(id, "") is not { } package || !IsPackageName(package.Name)))
        {
            throw new InvalidOperationException("This is not a plan I made for updating system packages.");
        }

        progress.Report(new(OperationPhase.Waiting));
        await _client.UpdateAsync(ids, Relay(progress, OperationPhase.Installing), ct).ConfigureAwait(false);
    }
}
