using System.Text;

namespace EBookMeta.Tests.Builders;

/// <summary>Assembles MOBI databases byte by byte, the way kindlegen would.</summary>
internal sealed class MobiBuilder
{
    private const int PalmHeaderLength = 78;
    private const int RecordEntryLength = 8;

    private readonly List<(int Type, byte[] Data)> _exth = [];
    private readonly List<byte[]> _extraRecords = [];

    private string _databaseName = "The-Dolls-House";
    private string _type = "BOOK";
    private string _creator = "MOBI";
    private string? _fullName = "The Doll's House";
    private int _textEncoding = 65001;
    private int _encryptionType;
    private int _mobiHeaderLength = 0xC8;
    private int _firstImageIndex;
    private byte[]? _coverImage;
    private MobiBuilder? _kf8;

    /// <summary>Adds an EXTH record with a text payload.</summary>
    internal MobiBuilder WithExth(int type, string value)
    {
        _exth.Add((type, TextEncoding.GetBytes(value)));
        return this;
    }

    /// <summary>Adds an EXTH record with a raw payload.</summary>
    internal MobiBuilder WithExth(int type, byte[] value)
    {
        _exth.Add((type, value));
        return this;
    }

    /// <summary>Adds an EXTH record holding a big-endian 32-bit number.</summary>
    internal MobiBuilder WithExthNumber(int type, uint value)
    {
        _exth.Add((type, BigEndian(value)));
        return this;
    }

    /// <summary>Sets the title in the header's own name field.</summary>
    internal MobiBuilder WithFullName(string? name)
    {
        _fullName = name;
        return this;
    }

    /// <summary>Writes the strings in Windows-1252 rather than UTF-8.</summary>
    internal MobiBuilder WithWindows1252()
    {
        _textEncoding = 1252;
        return this;
    }

    /// <summary>Marks the text as DRM-encrypted.</summary>
    internal MobiBuilder WithDrm(int encryptionType = 2)
    {
        _encryptionType = encryptionType;
        return this;
    }

    /// <summary>Uses a shorter MOBI header, as older producers wrote.</summary>
    internal MobiBuilder WithMobiHeaderLength(int length)
    {
        _mobiHeaderLength = length;
        return this;
    }

    /// <summary>Sets the type and creator tags, for PRC and AZW fixtures.</summary>
    internal MobiBuilder WithTags(string type, string creator)
    {
        _type = type;
        _creator = creator;
        return this;
    }

    /// <summary>Appends a text record, so the database is not header-only.</summary>
    internal MobiBuilder WithTextRecord(string text)
    {
        _extraRecords.Add(TextEncoding.GetBytes(text));
        return this;
    }

    /// <summary>
    /// Appends an image record and declares it the cover, wiring up both the
    /// first-image index and EXTH 201.
    /// </summary>
    internal MobiBuilder WithCover(byte[] image)
    {
        _coverImage = image;
        return this;
    }

    /// <summary>
    /// Appends a second MOBI header, making this the joint MOBI and KF8 file that
    /// kindlegen produces for an AZW3.
    /// </summary>
    internal MobiBuilder WithKf8Part(MobiBuilder kf8)
    {
        _kf8 = kf8;
        return this;
    }

    private Encoding TextEncoding => _textEncoding == 65001
        ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        : Encoding.GetEncoding(1252);

    /// <summary>Builds the database and writes it to a file.</summary>
    internal string WriteTo(string path)
    {
        File.WriteAllBytes(path, Build());
        return path;
    }

    /// <summary>Builds the database bytes.</summary>
    internal byte[] Build()
    {
        List<byte[]> records = BuildRecords();

        using var output = new MemoryStream();

        // PalmDB file header: a 32-byte name, then the dates and identifiers a
        // reader keeps but this build only has to preserve.
        byte[] header = new byte[PalmHeaderLength];
        byte[] name = Encoding.ASCII.GetBytes(_databaseName);
        Array.Copy(name, 0, header, 0, Math.Min(name.Length, 31));

        WriteBigEndian(header, 36, 0x4C21B7B5);  // creation date
        WriteBigEndian(header, 40, 0x4C21B7B5);  // modification date
        WriteBigEndian(header, 48, 1);           // modification number
        Encoding.ASCII.GetBytes(_type, 0, 4, header, 60);
        Encoding.ASCII.GetBytes(_creator, 0, 4, header, 64);
        WriteBigEndian(header, 68, 0x00000010);  // unique id seed
        header[76] = (byte)(records.Count >> 8);
        header[77] = (byte)records.Count;

        output.Write(header, 0, header.Length);

        // The record table, then two bytes of padding, which is what every real
        // PalmDB puts between the table and the first record.
        int offset = PalmHeaderLength + (records.Count * RecordEntryLength) + 2;

        for (int i = 0; i < records.Count; i++)
        {
            byte[] entry = new byte[RecordEntryLength];
            WriteBigEndian(entry, 0, (uint)offset);
            entry[4] = 0;

            int uniqueId = i * 2;
            entry[5] = (byte)(uniqueId >> 16);
            entry[6] = (byte)(uniqueId >> 8);
            entry[7] = (byte)uniqueId;

            output.Write(entry, 0, entry.Length);
            offset += records[i].Length;
        }

        output.Write(new byte[2], 0, 2);

        foreach (byte[] record in records)
        {
            output.Write(record, 0, record.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Lays out the records, resolving the indexes the headers have to point at.
    /// </summary>
    private List<byte[]> BuildRecords()
    {
        // The layout has to be known before record 0 can be written, because the
        // header states where the images and the KF8 part begin.
        int count = 1 + _extraRecords.Count;
        int coverIndex = _coverImage is null ? -1 : count++;
        int kf8Index = _kf8 is null ? -1 : count;

        _firstImageIndex = coverIndex;

        var records = new List<byte[]>(count) { BuildHeaderRecord(coverIndex, kf8Index) };
        records.AddRange(_extraRecords);

        if (_coverImage is not null)
        {
            records.Add(_coverImage);
        }

        if (_kf8 is not null)
        {
            records.AddRange(_kf8.BuildRecords());
        }

        return records;
    }

    private byte[] BuildHeaderRecord(int coverIndex, int kf8Index)
    {
        var exth = new List<(int Type, byte[] Data)>(_exth);

        if (coverIndex >= 0)
        {
            // EXTH 201 is relative to the first image record, not absolute.
            exth.Add((201, BigEndian(0)));
        }

        if (kf8Index >= 0)
        {
            exth.Add((121, BigEndian((uint)kf8Index)));
        }

        byte[] exthBlock = BuildExth(exth);
        byte[] nameBytes = _fullName is null ? [] : TextEncoding.GetBytes(_fullName);

        int mobiStart = 16;
        int exthStart = mobiStart + _mobiHeaderLength;
        int gap = 2;
        int nameOffset = exthStart + exthBlock.Length + gap;

        int total = nameOffset + nameBytes.Length;
        total += 2 + ((4 - ((nameBytes.Length + 2) % 4)) % 4);

        byte[] record = new byte[total];

        // PalmDOC header.
        record[1] = 1;                                    // compression: none
        WriteBigEndian(record, 4, 4096);                  // text length
        record[9] = 1;                                    // record count
        WriteBigEndian16(record, 10, 4096);               // record size
        WriteBigEndian16(record, 12, (ushort)_encryptionType);

        // MOBI header.
        Encoding.ASCII.GetBytes("MOBI", 0, 4, record, mobiStart);
        WriteBigEndian(record, mobiStart + 4, (uint)_mobiHeaderLength);
        WriteBigEndian(record, mobiStart + 8, 2);         // mobi type: book
        WriteBigEndian(record, mobiStart + 0x0C, (uint)_textEncoding);
        WriteBigEndian(record, mobiStart + 0x10, 0xDEADBEEF);
        WriteBigEndian(record, mobiStart + 0x14, 6);      // file version

        if (_mobiHeaderLength >= 0x48)
        {
            WriteBigEndian(record, mobiStart + 0x44, (uint)nameOffset);
            WriteBigEndian(record, mobiStart + 0x48, (uint)nameBytes.Length);
        }

        if (_mobiHeaderLength >= 0x60)
        {
            WriteBigEndian(
                record, mobiStart + 0x5C, coverIndex < 0 ? uint.MaxValue : (uint)coverIndex);
        }

        if (_mobiHeaderLength >= 0x74 && exthBlock.Length > 0)
        {
            WriteBigEndian(record, mobiStart + 0x70, 0x40);
        }

        exthBlock.CopyTo(record, exthStart);
        nameBytes.CopyTo(record, nameOffset);

        return record;
    }

    private static byte[] BuildExth(List<(int Type, byte[] Data)> records)
    {
        if (records.Count == 0)
        {
            return [];
        }

        int length = 12;

        foreach ((int _, byte[] data) in records)
        {
            length += 8 + data.Length;
        }

        int padded = length + ((4 - (length % 4)) % 4);
        byte[] block = new byte[padded];

        Encoding.ASCII.GetBytes("EXTH", 0, 4, block, 0);
        WriteBigEndian(block, 4, (uint)padded);
        WriteBigEndian(block, 8, (uint)records.Count);

        int position = 12;

        foreach ((int type, byte[] data) in records)
        {
            WriteBigEndian(block, position, (uint)type);
            WriteBigEndian(block, position + 4, (uint)(data.Length + 8));
            data.CopyTo(block, position + 8);
            position += 8 + data.Length;
        }

        return block;
    }

    private static byte[] BigEndian(uint value)
    {
        byte[] bytes = new byte[4];
        WriteBigEndian(bytes, 0, value);
        return bytes;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteBigEndian16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    /// <summary>
    /// A book with every field this build maps, plus two EXTH records it does not
    /// so their survival across a write is testable.
    /// </summary>
    internal static MobiBuilder Typical() =>
        new MobiBuilder()
            .WithExth(100, "Neil Gaiman")
            .WithExth(101, "DC Comics")
            .WithExth(103, "A short summary.")
            .WithExth(104, "978-1-4012-8477-1")
            .WithExth(105, "Fantasy")
            .WithExth(105, "Horror")
            .WithExth(106, "1989-03-07")
            .WithExth(109, "Copyright DC Comics")
            .WithExth(113, "B000FC1PJI")
            .WithExth(524, "en")

            // Not mapped by this build: a watermark and a creator-software tag.
            .WithExth(208, "watermark-payload")
            .WithExthNumber(204, 201)
            .WithTextRecord("The book's text.");
}
