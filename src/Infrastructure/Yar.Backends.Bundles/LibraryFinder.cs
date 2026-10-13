namespace Yar.Backends.Bundles;

/// <summary>
/// Answers "does this computer have the shared library a program asks for?" by looking in the
/// folders the system loads libraries from. Programs are not run to find out.
/// </summary>
public sealed class LibraryFinder
{
    private static readonly string[] UsualFolders =
    [
        "/lib64", "/usr/lib64", "/lib", "/usr/lib", "/usr/local/lib64", "/usr/local/lib",
        "/lib/x86_64-linux-gnu", "/usr/lib/x86_64-linux-gnu", "/lib/aarch64-linux-gnu", "/usr/lib/aarch64-linux-gnu",
        "/lib/i386-linux-gnu", "/usr/lib/i386-linux-gnu",
    ];

    private readonly List<string> _folders;

    public LibraryFinder(IEnumerable<string>? folders = null) =>
        _folders = (folders ?? UsualFolders.Concat(ConfiguredFolders("/etc/ld.so.conf", depth: 0)))
            .Distinct(StringComparer.Ordinal).Where(Directory.Exists).ToList();

    /// <summary>True when a library of that name, built for the same kind of processor, is installed.</summary>
    public bool Has(string name, ElfFile askedBy)
    {
        // A name with a folder in it is not something to go looking for.
        if (name.Length == 0 || name.Contains('/'))
            return false;
        foreach (var folder in _folders)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path) && ElfFile.Read(path) is { } found && found.Machine == askedBy.Machine)
                return true;
        }
        return false;
    }

    // /etc/ld.so.conf lists further folders, and may include other files that list more.
    private static IEnumerable<string> ConfiguredFolders(string file, int depth)
    {
        if (depth > 2 || !File.Exists(file))
            yield break;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var raw in lines.Take(500))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.StartsWith("include ", StringComparison.Ordinal))
            {
                var pattern = line["include ".Length..].Trim();
                var folder = Path.GetDirectoryName(pattern);
                if (folder is null || !Directory.Exists(folder))
                    continue;
                foreach (var included in Directory.EnumerateFiles(folder, Path.GetFileName(pattern)).Take(100))
                {
                    foreach (var found in ConfiguredFolders(included, depth + 1))
                        yield return found;
                }
            }
            else if (Path.IsPathFullyQualified(line))
            {
                yield return line.TrimEnd('/');
            }
        }
    }
}
