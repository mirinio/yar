namespace Yar.Core;

/// <summary>A source the user asks Yar to add.</summary>
public sealed record SourceRequest(string Name, string Url);

/// <summary>Checks an address a user typed for a new Flatpak source before anything is done with it.</summary>
public static class SourceAddress
{
    public const int MaxLength = 2048;

    /// <summary>
    /// Returns the request to carry out, or a plain-language reason why the address is not
    /// acceptable. Only https addresses of <c>.flatpakrepo</c> files are accepted: such a file
    /// carries the key that later downloads are checked against, and https keeps it from being
    /// swapped on the way.
    /// </summary>
    public static (SourceRequest? Request, string? Problem) Check(string? address, string? name = null)
    {
        address = address?.Trim() ?? "";
        if (address.Length == 0)
            return (null, "Type or paste the address of the source.");
        if (address.Length > MaxLength || address.Any(char.IsControl) || address.Any(char.IsWhiteSpace))
            return (null, "That does not look like a web address.");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return (null, "That does not look like a web address.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            return (null, "The address has to start with https://, so nobody can tamper with it on the way.");
        if (uri.UserInfo.Length > 0)
            return (null, "Addresses with a user name or password in them are not accepted.");
        if (!uri.AbsolutePath.EndsWith(".flatpakrepo", StringComparison.OrdinalIgnoreCase))
            return (null, "The address has to point to a .flatpakrepo file. The source's website usually offers one.");

        var chosen = SanitizeName(string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri.AbsolutePath))
            : name);
        if (chosen.Length == 0)
            chosen = SanitizeName(uri.Host);
        return (new SourceRequest(chosen, uri.AbsoluteUri), null);
    }

    /// <summary>Flatpak remote names: letters, digits, dash and underscore; never starting with a dash.</summary>
    public static string SanitizeName(string name)
    {
        var cleaned = new string(name.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray())
            .Trim('-', '_');
        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        return cleaned.Length > 40 ? cleaned[..40].TrimEnd('-', '_') : cleaned;
    }
}
