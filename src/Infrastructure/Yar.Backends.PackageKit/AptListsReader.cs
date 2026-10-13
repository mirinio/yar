namespace Yar.Backends.PackageKit;

/// <summary>A way to list repositories when PackageKit itself reports none.</summary>
public interface IRepoListFallback
{
    IReadOnlyList<(PkRepo Repo, RepoDetail Detail)> Read();
}

/// <summary>
/// Lists the repositories apt has downloaded an index for, from the release files in
/// <c>/var/lib/apt/lists</c>. On Ubuntu, PackageKit reports no repositories at all, although
/// it does name each installed package's origin; the IDs built here are the ones it uses there.
/// </summary>
public sealed class AptListsReader(string directory = "/var/lib/apt/lists") : IRepoListFallback
{
    private const int HeaderBytes = 8192;

    public IReadOnlyList<(PkRepo Repo, RepoDetail Detail)> Read()
    {
        var repos = new List<(PkRepo, RepoDetail)>();
        try
        {
            if (!Directory.Exists(directory))
                return repos;

            foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal)
                .Where(f => f.EndsWith("_InRelease", StringComparison.Ordinal) || f.EndsWith("_Release", StringComparison.Ordinal)))
            {
                using var stream = File.OpenRead(file);
                var buffer = new byte[HeaderBytes];
                var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                repos.AddRange(Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, read), file));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return repos.DistinctBy(r => r.Item1.Id).ToList();
    }

    /// <summary>One repository per component named in a release file's header.</summary>
    public static IReadOnlyList<(PkRepo Repo, RepoDetail Detail)> Parse(string header, string filePath)
    {
        string? origin = null, label = null, suite = null, components = null;
        foreach (var rawLine in header.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0)
                continue;
            var value = line[(separator + 2)..].Trim();
            switch (line[..separator])
            {
                case "Origin": origin ??= value; break;
                case "Label": label ??= value; break;
                case "Suite": suite ??= value; break;
                case "Components": components ??= value; break;
            }
        }
        if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(suite) || string.IsNullOrEmpty(components))
            return [];

        // The file is named after where it came from: host, then the path with "_" for "/".
        var host = Path.GetFileName(filePath).Split('_')[0];
        return components.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(component => (
                new PkRepo($"{origin.ToLowerInvariant().Replace(' ', '-')}-{suite}-{component}", $"{label ?? origin} {suite} ({component})", Enabled: true),
                new RepoDetail("", Url: Uri.CheckHostName(host) == UriHostNameType.Dns ? host : null, SignatureCheck: null, filePath)))
            .ToList();
    }
}
