using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Yar.Core;

public enum HashKind { Md5, Sha1, Sha256, Sha512 }

/// <summary>A hash a publisher states for a file, as the user pasted it.</summary>
public sealed record PublishedHash(HashKind Kind, string Hex)
{
    /// <summary>True for the old kinds that someone determined could fake.</summary>
    public bool Weak => Kind is HashKind.Md5 or HashKind.Sha1;

    public string KindName => Kind switch
    {
        HashKind.Md5 => "MD5",
        HashKind.Sha1 => "SHA-1",
        HashKind.Sha256 => "SHA-256",
        _ => "SHA-512",
    };
}

/// <summary>
/// Compares a file with the hash its publisher states. A match means the file is, bit for bit,
/// the one the publisher describes; it says nothing about whether the publisher can be trusted.
/// </summary>
public static partial class HashCheck
{
    /// <summary>
    /// Finds the hash for <paramref name="fileName"/> in what the user pasted: a bare hash, a
    /// "hash  name" line, a "SHA256 (name) = hash" line, or a whole list of such lines.
    /// Returns the hash, or a plain reason why none could be used.
    /// </summary>
    public static (PublishedHash? Hash, string? Problem) Parse(string pasted, string fileName)
    {
        var lines = pasted.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return (null, null);

        var all = lines.SelectMany(HashesIn).ToList();
        if (all.Count == 0)
            return (null, "I can't find a hash in that. A hash is a long run of the digits 0-9 and letters a-f.");

        // In a list, the line that names this file is the one that counts.
        var named = lines.Where(line => fileName.Length > 0 && line.Contains(fileName, StringComparison.Ordinal))
            .SelectMany(HashesIn).Distinct().ToList();
        if (named.Count == 1)
            return (named[0], null);
        if (named.Count > 1)
            return (null, $"That names {fileName} with more than one hash, so I can't tell which one counts.");

        var different = all.Distinct().ToList();
        return different.Count == 1
            ? (different[0], null)
            : (null, $"I found {different.Count} hashes and none of them is marked as belonging to {fileName}.");
    }

    public static async Task<string> ComputeAsync(string path, HashKind kind, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
        var bytes = kind switch
        {
            // Weak, and offered only because some publishers still state nothing else.
#pragma warning disable CA5351, CA5350
            HashKind.Md5 => await MD5.HashDataAsync(stream, ct).ConfigureAwait(false),
            HashKind.Sha1 => await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false),
#pragma warning restore CA5351, CA5350
            HashKind.Sha256 => await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false),
            _ => await SHA512.HashDataAsync(stream, ct).ConfigureAwait(false),
        };
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>True when the file is the one the hash describes.</summary>
    public static async Task<bool> MatchesAsync(string path, PublishedHash published, CancellationToken ct) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(await ComputeAsync(path, published.Kind, ct).ConfigureAwait(false)),
            Convert.FromHexString(published.Hex));

    private static IEnumerable<PublishedHash> HashesIn(string line)
    {
        foreach (Match match in HexRun().Matches(line))
        {
            HashKind? kind = match.Length switch
            {
                32 => HashKind.Md5,
                40 => HashKind.Sha1,
                64 => HashKind.Sha256,
                128 => HashKind.Sha512,
                _ => null,
            };
            if (kind is { } known)
                yield return new(known, match.Value.ToLowerInvariant());
        }
    }

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9a-fA-F]{32,128}(?![0-9A-Za-z])")]
    private static partial Regex HexRun();
}
