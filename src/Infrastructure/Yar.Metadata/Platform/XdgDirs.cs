namespace Yar.Metadata;

/// <summary>
/// The XDG base directories. This is the only place that reads the XDG environment variables.
/// </summary>
public sealed class XdgDirs
{
    public XdgDirs(string home, Func<string, string?> getEnvironmentVariable)
    {
        Home = home;
        DataHome = AbsoluteOr(getEnvironmentVariable("XDG_DATA_HOME"), Path.Combine(home, ".local", "share"));
        ConfigHome = AbsoluteOr(getEnvironmentVariable("XDG_CONFIG_HOME"), Path.Combine(home, ".config"));
        CacheHome = AbsoluteOr(getEnvironmentVariable("XDG_CACHE_HOME"), Path.Combine(home, ".cache"));
        StateHome = AbsoluteOr(getEnvironmentVariable("XDG_STATE_HOME"), Path.Combine(home, ".local", "state"));

        var dataDirs = getEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(dataDirs))
            dataDirs = "/usr/local/share:/usr/share";
        // The spec says relative paths are invalid and must be ignored.
        DataDirs = dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static XdgDirs FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable);

    public string Home { get; }
    public string DataHome { get; }
    public string ConfigHome { get; }
    public string CacheHome { get; }

    /// <summary>For what should last between runs but is not a setting, such as the record of changes.</summary>
    public string StateHome { get; }

    /// <summary>System data directories, most important first. Does not include <see cref="DataHome"/>.</summary>
    public IReadOnlyList<string> DataDirs { get; }

    /// <summary>Every <c>applications</c> directory, in lookup order: the user's own first.</summary>
    public IReadOnlyList<string> ApplicationDirs =>
        DataDirs.Prepend(DataHome).Distinct(StringComparer.Ordinal)
            .Select(d => Path.Combine(d, "applications")).ToList();

    public string UserApplicationsDir => Path.Combine(DataHome, "applications");

    private static string AbsoluteOr(string? value, string fallback) =>
        !string.IsNullOrEmpty(value) && Path.IsPathFullyQualified(value) ? Normalize(value) : fallback;

    private static string Normalize(string path) => path.Length > 1 ? path.TrimEnd('/') : path;
}
