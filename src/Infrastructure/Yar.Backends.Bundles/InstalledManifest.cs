using System.Text.Json;

namespace Yar.Backends.Bundles;

/// <summary>
/// Yar's note about an app it added from a file: where it came from and exactly which files and
/// folders Yar created for it. This is what lets such an app be removed again without guessing.
/// </summary>
public sealed record InstalledManifest(
    string Id,
    string Name,
    string Kind,
    DateTimeOffset AddedAt,
    string InstallPath,
    string? Program,
    string? LauncherPath,
    string? IconPath,
    string? Origin,
    string? Sha256,
    bool HashVerified)
{
    /// <summary>Whatever of this app Yar created and is still there, the note itself last.</summary>
    public IEnumerable<string> Paths(string manifestPath) =>
        new[] { InstallPath, LauncherPath, IconPath, manifestPath }.Where(p => p is not null).Select(p => p!);
}

/// <summary>Keeps the notes as one small file per app, usually in <c>$XDG_DATA_HOME/yar/apps</c>.</summary>
public sealed class ManifestStore(string directory)
{
    private const long MaxBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    public string PathOf(string id) => Path.Combine(directory, id + ".json");

    public bool Exists(string id) => File.Exists(PathOf(id));

    public void Write(InstalledManifest manifest)
    {
        Directory.CreateDirectory(directory);
        var temporary = PathOf(manifest.Id) + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Options));
        File.Move(temporary, PathOf(manifest.Id), overwrite: true);
    }

    /// <summary>Every readable note. A damaged one is skipped; the app it described is then treated as any other.</summary>
    public IReadOnlyList<InstalledManifest> ReadAll()
    {
        var found = new List<InstalledManifest>();
        try
        {
            if (!Directory.Exists(directory))
                return found;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").Take(5000).Order(StringComparer.Ordinal))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxBytes || info.LinkTarget is not null)
                        continue;
                    var manifest = JsonSerializer.Deserialize<InstalledManifest>(File.ReadAllText(file), Options);
                    // A note is only believed under the name it was written as.
                    if (manifest is not null && manifest.Id == Path.GetFileNameWithoutExtension(file) &&
                        Path.IsPathFullyQualified(manifest.InstallPath))
                    {
                        found.Add(manifest);
                    }
                }
                catch (Exception e) when (e is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return found;
    }
}
