using System.Net;
using Yar.Core;

namespace Yar.Online;

/// <summary>
/// Downloads a file from a link the user gave. Only over https, also when the server forwards to
/// another address. The file lands in a folder of its own inside Yar's cache.
/// </summary>
public sealed class LinkDownloader : IAppDownloader, IDisposable
{
    public const long MaxBytes = 20L * 1024 * 1024 * 1024;

    private const int MaxForwards = 8;

    private readonly string _directory;
    private readonly HttpClient _client;

    public LinkDownloader(string directory, HttpMessageHandler? handler = null)
    {
        _directory = directory;
        // Forwarding is followed by hand, so that every address on the way can be checked.
        _client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Yar/0.1");
    }

    public async Task<LinkInfo> ProbeAsync(Uri link, CancellationToken ct)
    {
        using var response = await OpenAsync(link, ct).ConfigureAwait(false);
        return Describe(response);
    }

    public async Task<string> DownloadAsync(LinkInfo link, IProgress<long> received, CancellationToken ct)
    {
        using var response = await OpenAsync(link.Address, ct).ConfigureAwait(false);
        var now = Describe(response);
        var folder = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(folder, now.FileName);
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                    throw new InvalidOperationException("The download is larger than I accept for an app.");
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received.Report(total);
            }
            if (now.Bytes is { } expected && total != expected)
                throw new InvalidOperationException("The download ended early; the file is not complete.");
            return path;
        }
        catch (Exception e)
        {
            Discard(path);
            if (e is HttpRequestException or IOException && !ct.IsCancellationRequested)
                throw new InvalidOperationException($"The download broke off: {e.Message}", e);
            throw;
        }
    }

    public void Discard(string path)
    {
        // Only ever a file in a folder this class made.
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is null || Path.GetDirectoryName(folder) != _directory.TrimEnd('/'))
            return;
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task<HttpResponseMessage> OpenAsync(Uri link, CancellationToken ct)
    {
        try
        {
            for (var hop = 0; hop <= MaxForwards; hop++)
            {
                if (DownloadLink.Check(link.AbsoluteUri) is { Link: null, Problem: var problem })
                    throw new InvalidOperationException(hop == 0 ? problem : $"The server forwards to an address I won't use ({link.Host}): {problem}");

                using var request = new HttpRequestMessage(HttpMethod.Get, link);
                var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
                {
                    response.Dispose();
                    link = next.IsAbsoluteUri ? next : new Uri(link, next);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new InvalidOperationException(status == HttpStatusCode.NotFound
                        ? "There is nothing at that link (the server says: not found)."
                        : $"The server refused the download ({(int)status}).");
                }
                return response;
            }
            throw new InvalidOperationException("The link forwards on and on without arriving anywhere.");
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException($"I couldn't reach {link.Host}: {e.Message}", e);
        }
    }

    private static LinkInfo Describe(HttpResponseMessage response)
    {
        var address = response.RequestMessage!.RequestUri!;
        if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml")
        {
            throw new InvalidOperationException(
                "That link leads to a web page, not to a file. On the page, copy the link of the download itself.");
        }
        if (response.Content.Headers.ContentLength > MaxBytes)
            throw new InvalidOperationException("The file behind that link is larger than I accept for an app.");

        var disposition = response.Content.Headers.ContentDisposition;
        var name = SafeName(disposition?.FileNameStar ?? disposition?.FileName?.Trim('"'))
            ?? SafeName(Uri.UnescapeDataString(address.AbsolutePath.TrimEnd('/')))
            ?? "download";
        return new(address, name, response.Content.Headers.ContentLength);
    }

    // The last part of whatever the server calls the file, with nothing in it that could lead elsewhere.
    internal static string? SafeName(string? offered)
    {
        if (string.IsNullOrWhiteSpace(offered))
            return null;
        var last = offered.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var clean = new string(last.Where(c => !char.IsControl(c) && c is not ('"' or '`' or '$' or '%')).ToArray()).Trim().TrimStart('.', '-');
        if (clean.Length > 120)
            clean = clean[^120..];
        return clean.Length == 0 ? null : clean;
    }
}
