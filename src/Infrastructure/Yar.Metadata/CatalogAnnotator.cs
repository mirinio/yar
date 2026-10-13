using Yar.Core;

namespace Yar.Metadata;

/// <summary>
/// Describes sources from the bundled table and gives apps the name, summary and component ID
/// from AppStream where a component describes one of their launchers.
/// </summary>
public sealed class CatalogAnnotator(
    KnownSources knownSources, Func<ComponentIndex> loadComponents, IReadOnlyList<string> searchPath) : ICatalogAnnotator
{
    // Providers whose apps are not named by AppStream data from the system catalogs.
    private static readonly HashSet<string> SkippedProviders = ["flatpak", "manual"];

    // Their launchers start the sandbox tool ("flatpak run ..."), not the app's own program.
    private static readonly HashSet<string> SandboxedProviders = ["flatpak", "snap"];

    public Task<(IReadOnlyList<SourceInfo> Sources, IReadOnlyList<AppInfo> Apps)> AnnotateAsync(
        IReadOnlyList<SourceInfo> sources, IReadOnlyList<AppInfo> apps, DesktopEntryIndex launchers,
        CancellationToken ct) =>
        Task.Run<(IReadOnlyList<SourceInfo>, IReadOnlyList<AppInfo>)>(() =>
        {
            var described = sources.Select(knownSources.Describe).ToList();
            var components = loadComponents();
            ct.ThrowIfCancellationRequested();
            var byPath = launchers.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            return (described, apps.Select(app => Tag(Annotate(app, components, byPath), byPath)).ToList());
        }, ct);

    // Category tags and the program a launcher starts, for every app that has a launcher.
    private AppInfo Tag(AppInfo app, Dictionary<string, DesktopEntry> launchers)
    {
        DesktopEntry? primary = null;
        if (app.PrimaryLauncherPath is { } path)
            launchers.TryGetValue(path, out primary);

        var tagged = app with { Categories = AppCategories.FromDesktopCategories(primary?.Categories ?? []) };
        if (primary is null || SandboxedProviders.Contains(app.ProviderId) || app.Technical.ContainsKey("program") ||
            DesktopExec.ResolveProgram(primary.Exec, searchPath) is not { } program)
        {
            return tagged;
        }
        return tagged with { Technical = new Dictionary<string, string>(app.Technical) { ["program"] = program } };
    }

    private static AppInfo Annotate(
        AppInfo app, ComponentIndex components, Dictionary<string, DesktopEntry> launchers)
    {
        if (SkippedProviders.Contains(app.ProviderId))
            return app;

        app.Technical.TryGetValue("package", out var packageName);
        foreach (var path in app.LauncherPaths)
        {
            if (!launchers.TryGetValue(path, out var entry) ||
                components.Find(entry.DesktopId, packageName) is not { } component)
            {
                continue;
            }

            // The launcher AppStream describes is the app's main one, whatever order the provider found.
            var iconIsPath = entry.Icon is not null && Path.IsPathFullyQualified(entry.Icon);
            return app with
            {
                Name = component.Name,
                Summary = component.Summary ?? app.Summary,
                ComponentId = app.ComponentId ?? component.Id,
                IconName = iconIsPath ? null : entry.Icon,
                IconPath = iconIsPath ? entry.Icon : null,
                LauncherPaths = app.LauncherPaths.OrderByDescending(p => p == path).ToList(),
            };
        }
        return app;
    }
}
