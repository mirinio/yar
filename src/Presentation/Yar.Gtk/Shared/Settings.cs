using System.Text.Json;

namespace Yar.App;

/// <summary>The few things Yar remembers between runs, kept in <c>$XDG_CONFIG_HOME/yar/settings.json</c>.</summary>
internal sealed class Settings
{
    private readonly string _path;

    private Settings(string path) => _path = path;

    public bool ShowTechnicalDetails { get; set; }

    /// <summary>Whether ratings, reviews and popular lists may be fetched from the internet.</summary>
    public bool UseOnlineData { get; set; } = true;

    /// <summary>Whether the Store shows trending source-code projects. Off unless the user asks for it.</summary>
    public bool ShowDeveloperProjects { get; set; }

    /// <summary>The programming languages to show projects for. Null until the user has seen the list once.</summary>
    public List<string>? DeveloperLanguages { get; set; }

    /// <summary>The folder apps the user adds from a file go into. Null means the recommended one.</summary>
    public string? AppsFolder { get; set; }

    public static Settings Load(string configHome)
    {
        var settings = new Settings(Path.Combine(configHome, "yar", "settings.json"));
        try
        {
            if (File.Exists(settings._path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settings._path));
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    settings.ShowTechnicalDetails =
                        document.RootElement.TryGetProperty("showTechnicalDetails", out var technical) &&
                        technical.ValueKind == JsonValueKind.True;
                    // On unless the user switched it off.
                    settings.UseOnlineData =
                        !document.RootElement.TryGetProperty("useOnlineData", out var online) ||
                        online.ValueKind != JsonValueKind.False;
                    settings.ShowDeveloperProjects =
                        document.RootElement.TryGetProperty("showDeveloperProjects", out var developer) &&
                        developer.ValueKind == JsonValueKind.True;
                    if (document.RootElement.TryGetProperty("appsFolder", out var folder) &&
                        folder.ValueKind == JsonValueKind.String && Path.IsPathFullyQualified(folder.GetString()!))
                    {
                        settings.AppsFolder = folder.GetString();
                    }
                    if (document.RootElement.TryGetProperty("developerLanguages", out var languages) &&
                        languages.ValueKind == JsonValueKind.Array)
                    {
                        settings.DeveloperLanguages = languages.EnumerateArray()
                            .Where(l => l.ValueKind == JsonValueKind.String)
                            .Select(l => l.GetString()!)
                            .Where(l => Yar.Core.DevLanguages.QueryFor(l) is not null)
                            .Distinct().ToList();
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged settings file just means starting from the defaults.
        }
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                showTechnicalDetails = ShowTechnicalDetails,
                useOnlineData = UseOnlineData,
                showDeveloperProjects = ShowDeveloperProjects,
                developerLanguages = DeveloperLanguages,
                appsFolder = AppsFolder,
            }));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember a preference is not worth interrupting the user for.
        }
    }
}
