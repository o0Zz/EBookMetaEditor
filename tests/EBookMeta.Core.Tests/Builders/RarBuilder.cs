using System.Text;

namespace EBookMeta.Tests.Builders;

/// <summary>
/// Assembles RAR 4 archives byte by byte, storing every file uncompressed.
/// <b>Not a RAR writer, and must not become one</b> — only the header layout is
/// implemented; compression is the part nothing in this build touches.
/// </summary>
internal sealed class RarBuilder
{
    private readonly List<Entry> _entries = [];
    private bool _solid;
    private bool _encrypted;

    private sealed record Entry(string Name, byte[] Content, bool IsDirectory = false);

    /// <summary>Block types, from the RAR 4 layout.</summary>
    private const byte MainHeaderType = 0x73;
    private const byte FileHeaderType = 0x74;
    private const byte EndHeaderType = 0x7B;

    /// <summary>Set on any header followed by data whose length it declares.</summary>
    private const ushort LongBlock = 0x8000;

    /// <summary>File header flag: this file continues a solid compression stream.</summary>
    private const ushort FileIsSolid = 0x0010;

    /// <summary>File header flag: the file data is encrypted.</summary>
    private const ushort FileIsEncrypted = 0x0004;

    /// <summary>
    /// File header flag marking a directory: bits five to seven together, which in a
    /// file's header describe the dictionary size instead.
    /// </summary>
    private const ushort FileIsDirectory = 0x00E0;

    /// <summary>DOS attributes: the archive bit, and the directory bit.</summary>
    private const uint ArchiveAttribute = 0x20;
    private const uint DirectoryAttribute = 0x10;

    /// <summary>Main header flag: the archive is solid.</summary>
    private const ushort ArchiveIsSolid = 0x0008;

    /// <summary>Compression method <c>0x30</c> — stored, no compression.</summary>
    private const byte MethodStore = 0x30;

    /// <summary>The minimum unpacker version that reads a stored RAR 4 entry.</summary>
    private const byte UnpackVersion = 20;

    /// <summary>Host OS 2 is Win32, which is what made every comic archive in the wild.</summary>
    private const byte HostOsWindows = 2;

    /// <summary>Fixed, so a fixture built twice is byte-identical.</summary>
    private static readonly DateTimeOffset FixedTimestamp =
        new(2013, 6, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Adds a file entry, stored uncompressed.</summary>
    internal RarBuilder WithFile(string name, byte[] content)
    {
        _entries.Add(new Entry(name, content));
        return this;
    }

    /// <summary>Adds a text file entry.</summary>
    internal RarBuilder WithFile(string name, string content) =>
        WithFile(name, Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// Adds a directory entry — the folder marker a comic packed from an extracted
    /// directory carries. Marked in the header flags and DOS attributes, not by a
    /// trailing separator, so nothing about the name distinguishes it from a page.
    /// </summary>
    internal RarBuilder WithDirectory(string name)
    {
        _entries.Add(new Entry(name, [], IsDirectory: true));
        return this;
    }

    /// <summary>
    /// Marks the archive solid, which is a claim about the headers and not about the
    /// data — the entries stay stored and readable. That is enough: what is under
    /// test is that <c>RarContainer</c> refuses an archive whose entries it cannot
    /// promise to serve individually.
    /// </summary>
    internal RarBuilder Solid()
    {
        _solid = true;
        return this;
    }

    /// <summary>
    /// Marks the file data encrypted, again as a header claim over readable data.
    /// A real encrypted archive would need a password this build never asks for, and
    /// the refusal happens before anything is decrypted.
    /// </summary>
    internal RarBuilder Encrypted()
    {
        _encrypted = true;
        return this;
    }

    /// <summary>Builds the archive and writes it to a file.</summary>
    internal string WriteTo(string path)
    {
        File.WriteAllBytes(path, Build());
        return path;
    }

    /// <summary>Builds the archive in memory.</summary>
    internal byte[] Build()
    {
        using var output = new MemoryStream();

        // The marker block: itself a header, with a fixed CRC, type and size that
        // together are the "Rar!\x1a\x07\x00" magic number the sniffer looks for.
        output.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00], 0, 7);

        WriteMainHeader(output);

        foreach (Entry entry in _entries)
        {
            WriteFileHeader(output, entry);
            output.Write(entry.Content, 0, entry.Content.Length);
        }

        WriteEndHeader(output);

        return output.ToArray();
    }

    /// <summary>The main header: thirteen bytes, two of them reserved and never used.</summary>
    private void WriteMainHeader(Stream output)
    {
        byte[] header = new byte[13];

        header[2] = MainHeaderType;
        WriteUInt16(header, 3, _solid ? ArchiveIsSolid : (ushort)0);
        WriteUInt16(header, 5, (ushort)header.Length);

        StampHeaderCrc(header);
        output.Write(header, 0, header.Length);
    }

    /// <summary>
    /// One file header: thirty-two fixed bytes, then the name, then the data.
    /// </summary>
    private void WriteFileHeader(Stream output, Entry entry)
    {
        byte[] name = Encoding.ASCII.GetBytes(entry.Name);
        byte[] header = new byte[32 + name.Length];

        ushort flags = LongBlock;
        if (_solid)
        {
            flags |= FileIsSolid;
        }

        if (_encrypted)
        {
            flags |= FileIsEncrypted;
        }

        if (entry.IsDirectory)
        {
            flags |= FileIsDirectory;
        }

        header[2] = FileHeaderType;
        WriteUInt16(header, 3, flags);
        WriteUInt16(header, 5, (ushort)header.Length);

        // Packed and unpacked sizes are equal because the method is "store".
        WriteUInt32(header, 7, (uint)entry.Content.Length);
        WriteUInt32(header, 11, (uint)entry.Content.Length);

        header[15] = HostOsWindows;
        WriteUInt32(header, 16, Crc32(entry.Content));
        WriteUInt32(header, 20, DosTimestamp(FixedTimestamp));
        header[24] = UnpackVersion;
        header[25] = MethodStore;
        WriteUInt16(header, 26, (ushort)name.Length);

        WriteUInt32(header, 28, entry.IsDirectory ? DirectoryAttribute : ArchiveAttribute);

        Array.Copy(name, 0, header, 32, name.Length);

        StampHeaderCrc(header);
        output.Write(header, 0, header.Length);
    }

    /// <summary>The end-of-archive block, seven bytes and no payload.</summary>
    private static void WriteEndHeader(Stream output)
    {
        byte[] header = new byte[7];

        header[2] = EndHeaderType;

        // 0x4000 tells a reader that does not know this block type to skip it,
        // which is what rar itself sets here.
        WriteUInt16(header, 3, 0x4000);
        WriteUInt16(header, 5, (ushort)header.Length);

        StampHeaderCrc(header);
        output.Write(header, 0, header.Length);
    }

    /// <summary>Writes the block's own checksum, which covers everything after it.</summary>
    private static void StampHeaderCrc(byte[] header)
    {
        uint crc = Crc32(header, 2, header.Length - 2);
        WriteUInt16(header, 0, (ushort)(crc & 0xFFFF));
    }

    /// <summary>The MS-DOS packed date and time RAR records for an entry.</summary>
    private static uint DosTimestamp(DateTimeOffset value) =>
        ((uint)(value.Year - 1980) << 25) |
        ((uint)value.Month << 21) |
        ((uint)value.Day << 16) |
        ((uint)value.Hour << 11) |
        ((uint)value.Minute << 5) |
        ((uint)value.Second / 2);

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static uint Crc32(byte[] data) => Crc32(data, 0, data.Length);

    /// <summary>
    /// The ordinary CRC-32, which RAR uses for both its headers and its file data.
    /// </summary>
    private static uint Crc32(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFF;

        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(crc & 1));
            }
        }

        return ~crc;
    }
}
