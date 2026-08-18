using System.Text;

namespace EBookMeta.Tests.Builders;

/// <summary>
/// Assembles 7z archives byte by byte, storing every file with the Copy coder and
/// leaving the header uncompressed. <b>Not a 7z writer, and must not become one</b> —
/// only the published header layout is implemented; compression is exactly the part
/// nothing in this build touches.
/// </summary>
internal sealed class SevenZipBuilder
{
    private readonly List<Entry> _entries = [];
    private bool _solid;
    private bool _encrypted;

    private sealed record Entry(string Name, byte[] Content, bool IsDirectory = false);

    /// <summary>Structure ids, from the published 7z header layout.</summary>
    private const byte IdEnd = 0x00;
    private const byte IdHeader = 0x01;
    private const byte IdMainStreamsInfo = 0x04;
    private const byte IdFilesInfo = 0x05;
    private const byte IdPackInfo = 0x06;
    private const byte IdUnpackInfo = 0x07;
    private const byte IdSubStreamsInfo = 0x08;
    private const byte IdSize = 0x09;
    private const byte IdFolder = 0x0B;
    private const byte IdCodersUnpackSize = 0x0C;
    private const byte IdNumUnpackStream = 0x0D;
    private const byte IdEmptyStream = 0x0E;
    private const byte IdName = 0x11;

    /// <summary>Coder id 0 is Copy — the entries are stored, not compressed.</summary>
    private static readonly byte[] CopyCoder = [0x00];

    /// <summary>
    /// AES-256 + SHA-256. Only ever written as a claim in the header: the bytes stay
    /// readable, and the refusal happens before anything is decrypted.
    /// </summary>
    private static readonly byte[] AesCoder = [0x06, 0xF1, 0x07, 0x01];

    /// <summary>Adds a file entry, stored uncompressed.</summary>
    internal SevenZipBuilder WithFile(string name, byte[] content)
    {
        _entries.Add(new Entry(name, content));
        return this;
    }

    /// <summary>Adds a text file entry.</summary>
    internal SevenZipBuilder WithFile(string name, string content) =>
        WithFile(name, Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// Adds a folder marker, which 7z records as a file with no stream and no
    /// trailing separator on the name.
    /// </summary>
    internal SevenZipBuilder WithDirectory(string name)
    {
        _entries.Add(new Entry(name, [], IsDirectory: true));
        return this;
    }

    /// <summary>
    /// Packs every file into one compression block, which is what 7-Zip does by
    /// default and therefore what most comics in the wild look like.
    /// </summary>
    internal SevenZipBuilder Solid()
    {
        _solid = true;
        return this;
    }

    /// <summary>
    /// Names the AES coder in the header over data that is in fact readable. That is
    /// enough: what is under test is that the container refuses an archive it would
    /// need a password for.
    /// </summary>
    internal SevenZipBuilder Encrypted()
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
        List<Entry> files = [.. _entries.Where(e => !e.IsDirectory)];

        using var packed = new MemoryStream();
        foreach (Entry file in files)
        {
            packed.Write(file.Content, 0, file.Content.Length);
        }

        byte[] data = packed.ToArray();
        byte[] header = BuildHeader(files);

        using var output = new MemoryStream();

        output.Write(SignatureHeader(data.Length, header.Length, header), 0, 32);
        output.Write(data, 0, data.Length);
        output.Write(header, 0, header.Length);

        return output.ToArray();
    }

    /// <summary>The fixed 32-byte header the file starts with.</summary>
    private static byte[] SignatureHeader(int packedLength, int headerLength, byte[] header)
    {
        byte[] block = new byte[32];

        byte[] magic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        Array.Copy(magic, block, magic.Length);

        // Format version 0.4, which is what every 7-Zip since 4.x writes.
        block[6] = 0;
        block[7] = 4;

        // Everything from offset 12 on is covered by the CRC at offset 8, and the
        // next header's offset is relative to the end of this block.
        WriteUInt64(block, 12, (ulong)packedLength);
        WriteUInt64(block, 20, (ulong)headerLength);
        WriteUInt32(block, 28, Crc32(header));
        WriteUInt32(block, 8, Crc32(block, 12, 20));

        return block;
    }

    /// <summary>The next header: the whole directory of the archive, uncompressed.</summary>
    private byte[] BuildHeader(List<Entry> files)
    {
        using var output = new MemoryStream();

        output.WriteByte(IdHeader);

        if (files.Count > 0)
        {
            output.WriteByte(IdMainStreamsInfo);
            WritePackInfo(output, files);
            WriteUnpackInfo(output, files);
            WriteSubStreamsInfo(output, files);
            output.WriteByte(IdEnd);
        }

        WriteFilesInfo(output);

        output.WriteByte(IdEnd);

        return output.ToArray();
    }

    /// <summary>Where the packed streams are and how long each one is.</summary>
    private void WritePackInfo(Stream output, List<Entry> files)
    {
        output.WriteByte(IdPackInfo);
        WriteNumber(output, 0);
        WriteNumber(output, _solid ? 1UL : (ulong)files.Count);

        output.WriteByte(IdSize);

        if (_solid)
        {
            WriteNumber(output, (ulong)files.Sum(f => (long)f.Content.Length));
        }
        else
        {
            foreach (Entry file in files)
            {
                WriteNumber(output, (ulong)file.Content.Length);
            }
        }

        output.WriteByte(IdEnd);
    }

    /// <summary>The coder chain each block was packed with, and what it unpacks to.</summary>
    private void WriteUnpackInfo(Stream output, List<Entry> files)
    {
        int folders = _solid ? 1 : files.Count;
        byte[] coder = _encrypted ? AesCoder : CopyCoder;

        output.WriteByte(IdUnpackInfo);
        output.WriteByte(IdFolder);
        WriteNumber(output, (ulong)folders);

        // External 0: the folder definitions are right here rather than in a stream.
        output.WriteByte(0);

        for (int i = 0; i < folders; i++)
        {
            WriteNumber(output, 1);

            // The low four bits are the coder id's length; 0x20 says properties
            // follow, which only the AES coder claims to have. Neither is complex,
            // so each takes one stream in and puts one out, and a single-coder
            // folder needs no bind pairs and no packed-stream index.
            output.WriteByte((byte)(coder.Length | (_encrypted ? 0x20 : 0x00)));
            output.Write(coder, 0, coder.Length);

            if (_encrypted)
            {
                byte[] properties = [0x53, 0x00];
                WriteNumber(output, (ulong)properties.Length);
                output.Write(properties, 0, properties.Length);
            }
        }

        output.WriteByte(IdCodersUnpackSize);

        if (_solid)
        {
            WriteNumber(output, (ulong)files.Sum(f => (long)f.Content.Length));
        }
        else
        {
            foreach (Entry file in files)
            {
                WriteNumber(output, (ulong)file.Content.Length);
            }
        }

        output.WriteByte(IdEnd);
    }

    /// <summary>
    /// How the blocks divide back into the files inside them. A folder per file needs
    /// nothing said — one substream each, sized by its folder — but the structure is
    /// written all the same: a reader that finds no <c>kSubStreamsInfo</c> is left with
    /// no sizes at all, and 7-Zip always writes one.
    /// </summary>
    private void WriteSubStreamsInfo(Stream output, List<Entry> files)
    {
        output.WriteByte(IdSubStreamsInfo);

        if (_solid)
        {
            output.WriteByte(IdNumUnpackStream);
            WriteNumber(output, (ulong)files.Count);

            // Every size but the last, which is whatever remains of the block.
            output.WriteByte(IdSize);
            for (int i = 0; i < files.Count - 1; i++)
            {
                WriteNumber(output, (ulong)files[i].Content.Length);
            }
        }

        output.WriteByte(IdEnd);
    }

    /// <summary>The names, in order, and which of them carry no stream.</summary>
    private void WriteFilesInfo(Stream output)
    {
        output.WriteByte(IdFilesInfo);
        WriteNumber(output, (ulong)_entries.Count);

        if (_entries.Any(e => e.IsDirectory))
        {
            // A bit per entry, most significant first. Set means "no stream", and
            // with no kEmptyFile vector to say otherwise every one of them is a
            // directory rather than a zero-length file.
            byte[] bits = BitVector(_entries.Select(e => e.IsDirectory));

            output.WriteByte(IdEmptyStream);
            WriteNumber(output, (ulong)bits.Length);
            output.Write(bits, 0, bits.Length);
        }

        using var names = new MemoryStream();

        // External 0, then UTF-16LE names each closed by a NUL.
        names.WriteByte(0);
        foreach (Entry entry in _entries)
        {
            byte[] text = Encoding.Unicode.GetBytes(entry.Name + "\0");
            names.Write(text, 0, text.Length);
        }

        byte[] block = names.ToArray();

        output.WriteByte(IdName);
        WriteNumber(output, (ulong)block.Length);
        output.Write(block, 0, block.Length);

        output.WriteByte(IdEnd);
    }

    private static byte[] BitVector(IEnumerable<bool> bits)
    {
        bool[] values = [.. bits];
        byte[] packed = new byte[(values.Length + 7) / 8];

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
            {
                packed[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return packed;
    }

    /// <summary>
    /// 7z's variable-length number: the leading byte's high bits say how many more
    /// follow, and those follow least significant first.
    /// </summary>
    private static void WriteNumber(Stream output, ulong value)
    {
        byte first = 0;
        byte mask = 0x80;
        int extra = 0;

        for (; extra < 8; extra++)
        {
            if (value < 1UL << (7 * (extra + 1)))
            {
                first |= (byte)(value >> (8 * extra));
                break;
            }

            first |= mask;
            mask >>= 1;
        }

        output.WriteByte(first);

        for (int i = 0; i < extra; i++)
        {
            output.WriteByte((byte)(value >> (8 * i)));
        }
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        for (int i = 0; i < 4; i++)
        {
            buffer[offset + i] = (byte)(value >> (8 * i));
        }
    }

    private static void WriteUInt64(byte[] buffer, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            buffer[offset + i] = (byte)(value >> (8 * i));
        }
    }

    private static uint Crc32(byte[] data) => Crc32(data, 0, data.Length);

    /// <summary>The ordinary CRC-32, which 7z uses for its headers.</summary>
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
