using System.Text.RegularExpressions;
using Yar.Core;

namespace Yar.Backends.Bundles;

// The names Yar gives to what it creates for an added app: the app's own name taken from a
// file name, the short name its files and folders get, and the text of its menu entry.
public sealed partial class BundleIntake
{
    internal static string LauncherText(
        string name, string? summary, string program, string? workingFolder, string? icon,
        IReadOnlyList<string> categories, string slug, bool takesFiles)
    {
        var lines = new List<string>
        {
            "[Desktop Entry]",
            "Type=Application",
            $"Name={CleanValue(name)}",
        };
        if (!string.IsNullOrWhiteSpace(summary))
            lines.Add($"Comment={CleanValue(summary)}");
        lines.Add($"Exec=\"{program}\"{(takesFiles ? " %U" : "")}");
        if (workingFolder is not null)
            lines.Add($"Path={workingFolder}");
        lines.Add($"Icon={icon ?? "application-x-executable"}");
        lines.Add("Terminal=false");
        var kept = categories.Select(CleanValue).Where(c => c.Length > 0 && c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')).ToList();
        if (kept.Count > 0)
            lines.Add($"Categories={string.Join(';', kept)};");
        lines.Add($"X-Yar-App={slug}");
        return string.Join('\n', lines) + "\n";
    }

    // One line, no control characters, and no backslash to be read as an escape.
    private static string CleanValue(string text) =>
        new string(text.Where(c => !char.IsControl(c) && c != '\\').ToArray()).Trim();

    private static bool IsPlainRelative(string target) =>
        target.Length is > 0 and < 300 && !Path.IsPathRooted(target) && !target.Split('/').Contains("..") &&
        !target.Any(c => char.IsControl(c) || c is '*' or '?' or '[');

    private static string DestinationName(string slug, BundleKind kind) =>
        kind == BundleKind.AppImage ? slug + ".AppImage" : slug;

    // A short, plain name for files and folders, free in every place it will be used.
    private string FreeSlug(string name, string folder, BundleKind kind)
    {
        var plain = NotSlugCharacter().Replace(name.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormKD), "-").Trim('-');
        if (plain.Length == 0)
            plain = "app";
        if (plain.Length > 48)
            plain = plain[..48].Trim('-');
        for (var attempt = 1; ; attempt++)
        {
            var slug = attempt == 1 ? plain : $"{plain}-{attempt}";
            var destination = Path.Combine(folder, DestinationName(slug, kind));
            if (!manifests.Exists(slug) && !File.Exists(destination) && !Directory.Exists(destination) &&
                !File.Exists(Path.Combine(places.LauncherDir, $"yar-{slug}.desktop")) &&
                !IconEndings.Any(ending => File.Exists(Path.Combine(places.IconDir, slug + ending))))
            {
                return slug;
            }
        }
    }

    /// <summary>"krita-5.2.9-x86_64.AppImage" becomes "Krita": the file name without version, processor and file type.</summary>
    public static string CleanName(string fileName)
    {
        var name = FileTypeEnding().Replace(fileName, "");
        name = VersionOnwards().Replace(name, "");
        name = ProcessorOrSystem().Replace(name, " ");
        name = name.Replace('_', ' ').Replace('-', ' ').Trim(' ', '.');
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!name.Any(char.IsLetter))
            return "App";
        return char.IsLower(name[0]) && name.Skip(1).All(c => !char.IsUpper(c)) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    [GeneratedRegex(@"(\.tar)?\.(appimage|zip|gz|tgz|xz|txz|bz2|zst|tar|run|bin|rpm|deb|flatpak|flatpakref)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileTypeEnding();

    [GeneratedRegex(@"[-_ .]v?\d+(\.\d+)*([-_.][0-9A-Za-z]+)*$")]
    private static partial Regex VersionOnwards();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(x86[-_]64|amd64|x64|aarch64|arm64|i[36]86|linux|gnu|glibc|portable)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessorOrSystem();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotSlugCharacter();
}
