using Yar.Core;

namespace Yar.Backends.Flatpak;

// Installing the updates flatpak reports, for named apps and runtimes only.
public sealed partial class FlatpakProvider
{
    public Task<ChangePlan> PlanUpdateAsync(IReadOnlyList<UpdateInfo> updates, string name, CancellationToken ct)
    {
        // An update is listed as "<installation>:<app or runtime ID>".
        var chosen = updates
            .Where(u => u.ProviderId == ProviderId)
            .Select(u => (Update: u, Parts: u.Id.Split(':', 2)))
            .Where(x => x.Parts is [("user" or "system"), var id] && IsPlainId(id))
            .Select(x => (x.Update, Installation: x.Parts[0], Id: x.Parts[1]))
            .ToList();
        if (_flatpak is null || chosen.Count == 0)
            return Task.FromResult(ChangePlan.Refused(ProviderId, name, name, "There is nothing here that I can update."));

        var notes = new List<string>
        {
            "An app that is open keeps running its old version until you close and reopen it.",
            "I can't say ahead of time how much there is to download; Flatpak only fetches the parts that changed.",
        };
        if (chosen.Any(c => c.Installation == "system"))
            notes.Add("Some of these are installed for everyone on this computer, so your system may ask for your password.");
        if (chosen.Any(c => c.Update.AppId is null))
            notes.Add("Building blocks that apps share are updated too; the apps that use them get the benefit.");

        var technical = new Dictionary<string, string>();
        foreach (var group in chosen.GroupBy(c => c.Installation))
            technical[group.Key] = string.Join('\n', group.Select(c => c.Id).Distinct(StringComparer.Ordinal));

        return Task.FromResult(new ChangePlan(
            ProviderId, chosen.Count == 1 ? chosen[0].Update.AppId ?? chosen[0].Update.Id : name, name,
            chosen.OrderBy(c => c.Update.AppId is null).ThenBy(c => c.Update.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new PlanItem(
                    c.Update.Name,
                    (c.Update.CurrentVersion, c.Update.NewVersion) switch
                    {
                        // Flatpak often rebuilds an app without changing its version number.
                        ({ } current, { } next) when current == next => $"{next}, a newer build",
                        ({ } current, { } next) => $"{current} → {next}",
                        (null, { } next) => next,
                        _ => c.Update.AppId is null ? "A building block apps share" : null,
                    })).ToList())
        {
            Notes = notes,
            Technical = technical,
        });
    }

    public async Task UpdateAsync(ChangePlan plan, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        var batches = Installations
            .Select(installation => (installation, Ids: (plan.Technical.GetValueOrDefault(installation) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)))
            .Where(batch => batch.Ids.Length > 0)
            .ToList();
        // A plan comes back from the window; check again that it names plain IDs and nothing else.
        if (_flatpak is null || plan.ProviderId != ProviderId || batches.Count == 0 ||
            batches.Any(batch => batch.Ids.Any(id => !IsPlainId(id))) ||
            plan.Technical.Keys.Any(key => key is not ("user" or "system")))
        {
            throw new InvalidOperationException("This is not a plan I made for updating Flatpak apps.");
        }

        progress.Report(new(OperationPhase.Waiting));
        foreach (var (installation, ids) in batches)
        {
            await RunChangeAsync(
                ["update", $"--{installation}", "--noninteractive", .. ids], InstallTimeout,
                OperationPhase.Installing, progress, ct).ConfigureAwait(false);
        }
    }

    // An app or runtime ID such as org.gnome.Platform: dotted, with no path and nothing that could be read as an option.
    private static bool IsPlainId(string id) => IsRef(id) && id.Contains('.') && !id.Contains('/');
}
