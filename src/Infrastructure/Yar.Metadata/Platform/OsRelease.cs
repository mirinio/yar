namespace Yar.Metadata;

/// <summary>Which distribution this is, from <c>/etc/os-release</c>.</summary>
public sealed record OsRelease(string? Id, string? Name)
{
    /// <summary>The distribution's website, if it names one.</summary>
    public string? HomeUrl { get; init; }

    public static OsRelease Read(string path = "/etc/os-release")
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : new(null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(null, null);
        }
    }

    public static OsRelease Parse(string content)
    {
        string? id = null, name = null, homeUrl = null;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('=');
            if (separator <= 0 || line[0] == '#')
                continue;

            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            switch (line[..separator])
            {
                case "ID": id = value; break;
                case "NAME": name = value; break;
                case "HOME_URL": homeUrl = value; break;
            }
        }
        return new(string.IsNullOrEmpty(id) ? null : id, string.IsNullOrEmpty(name) ? null : name)
        {
            HomeUrl = string.IsNullOrEmpty(homeUrl) ? null : homeUrl,
        };
    }
}
