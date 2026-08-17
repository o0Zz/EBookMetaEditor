using System.Buffers.Binary;
using System.Text;

namespace EBookMeta.Containers;

/// <summary>A PalmDB database — the record container behind MOBI, PRC, AZW and AZW3.</summary>
public sealed class PalmDbContainer : IContainer
{
    /// <summary>Bytes of fixed header before the record table.</summary>
    private const int HeaderLength = 78;

    /// <summary>Bytes per entry in the record table.</summary>
    private const int RecordEntryLength = 8;

    /// <summary>Where the record count sits in the header.</summary>
    private const int RecordCountOffset = 76;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly ContainerEntry[] _entries;
    private readonly long[] _offsets;
    private readonly byte[] _header;
    private readonly RecordInfo[] _records;
    private bool _disposed;

    /// <summary>What the record table says about a record, other than where it is.</summary>
    /// <param name="Attributes">The attribute byte, meaningless here but the user's.</param>
    /// <param name="UniqueId">The record's 24-bit unique id.</param>
    private readonly record struct RecordInfo(byte Attributes, int UniqueId);

    private PalmDbContainer(
        Stream stream,
        bool ownsStream,
        string? path,
        byte[] header,
        ContainerEntry[] entries,
        long[] offsets,
        RecordInfo[] records)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _header = header;
        _entries = entries;
        _offsets = offsets;
        _records = records;
        Path = path;
    }

    /// <inheritdoc />
    public bool IsWritable => true;

    /// <inheritdoc />
    public IReadOnlyList<ContainerEntry> Entries => _entries;

    /// <summary>The file this container was opened from, when it came from one.</summary>
    public string? Path { get; }

    /// <inheritdoc />
    public string? ArchiveComment => null;

    /// <summary>Opens a PalmDB file.</summary>
    /// <param name="path">The file to open.</param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    /// <exception cref="BookFormatException">The file is not a readable PalmDB.</exception>
    public static PalmDbContainer Open(string path)
    {
        Throw.IfNullOrEmpty(path);

        return BookContainers.OpenFile(path, stream => Open(stream, path, leaveOpen: false));
    }

    /// <summary>Opens a PalmDB over an existing seekable stream.</summary>
    /// <param name="stream">A readable, seekable stream over the database.</param>
    /// <param name="path">The originating path, for diagnostics. May be null.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave <paramref name="stream"/> open when the
    /// container is disposed.
    /// </param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookFormatException">The stream is not a readable PalmDB.</exception>
    public static PalmDbContainer Open(Stream stream, string? path = null, bool leaveOpen = false)
    {
        Throw.IfNull(stream);

        if (!stream.CanSeek)
        {
            throw new BookFormatException(
                "A PalmDB database must be read from a seekable stream.");
        }

        long length = stream.Length;

        if (length < HeaderLength)
        {
            throw new BookFormatException(
                "This file is too short to be a PalmDB database.");
        }

        byte[] header = new byte[HeaderLength];
        stream.Position = 0;
        ReadExactly(stream, header, header.Length);

        int count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(RecordCountOffset));

        if (count == 0)
        {
            throw new BookFormatException("This PalmDB database holds no records.");
        }

        long tableLength = (long)count * RecordEntryLength;

        if (HeaderLength + tableLength > length)
        {
            throw new BookFormatException(
                $"The record table claims {count} records, which does not fit in the file.");
        }

        byte[] table = new byte[tableLength];
        ReadExactly(stream, table, table.Length);

        var offsets = new long[count];
        var records = new RecordInfo[count];

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = table.AsSpan(i * RecordEntryLength, RecordEntryLength);

            offsets[i] = BinaryPrimitives.ReadUInt32BigEndian(entry);
            records[i] = new RecordInfo(
                entry[4],

                // A 24-bit big-endian integer, which no BinaryPrimitives overload
                // covers.
                (entry[5] << 16) | (entry[6] << 8) | entry[7]);

            if (offsets[i] > length)
            {
                throw new BookFormatException(
                    $"Record {i} starts at {offsets[i]}, past the end of the file.");
            }

            if (i > 0 && offsets[i] < offsets[i - 1])
            {
                throw new BookFormatException(
                    $"Record {i} starts before record {i - 1}; the record table is corrupt.");
            }
        }

        var entries = new ContainerEntry[count];

        for (int i = 0; i < count; i++)
        {
            // A record's length is only implied: it runs to the next one, and the
            // last runs to the end of the file.
            long end = i + 1 < count ? offsets[i + 1] : length;

            entries[i] = new ContainerEntry
            {
                Name = "record" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Index = i,
                Length = Math.Max(0, end - offsets[i]),
                CompressionMethod = ZipCompressionMethods.Stored,
            };
        }

        return new PalmDbContainer(
            stream,
            ownsStream: !leaveOpen,
            path,
            header,
            entries,
            offsets,
            records);
    }

    /// <inheritdoc />
    public Stream OpenRead(ContainerEntry entry)
    {
        Throw.IfNull(entry);
        Throw.IfDisposed(_disposed, this);

        if ((uint)entry.Index >= (uint)_entries.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entry), entry.Index, "Entry index is outside this container.");
        }

        long start = _offsets[entry.Index];
        long length = _entries[entry.Index].Length;

        if (Path is null)
        {
            return new SectionStream(_stream, start, length, ownsStream: false);
        }

        FileStream own = BookContainers.ReopenForEntry(
            Path, $"Record {entry.Index}");

        return new SectionStream(own, start, length, ownsStream: true);
    }

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// The record count differs from the source. A PalmDB's record indexes are
    /// referenced from inside the records themselves — the KF8 boundary, the first
    /// image index — so adding or removing one would invalidate pointers this
    /// build does not know how to find.
    /// </exception>
    public void Rebuild(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);
        Throw.IfDisposed(_disposed, this);

        List<PendingEntry> pending = [.. entries];

        if (pending.Count != _entries.Length)
        {
            throw new BookFormatException(
                $"A PalmDB rebuild must write the same {_entries.Length} records it read, "
                + $"but {pending.Count} were supplied. Record numbers are referenced from "
                + "inside the file, so they cannot be added or removed here.");
        }

        try
        {
            using var output = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            // The gap between the record table and the first record is part of the
            // original layout — traditionally two bytes of padding — and is copied
            // rather than assumed, so record 0 keeps the offset it had.
            long firstRecord = _offsets[0];
            long tableEnd = HeaderLength + ((long)_entries.Length * RecordEntryLength);

            if (firstRecord < tableEnd)
            {
                throw new BookFormatException(
                    "The first record overlaps the record table; the file is corrupt.");
            }

            byte[] gap = new byte[firstRecord - tableEnd];
            _stream.Position = tableEnd;
            ReadExactly(_stream, gap, gap.Length);

            // Sizes have to be known before the table can be written, so the record
            // bodies are materialised first. Only record 0 is ever rewritten; the
            // rest are windows onto the source file.
            var bodies = new byte[pending.Count][];

            for (int i = 0; i < pending.Count; i++)
            {
                using Stream content = pending[i].OpenContent();
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);
                bodies[i] = buffer.ToArray();
            }

            byte[] header = (byte[])_header.Clone();
            output.Write(header, 0, header.Length);

            byte[] entry = new byte[RecordEntryLength];
            long offset = firstRecord;

            for (int i = 0; i < bodies.Length; i++)
            {
                if (offset > uint.MaxValue)
                {
                    throw new BookFormatException(
                        "The rebuilt database would exceed the 4 GB a PalmDB record "
                        + "offset can express.");
                }

                BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)offset);
                entry[4] = _records[i].Attributes;
                entry[5] = (byte)(_records[i].UniqueId >> 16);
                entry[6] = (byte)(_records[i].UniqueId >> 8);
                entry[7] = (byte)_records[i].UniqueId;

                output.Write(entry, 0, entry.Length);
                offset += bodies[i].Length;
            }

            output.Write(gap, 0, gap.Length);

            foreach (byte[] body in bodies)
            {
                output.Write(body, 0, body.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BookIoException($"Could not write '{targetPath}'.", ex);
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int total = 0;

        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);

            if (read <= 0)
            {
                throw new BookFormatException(
                    "This PalmDB database is truncated.");
            }

            total += read;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
