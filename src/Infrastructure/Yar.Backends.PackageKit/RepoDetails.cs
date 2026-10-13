namespace Yar.Backends.PackageKit;

/// <summary>What a repository's configuration file says about it, beyond what PackageKit reports.</summary>
public sealed record RepoDetail(string RepoId, string? Url, bool? SignatureCheck, string FilePath);

public interface IRepoDetailReader
{
    /// <summary>Details per repository ID. Empty when this reader's files do not exist here.</summary>
    IReadOnlyDictionary<string, RepoDetail> Read();
}

/// <summary>
/// Reads <c>*.repo</c> files as used by DNF (<c>/etc/yum.repos.d</c>) and zypper (<c>/etc/zypp/repos.d</c>).
/// </summary>
public sealed class RepoFileReader(IReadOnlyList<string> directories) : IRepoDetailReader
{
    private const long MaxFileBytes = 1024 * 1024;

    public static RepoFileReader ForSystem() => new(["/etc/yum.repos.d", "/etc/zypp/repos.d"]);

    public IReadOnlyDictionary<string, RepoDetail> Read()
    {
        var details = new Dictionary<string, RepoDetail>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.Exists(directory))
                    continue;
                foreach (var file in Directory.EnumerateFiles(directory, "*.repo").Order(StringComparer.Ordinal))
                {
                    if (new FileInfo(file).Length > MaxFileBytes)
                        continue;
                    foreach (var detail in Parse(File.ReadAllText(file), file))
                        details.TryAdd(detail.RepoId, detail);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Details are optional; the repository is still listed without them.
            }
        }
        return details;
    }

    public static IReadOnlyList<RepoDetail> Parse(string content, string filePath)
    {
        var details = new List<RepoDetail>();
        string? id = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (id is null)
                return;
            // Only the first URL of a list; it is shown to the user, not fetched.
            var url = FirstValue("baseurl") ?? FirstValue("metalink") ?? FirstValue("mirrorlist");
            bool? signatureCheck = values.TryGetValue("gpgcheck", out var gpgcheck)
                ? gpgcheck is "1" or "true" or "yes" or "on"
                : null;
            details.Add(new(id, url, signatureCheck, filePath));
        }

        string? FirstValue(string key) =>
            values.TryGetValue(key, out var value)
                ? value.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                : null;

        string? lastKey = null;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';')
                continue;

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                Flush();
                id = trimmed[1..^1].Trim();
                values.Clear();
                lastKey = null;
                continue;
            }
            if (id is null)
                continue;

            // An indented line continues the previous value (multi-line baseurl lists).
            if (char.IsWhiteSpace(line[0]) && lastKey is not null)
            {
                values[lastKey] += " " + trimmed;
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;
            lastKey = trimmed[..separator].Trim();
            values[lastKey] = trimmed[(separator + 1)..].Trim();
        }
        Flush();
        return details;
    }
}
