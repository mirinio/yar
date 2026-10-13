namespace Yar.Core;

/// <summary>Data an app left behind after it was removed.</summary>
/// <param name="SizeBytes">Null when the folder was too large to measure quickly.</param>
public sealed record LeftoverData(string ProviderId, string Id, string Path, long? SizeBytes);

/// <summary>Optional: a provider that can find data left behind by apps that are gone. It only looks.</summary>
public interface ILeftoverProvider
{
    Task<IReadOnlyList<LeftoverData>> FindLeftoversAsync(IReadOnlyList<AppInfo> installed, CancellationToken ct);
}

public enum AttentionKind { Duplicate, BrokenLauncher, NotInMenu, LeftoverData }

/// <summary>Something worth a look. <see cref="Apps"/> are the installed apps it concerns, if any.</summary>
public sealed record AttentionItem(
    AttentionKind Kind, string Title, IReadOnlyList<AppInfo> Apps, string? Path = null, long? SizeBytes = null);

/// <summary>Gathers the things Yar noticed that a user may want to tidy up. Nothing here changes anything.</summary>
public static class AttentionFinder
{
    public const long MinLeftoverBytes = 1024 * 1024;

    public static IReadOnlyList<AttentionItem> Find(CatalogSnapshot snapshot)
    {
        var items = new List<AttentionItem>();

        foreach (var group in snapshot.Duplicates)
            items.Add(new(AttentionKind.Duplicate, group.Apps[0].Name, group.Apps));

        foreach (var app in snapshot.Apps)
        {
            if (app.Technical.ContainsKey("broken"))
                items.Add(new(AttentionKind.BrokenLauncher, app.Name, [app], app.PrimaryLauncherPath));
            else if (app.Technical.TryGetValue("in-menu", out var inMenu) && inMenu == "false")
                items.Add(new(AttentionKind.NotInMenu, app.Name, [app], app.InstallPath, app.SizeBytes));
        }

        // Some programs create an empty settings folder for apps that were never installed.
        // A few kilobytes are not worth anyone's attention.
        foreach (var leftover in snapshot.Leftovers
            .Where(l => l.SizeBytes is null or >= MinLeftoverBytes)
            .OrderByDescending(l => l.SizeBytes ?? long.MaxValue))
            items.Add(new(AttentionKind.LeftoverData, leftover.Id, [], leftover.Path, leftover.SizeBytes));

        return items;
    }
}
