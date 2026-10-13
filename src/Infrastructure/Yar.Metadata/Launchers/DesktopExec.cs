using System.Text;

namespace Yar.Metadata;

/// <summary>Splits a desktop entry's <c>Exec</c> value into arguments, following the spec's quoting rules.</summary>
public static class DesktopExec
{
    /// <summary>
    /// Returns the argument list with field codes such as <c>%f</c> left in place,
    /// or null if the value is empty or its quoting is broken.
    /// </summary>
    public static IReadOnlyList<string>? Split(string? exec)
    {
        if (string.IsNullOrWhiteSpace(exec))
            return null;

        var arguments = new List<string>();
        var current = new StringBuilder();
        var inArgument = false;

        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (c == '"')
            {
                inArgument = true;
                var closed = false;
                for (i++; i < exec.Length; i++)
                {
                    c = exec[i];
                    if (c == '"')
                    {
                        closed = true;
                        break;
                    }
                    // Inside quotes a backslash escapes ", `, $ and \.
                    if (c == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\')
                        c = exec[++i];
                    current.Append(c);
                }
                if (!closed)
                    return null;
            }
            else if (c is ' ' or '\t')
            {
                if (inArgument)
                    arguments.Add(current.ToString());
                current.Clear();
                inArgument = false;
            }
            else
            {
                inArgument = true;
                current.Append(c);
            }
        }

        if (inArgument)
            arguments.Add(current.ToString());
        return arguments.Count > 0 ? arguments : null;
    }

    /// <summary>
    /// Finds the program an <c>Exec</c> value starts: an absolute path as is, a bare name looked
    /// up in <paramref name="searchPath"/>. Returns null if it cannot be found.
    /// </summary>
    public static string? ResolveProgram(string? exec, IEnumerable<string> searchPath)
    {
        var program = Split(exec)?[0];
        return string.IsNullOrEmpty(program) ? null : FindProgram(program, searchPath);
    }

    /// <summary>Finds a program by its absolute path or, for a bare name, in <paramref name="searchPath"/>.</summary>
    public static string? FindProgram(string program, IEnumerable<string> searchPath)
    {
        if (Path.IsPathFullyQualified(program))
            return File.Exists(program) ? program : null;
        if (program.Contains('/'))
            return null;

        return searchPath
            .Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, program))
            .FirstOrDefault(File.Exists);
    }
}
