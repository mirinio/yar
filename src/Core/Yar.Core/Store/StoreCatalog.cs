namespace Yar.Core;

/// <summary>Turns raw catalog entries into the list of apps the Store shows.</summary>
public static class StoreCatalog
{
    /// <summary>
    /// Merges entries that describe the same app. Two entries are the same app when one's ID is
    /// among the other's IDs: a Flatpak listing "org.mozilla.firefox" that also provides
    /// "firefox.desktop" is the same app as the distribution's "firefox.desktop". Names are never
    /// used for this; different apps can share a name.
    /// </summary>
    public static IReadOnlyList<StoreApp> Merge(IReadOnlyList<CatalogEntry> entries)
    {
        // Union-find over entries, joined through every ID they answer to.
        var parent = Enumerable.Range(0, entries.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        var firstWithId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            foreach (var id in IdsOf(entries[i]))
            {
                if (firstWithId.TryGetValue(id, out var other))
                    parent[Find(i)] = Find(other);
                else
                    firstWithId[id] = i;
            }
        }

        return Enumerable.Range(0, entries.Count)
            .GroupBy(Find)
            .Select(group => Combine(group.Select(i => entries[i]).ToList()))
            .ToList();
    }

    /// <summary>
    /// The offer to suggest: a Flatpak whose maker is verified, otherwise the distribution's own
    /// package, otherwise whatever comes from the best-known source.
    /// </summary>
    public static StoreOffer? Suggest(StoreApp app, Func<StoreOffer, TrustLevel> trustOf) =>
        app.Offers
            .OrderBy(offer => offer is { ProviderId: "flatpak", MakerVerified: true } ? 0
                : offer.ProviderId == "packagekit" && trustOf(offer) == TrustLevel.Distro ? 1
                : 2)
            .ThenBy(trustOf)
            .FirstOrDefault();

    /// <summary>Finds the installed app this store entry corresponds to, if it is installed.</summary>
    public static AppInfo? InstalledMatch(StoreApp app, IReadOnlyList<AppInfo> installed)
    {
        var ids = app.KnownIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return installed.FirstOrDefault(candidate =>
            (candidate.ComponentId is { } component && ids.Contains(Normalize(component))) ||
            candidate.LauncherPaths.Any(path => ids.Contains(Normalize(Path.GetFileName(path)))));
    }

    /// <summary>Orders search hits: names starting with the query, then names containing it, then the rest.</summary>
    public static IReadOnlyList<StoreApp> Search(
        IReadOnlyList<StoreApp> apps, string query, Func<AppCategory, string> categoryName, int limit)
    {
        query = query.Trim();
        if (query.Length == 0)
            return [];

        return apps
            .Select(app => (App: app, Rank: Rank(app, query, categoryName)))
            .Where(hit => hit.Rank < int.MaxValue)
            .OrderBy(hit => hit.Rank)
            .ThenBy(hit => hit.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(hit => hit.App)
            .ToList();
    }

    /// <summary>
    /// The apps of one category, most installed first where that is known and by name otherwise.
    /// </summary>
    public static IReadOnlyList<StoreApp> InCategory(
        IReadOnlyList<StoreApp> apps, AppCategory category, Func<StoreApp, long?> installsOf) =>
        apps.Where(app => app.Categories.Contains(category))
            .OrderByDescending(app => installsOf(app) ?? -1)
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static string Normalize(string id) =>
        id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? id[..^".desktop".Length] : id;

    private static int Rank(StoreApp app, string query, Func<AppCategory, string> categoryName)
    {
        const StringComparison Ignore = StringComparison.CurrentCultureIgnoreCase;
        if (app.Name.StartsWith(query, Ignore))
            return 0;
        if (app.Name.Contains(query, Ignore))
            return 1;
        if (app.Summary?.Contains(query, Ignore) == true)
            return 2;
        if (query.Length >= 3 && app.Categories.Any(c => c != AppCategory.Other && categoryName(c).Contains(query, Ignore)))
            return 3;
        return int.MaxValue;
    }

    private static IEnumerable<string> IdsOf(CatalogEntry entry) =>
        entry.OtherIds.Prepend(entry.ComponentId).Select(Normalize).Where(id => id.Length > 0);

    private static StoreApp Combine(List<CatalogEntry> entries)
    {
        // The Flatpak catalog is the richest (maker, licence, verification), so it describes the app when present.
        var best = entries.OrderBy(e => e.ProviderId == "flatpak" ? 0 : 1)
            .ThenByDescending(e => e.Description?.Length ?? 0).First();
        string? First(Func<CatalogEntry, string?> pick) => pick(best) ?? entries.Select(pick).FirstOrDefault(v => v is not null);

        var offers = entries
            .Select(e => new StoreOffer(
                e.ProviderId, e.SourceId, e.ComponentId, e.PackageName, e.Version, e.MakerVerified, e.VerifiedBy))
            .DistinctBy(o => (o.ProviderId, o.SourceId))
            .ToList();

        return new StoreApp(
            Id: Normalize(best.ComponentId),
            Name: best.Name,
            Summary: First(e => e.Summary),
            Description: First(e => e.Description),
            Developer: First(e => e.Developer),
            License: First(e => e.License),
            Homepage: First(e => e.Homepage),
            IconPath: First(e => e.IconPath),
            IconName: First(e => e.IconName),
            Categories: AppCategories.FromDesktopCategories(entries.SelectMany(e => e.DesktopCategories).ToList()),
            KnownIds: entries.SelectMany(IdsOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Offers: offers)
        {
            Screenshots = entries.OrderBy(e => e.ProviderId == "flatpak" ? 0 : 1)
                .Select(e => e.Screenshots).FirstOrDefault(shots => shots.Count > 0) ?? [],
        };
    }
}
