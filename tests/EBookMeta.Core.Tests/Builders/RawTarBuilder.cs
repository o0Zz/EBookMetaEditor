using System.Text;

namespace EBookMeta.Tests.Builders;

/// <summary>Assembles TAR archives byte by byte, the way <c>tar</c> would.</summary>
internal sealed class RawTarBuilder
{
    private const int BlockSize = 512;

    private readonly List<Entry> _entries = [];
    private int _blockingFactor = 20;

    private sealed record Entry(
        string Name,
        byte[] Content,
        int Mode,
        int Uid,
        int Gid,
        string UserName,
        string GroupName,
        DateTimeOffset Modified,
        bool GnuLongName);

    /// <summary>
    /// The owner details GNU tar records for a file created by an ordinary user,
    /// none of which this build models.
    /// </summary>
    private const int DefaultMode = 0x1A0;      // 0640
    private const int DefaultUid = 1000;
    private const int DefaultGid = 1000;
    private const string DefaultUserName = "reader";
    private const string DefaultGroupName = "comics";

    private static readonly DateTimeOffset FixedTimestamp =
        new(2013, 6, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Adds a file entry.</summary>
    internal RawTarBuilder WithFile(
        string name,
        byte[] content,
        int mode = DefaultMode,
        int uid = DefaultUid,
        int gid = DefaultGid,
        string userName = DefaultUserName,
        string groupName = DefaultGroupName)
    {
        _entries.Add(new Entry(
            name, content, mode, uid, gid, userName, groupName, FixedTimestamp, GnuLongName: false));

        return this;
    }

    /// <summary>Adds a text file entry.</summary>
    internal RawTarBuilder WithFile(string name, string content) =>
        WithFile(name, Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// Adds a file whose name is carried in a GNU <c>L</c> long-name block rather
    /// than in the header, which is how GNU tar stores a name over 100 bytes.
    /// </summary>
    internal RawTarBuilder WithGnuLongNamedFile(string name, byte[] content)
    {
        _entries.Add(new Entry(
            name,
            content,
            DefaultMode,
            DefaultUid,
            DefaultGid,
            DefaultUserName,
            DefaultGroupName,
            FixedTimestamp,
            GnuLongName: true));

        return this;
    }

    /// <summary>
    /// Sets how many blocks the archive is padded to. GNU tar's default is 20,
    /// meaning a ten-kilobyte tail that a naive writer replaces with 1024 bytes.
    /// </summary>
    internal RawTarBuilder WithBlockingFactor(int blocks)
    {
        _blockingFactor = blocks;
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

        foreach (Entry entry in _entries)
        {
            if (entry.GnuLongName)
            {
                byte[] name = Encoding.UTF8.GetBytes(entry.Name + "\0");

                // The 'L' block declares the length of the name, and the name
                // follows as ordinary padded data. The header after it carries a
                // truncated name that readers are expected to ignore.
                output.Write(Header(entry with { Name = "././@LongLink" }, name.Length, 'L'), 0, BlockSize);
                WritePadded(output, name);

                // The header that follows carries the name truncated to the field,
                // which is what GNU tar writes and what readers are expected to
                // ignore in favour of the block above.
                output.Write(
                    Header(entry with { Name = Truncate(entry.Name, 100) }, entry.Content.Length, '0'),
                    0,
                    BlockSize);

                WritePadded(output, entry.Content);
                continue;
            }

            output.Write(Header(entry, entry.Content.Length, '0'), 0, BlockSize);
            WritePadded(output, entry.Content);
        }

        // Two zero blocks end the archive, then padding out to the blocking
        // factor. Both are part of what a faithful rebuild reproduces.
        output.Write(new byte[BlockSize * 2], 0, BlockSize * 2);

        long blocks = output.Length / BlockSize;
        long padding = (_blockingFactor - (blocks % _blockingFactor)) % _blockingFactor;

        if (padding > 0)
        {
            output.Write(new byte[padding * BlockSize], 0, (int)(padding * BlockSize));
        }

        return output.ToArray();
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value.Substring(0, length);

    private static void WritePadded(Stream output, byte[] content)
    {
        output.Write(content, 0, content.Length);

        int overhang = content.Length % BlockSize;
        if (overhang != 0)
        {
            output.Write(new byte[BlockSize - overhang], 0, BlockSize - overhang);
        }
    }

    private static byte[] Header(Entry entry, int size, char typeFlag)
    {
        byte[] block = new byte[BlockSize];

        // A name over 100 bytes is split at a slash: the tail goes in the name
        // field and the head in the ustar prefix at offset 345.
        byte[] name = Encoding.UTF8.GetBytes(entry.Name);

        if (name.Length <= 100)
        {
            Array.Copy(name, 0, block, 0, name.Length);
        }
        else
        {
            int split = entry.Name.LastIndexOf('/', Math.Min(entry.Name.Length - 1, 155));
            byte[] prefix = Encoding.UTF8.GetBytes(entry.Name.Substring(0, split));
            byte[] tail = Encoding.UTF8.GetBytes(entry.Name.Substring(split + 1));

            Array.Copy(tail, 0, block, 0, tail.Length);
            Array.Copy(prefix, 0, block, 345, prefix.Length);
        }

        Octal(block, 100, 7, entry.Mode);
        Octal(block, 108, 7, entry.Uid);
        Octal(block, 116, 7, entry.Gid);
        Octal(block, 124, 11, size);
        Octal(block, 136, 11, entry.Modified.ToUnixTimeSeconds());

        block[156] = (byte)typeFlag;

        Ascii(block, 257, "ustar");
        block[263] = (byte)'0';
        block[264] = (byte)'0';
        Ascii(block, 265, entry.UserName);
        Ascii(block, 297, entry.GroupName);
        Octal(block, 329, 7, 0);
        Octal(block, 337, 7, 0);

        // Computed last, over a block whose checksum field reads as spaces.
        int sum = 0;
        for (int i = 0; i < BlockSize; i++)
        {
            sum += i >= 148 && i < 156 ? ' ' : block[i];
        }

        Octal(block, 148, 6, sum);
        block[154] = 0;
        block[155] = (byte)' ';

        return block;
    }

    private static void Octal(byte[] block, int offset, int width, long value)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            block[offset + i] = (byte)('0' + (int)(value & 7));
            value >>= 3;
        }
    }

    private static void Ascii(byte[] block, int offset, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Array.Copy(bytes, 0, block, offset, bytes.Length);
    }
}
