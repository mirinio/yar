namespace Yar.Core;

/// <summary>
/// Improves what the providers found with information they do not have themselves:
/// friendlier names and summaries for apps, trust levels and descriptions for sources.
/// </summary>
public interface ICatalogAnnotator
{
    Task<(IReadOnlyList<SourceInfo> Sources, IReadOnlyList<AppInfo> Apps)> AnnotateAsync(
        IReadOnlyList<SourceInfo> sources,
        IReadOnlyList<AppInfo> apps,
        DesktopEntryIndex launchers,
        CancellationToken ct);
}
