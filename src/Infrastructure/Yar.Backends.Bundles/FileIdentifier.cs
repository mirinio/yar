using Yar.Core;

namespace Yar.Backends.Bundles;

/// <param name="Format">The exact format within the kind: "zip", "tar.gz", "rpm", "flatpakref" and so on.</param>
/// <param name="Label">What to call it when talking to the user.</param>
/// <param name="WhyNot">For things Yar does not install: what it is and what to do instead.</param>
public sealed record FileIdentity(BundleKind Kind, string Format, string Label, string? WhyNot = null);

/// <summary>
/// Tells what a file is from its first bytes. The name is only consulted where the bytes cannot
/// decide; a file called "setup.zip" that is really a Windows program is called a Windows program.
/// </summary>
public static class FileIdentifier
{
    private const int HeaderBytes = 512;

    public static FileIdentity Identify(string path)
    {
        var header = new byte[HeaderBytes];
        int read;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory))
                return Unsupported("unknown", "Not a file", "That is a folder or is not there any more. I can only take a single file.");
            // Pipes and devices would block for ever when opened.
            if (info.LinkTarget is null && (info.Attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                return Unsupported("unknown", "Not a file", "That is not an ordinary file.");
            if (info.Length == 0)
                return Unsupported("unknown", "An empty file", "This file is empty. The download probably did not finish.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Unsupported("unknown", "A file I can't read", $"I couldn't read this file: {e.Message}");
        }
        return Identify(header.AsSpan(0, read), Path.GetFileName(path));
    }

    public static FileIdentity Identify(ReadOnlySpan<byte> header, string fileName)
    {
        var name = fileName.ToLowerInvariant();

        if (ElfFile.HasMagic(header))
        {
            return AppImageFile.DetectType(header) is not null
                ? new(BundleKind.AppImage, "appimage", "An AppImage: a whole app in one file")
                : new(BundleKind.Program, "elf", "A single program file");
        }
        if (header.StartsWith("MZ"u8))
        {
            return Unsupported("windows", "A Windows program",
                "This is a program for Windows. It can't run on Linux by itself; tools such as Bottles or Wine exist for that, " +
                "and many apps have a Linux version in the Store.");
        }
        if (header.StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0]))
            return Unsupported("windows", "A Windows installer or an old Office file", "This is a Windows installer (.msi) or an old Office document, not a Linux app.");
        if (header.StartsWith((ReadOnlySpan<byte>)[0xCF, 0xFA, 0xED, 0xFE]) || header.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xED, 0xFA, 0xCF]) || header.StartsWith((ReadOnlySpan<byte>)[0xCA, 0xFE, 0xBA, 0xBE]) ||
            name.EndsWith(".dmg", StringComparison.Ordinal))
        {
            return Unsupported("mac", "A macOS program", "This is a program for a Mac. It can't run on Linux.");
        }
        if (header.StartsWith((ReadOnlySpan<byte>)[0xED, 0xAB, 0xEE, 0xDB]))
            return new(BundleKind.Package, "rpm", "An RPM package, as Fedora and openSUSE use");
        if (header.StartsWith("!<arch>\ndebian-binary"u8))
            return new(BundleKind.Package, "deb", "A Debian package, as Debian and Ubuntu use");
        if (header.StartsWith("hsqs"u8))
        {
            return Unsupported("snap", "A Snap package",
                "This looks like a Snap package. I can't install Snap files yet; the snap command can (snap install --dangerous).");
        }
        if (header.StartsWith("PK\u0003\u0004"u8) || header.StartsWith("PK\u0005\u0006"u8))
            return new(BundleKind.Archive, "zip", "A zip archive");
        if (header.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]))
            return new(BundleKind.Archive, "tar.gz", "A compressed archive (tar.gz)");
        if (header.StartsWith((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]))
            return new(BundleKind.Archive, "tar.xz", "A compressed archive (tar.xz)");
        if (header.StartsWith("BZh"u8))
            return new(BundleKind.Archive, "tar.bz2", "A compressed archive (tar.bz2)");
        if (header.StartsWith((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD]))
            return new(BundleKind.Archive, "tar.zst", "A compressed archive (tar.zst)");
        if (header.Length >= 262 && header.Slice(257, 5).SequenceEqual("ustar"u8))
            return new(BundleKind.Archive, "tar", "An archive (tar)");
        if (header.StartsWith("7z¼¯"u8[..2]) && name.EndsWith(".7z", StringComparison.Ordinal) || header.StartsWith("Rar!"u8))
            return Unsupported("archive", "A 7z or rar archive", "I can't unpack this kind of archive yet. Unpack it yourself and look for an AppImage or a tar/zip version instead.");

        var text = System.Text.Encoding.UTF8.GetString(header).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (text.StartsWith("[Flatpak Ref]", StringComparison.Ordinal))
            return new(BundleKind.FlatpakFile, "flatpakref", "A pointer to a Flatpak app (.flatpakref)");
        if (text.StartsWith("[Flatpak Repo]", StringComparison.Ordinal))
        {
            return Unsupported("flatpakrepo", "A Flatpak source",
                "This file describes a source of apps, not an app. Add it under Sources, by its address.");
        }
        if (text.StartsWith("#!", StringComparison.Ordinal))
        {
            return Unsupported("script", "An installer script",
                "This is a script that installs things in its own way. I can't see ahead of time what it would put where, " +
                "so I couldn't keep track of it or remove it for you later. If you trust it, run it yourself in a terminal.");
        }
        if (text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return Unsupported("html", "A web page",
                "This is a web page, not an app. The link probably leads to a download page; copy the link of the file itself.");
        }
        // A Flatpak bundle has no simple signature of its own; its name is the only clue.
        if (name.EndsWith(".flatpak", StringComparison.Ordinal))
            return new(BundleKind.FlatpakFile, "flatpak", "A Flatpak app as a file (.flatpak)");

        return Unsupported("unknown", "A file I don't recognise",
            "I can't tell what kind of file this is, so I can't install it. I know AppImages, zip and tar archives, " +
            "RPM and Debian packages and Flatpak files.");
    }

    private static FileIdentity Unsupported(string format, string label, string whyNot) =>
        new(BundleKind.Unsupported, format, label, whyNot);
}
