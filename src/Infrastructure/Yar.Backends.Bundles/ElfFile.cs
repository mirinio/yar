using System.Buffers.Binary;
using System.Text;

namespace Yar.Backends.Bundles;

/// <summary>
/// What a Linux program file says about itself in its header: which processor it is for, and
/// which shared libraries it asks for. The file is only read, never run.
/// </summary>
public sealed record ElfFile(ushort Machine, bool Is64Bit, bool IsProgram, string? Interpreter, IReadOnlyList<string> Needed, long EndOfSections)
{
    public const ushort X86 = 3, X8664 = 62, Arm = 40, Aarch64 = 183, RiscV = 243;

    // Headers of real programs are small; anything beyond this is not worth following.
    private const int MaxHeaders = 256;
    private const int MaxTableBytes = 4 * 1024 * 1024;

    public string MachineName => Machine switch
    {
        X86 => "32-bit Intel/AMD",
        X8664 => "64-bit Intel/AMD",
        Arm => "32-bit ARM",
        Aarch64 => "64-bit ARM",
        RiscV => "RISC-V",
        _ => $"an unusual processor (type {Machine})",
    };

    public static bool HasMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[..4].SequenceEqual("\u007fELF"u8);

    /// <summary>Reads the header, or returns null when the file is not a readable ELF file.</summary>
    public static ElfFile? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ElfFile? Read(Stream stream)
    {
        Span<byte> ident = stackalloc byte[64];
        if (!TryReadAt(stream, 0, ident) || !HasMagic(ident))
            return null;
        var is64 = ident[4] == 2;
        // Only little-endian files are read; every processor Yar runs on is little-endian.
        if (ident[4] is not (1 or 2) || ident[5] != 1)
            return null;

        var type = BinaryPrimitives.ReadUInt16LittleEndian(ident[16..]);
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(ident[18..]);
        long phoff = is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(ident[32..]) : BinaryPrimitives.ReadUInt32LittleEndian(ident[28..]);
        long shoff = is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(ident[40..]) : BinaryPrimitives.ReadUInt32LittleEndian(ident[32..]);
        int phentsize = BinaryPrimitives.ReadUInt16LittleEndian(ident[(is64 ? 54 : 42)..]);
        int phnum = BinaryPrimitives.ReadUInt16LittleEndian(ident[(is64 ? 56 : 44)..]);
        int shentsize = BinaryPrimitives.ReadUInt16LittleEndian(ident[(is64 ? 58 : 46)..]);
        int shnum = BinaryPrimitives.ReadUInt16LittleEndian(ident[(is64 ? 60 : 48)..]);

        string? interpreter = null;
        var needed = new List<string>();
        var loads = new List<(long Vaddr, long Offset, long FileSize)>();
        (long Offset, long Size)? dynamic = null;

        if (phoff > 0 && phnum is > 0 and <= MaxHeaders && phentsize >= (is64 ? 56 : 32))
        {
            var entry = new byte[phentsize];
            for (var i = 0; i < phnum; i++)
            {
                if (!TryReadAt(stream, phoff + (long)i * phentsize, entry))
                    break;
                var kind = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                long offset = is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(8)) : BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4));
                long vaddr = is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(16)) : BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(8));
                long fileSize = is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(32)) : BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(16));
                if (offset < 0 || fileSize < 0)
                    continue;
                switch (kind)
                {
                    case 1: // PT_LOAD
                        loads.Add((vaddr, offset, fileSize));
                        break;
                    case 2: // PT_DYNAMIC
                        dynamic = (offset, fileSize);
                        break;
                    case 3 when fileSize is > 1 and <= 4096: // PT_INTERP
                        var name = new byte[fileSize];
                        if (TryReadAt(stream, offset, name))
                            interpreter = Encoding.UTF8.GetString(name).TrimEnd('\0');
                        break;
                }
            }
        }

        if (dynamic is { Size: > 0 and <= MaxTableBytes } table)
            needed = ReadNeeded(stream, is64, table, loads);

        // An AppImage's payload begins where the program file ends, which is after its section table.
        var end = shoff > 0 && shnum > 0 ? shoff + (long)shnum * shentsize : 0;
        return new(machine, is64, type is 2 or 3, interpreter, needed, end);
    }

    private static List<string> ReadNeeded(
        Stream stream, bool is64, (long Offset, long Size) table, List<(long Vaddr, long Offset, long FileSize)> loads)
    {
        var needed = new List<string>();
        var bytes = new byte[table.Size];
        if (!TryReadAt(stream, table.Offset, bytes))
            return needed;

        var entrySize = is64 ? 16 : 8;
        var nameOffsets = new List<long>();
        long stringTableAddress = -1, stringTableSize = 0;
        for (var at = 0; at + entrySize <= bytes.Length; at += entrySize)
        {
            long tag = is64 ? BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at)) : BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
            long value = is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 8)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            if (tag == 0)
                break;
            if (tag == 1) // DT_NEEDED
                nameOffsets.Add(value);
            else if (tag == 5) // DT_STRTAB
                stringTableAddress = value;
            else if (tag == 10) // DT_STRSZ
                stringTableSize = value;
        }

        // The string table is given as an address in memory; the loadable segments say where that is in the file.
        var segment = loads.FirstOrDefault(load => stringTableAddress >= load.Vaddr && stringTableAddress < load.Vaddr + load.FileSize);
        if (stringTableAddress < 0 || segment.FileSize == 0 || stringTableSize is <= 0 or > MaxTableBytes)
            return needed;
        var strings = new byte[stringTableSize];
        if (!TryReadAt(stream, segment.Offset + (stringTableAddress - segment.Vaddr), strings))
            return needed;

        foreach (var offset in nameOffsets.Where(o => o >= 0 && o < strings.Length).Take(MaxHeaders))
        {
            var end = Array.IndexOf(strings, (byte)0, (int)offset);
            if (end > offset)
                needed.Add(Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset));
        }
        return needed;
    }

    private static bool TryReadAt(Stream stream, long offset, Span<byte> into)
    {
        if (offset < 0 || offset > stream.Length - into.Length)
            return false;
        stream.Position = offset;
        return stream.ReadAtLeast(into, into.Length, throwOnEndOfStream: false) == into.Length;
    }
}
