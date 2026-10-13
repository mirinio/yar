namespace Yar.Backends.Bundles;

/// <summary>Recognises AppImages by their header. The file is only read, never run.</summary>
public static class AppImageFile
{
    // Anything smaller cannot be a real AppImage. This also skips FIFOs and sockets,
    // which report a length of zero and would block when opened.
    private const long MinimumBytes = 4096;

    /// <summary>Returns the AppImage type (1 or 2), or null if the file is not an AppImage.</summary>
    public static int? DetectType(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MinimumBytes)
                return null;

            Span<byte> header = stackalloc byte[11];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
                ? DetectType(header)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static int? DetectType(ReadOnlySpan<byte> header)
    {
        // ELF magic at offset 0, then "AI" and the type byte at offset 8.
        if (header.Length < 11 || !header[..4].SequenceEqual("\u007fELF"u8))
            return null;
        if (header[8] != (byte)'A' || header[9] != (byte)'I')
            return null;
        return header[10] is 1 or 2 ? header[10] : null;
    }
}
