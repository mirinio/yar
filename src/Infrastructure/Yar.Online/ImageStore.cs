using System.Security.Cryptography;
using System.Text;
using Yar.Core;

namespace Yar.Online;

/// <summary>
/// Downloads pictures of apps and keeps them on disk so each is fetched once. Pictures come from
/// other people's servers, so every answer is checked for host, kind and size before it is kept.
/// </summary>
public sealed class ImageStore(string directory, long maxTotalBytes = 200L * 1024 * 1024)
{
    public const int MaxImageBytes = 5 * 1024 * 1024;

    private static readonly string[] AcceptedTypes = ["image/png", "image/jpeg", "image/webp"];

    public async Task<byte[]?> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        if (!ScreenshotUrls.IsAllowed(url))
            return null;

        var path = Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url))));
        try
        {
            if (File.Exists(path))
            {
                // Marks the picture as recently used, so pruning removes it last.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        byte[] bytes;
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                !AcceptedTypes.Contains(response.Content.Headers.ContentType?.MediaType, StringComparer.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentLength > MaxImageBytes ||
                // A redirect must not lead off the accepted hosts.
                !ScreenshotUrls.IsAllowed(response.RequestMessage?.RequestUri?.AbsoluteUri))
            {
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                // The announced length can be missing or wrong; count what actually arrives.
                if (buffer.Length + read > MaxImageBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }
            bytes = buffer.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }

        if (bytes.Length == 0)
            return null;
        Keep(path, bytes);
        return bytes;
    }

    private void Keep(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temporary = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            Prune();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A picture that cannot be kept is simply downloaded again next time.
        }
    }

    // Keeps the folder under its limit by removing the pictures not looked at for the longest time.
    private void Prune()
    {
        var files = new DirectoryInfo(directory).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total > maxTotalBytes)
                file.Delete();
        }
    }
}
