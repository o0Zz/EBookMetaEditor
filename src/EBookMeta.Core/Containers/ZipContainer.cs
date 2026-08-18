using SharpCompress.Common;
using SharpCompress.Writers.Zip;
using SharpCompress.Writers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace EBookMeta.Containers;

/// <summary>A ZIP container — the storage behind EPUB, CBZ and FB2.ZIP.</summary>
public sealed class ZipContainer : IContainer
{
    private readonly ZipArchive _archive;
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly ContainerEntry[] _entries;
    private bool _disposed;

    private ZipContainer(
        ZipArchive archive,
        Stream stream,
        bool ownsStream,
        ContainerEntry[] entries,
        string? path,
        string? archiveComment)
    {
        _archive = archive;
        _stream = stream;
        _ownsStream = ownsStream;
        _entries = entries;
        Path = path;
        ArchiveComment = archiveComment;
    }

    /// <summary>How <see cref="BookContainers"/> knows this container.</summary>
    public static ContainerFormat Format { get; } = new()
    {
        Kind = ContainerKind.Zip,
        Open = Open,
        Signatures =
        [
            ContainerSignature.Text("PK\u0003\u0004"),
            ContainerSignature.Text("PK\u0005\u0006"),
            ContainerSignature.Text("PK\u0007\u0008"),
        ],
    };

    /// <inheritdoc />
    public bool IsWritable => true;

    /// <inheritdoc />
    public IReadOnlyList<ContainerEntry> Entries => _entries;

    /// <summary>The file this container was opened from, when it came from one.</summary>
    public string? Path { get; }

    /// <inheritdoc />
    public string? ArchiveComment { get; }

    /// <summary>Opens a ZIP container from a file path.</summary>
    /// <param name="path">The archive to open.</param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    /// <exception cref="BookFormatException">The file is not a readable ZIP.</exception>
    public static ZipContainer Open(string path)
    {
        Throw.IfNullOrEmpty(path);

        return BookContainers.OpenFile(path, stream => Open(stream, path, leaveOpen: false));
    }

    /// <summary>Opens a ZIP container over an existing seekable stream.</summary>
    /// <param name="stream">A readable, seekable stream over the archive.</param>
    /// <param name="path">The originating path, for diagnostics. May be null.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave <paramref name="stream"/> open when the
    /// container is disposed.
    /// </param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookFormatException">The stream is not a readable ZIP.</exception>
    public static ZipContainer Open(Stream stream, string? path = null, bool leaveOpen = false)
    {
        Throw.IfNull(stream);

        ZipCentralDirectory directory = ZipCentralDirectory.Read(stream);

        stream.Position = 0;
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new BookFormatException($"'{path}' is not a readable ZIP archive.", ex);
        }

        try
        {
            // Disagreeing with ZipArchive on entry count means we do not understand the
            // layout, and a rebuild could pair the wrong method onto the wrong entry.
            if (directory.CompressionMethods.Count != archive.Entries.Count)
            {
                throw new BookFormatException(
                    $"Central directory lists {directory.CompressionMethods.Count} entries but the " +
                    $"archive reads {archive.Entries.Count}. The file is inconsistent and will not " +
                    "be edited.");
            }

            var entries = new ContainerEntry[archive.Entries.Count];

            for (int i = 0; i < entries.Length; i++)
            {
                ZipArchiveEntry zipEntry = archive.Entries[i];

                entries[i] = new ContainerEntry
                {
                    // Name comes from ZipArchive, not from our parser, so the
                    // two views cannot diverge on entry-name encoding.
                    Name = zipEntry.FullName,
                    Index = i,
                    Length = zipEntry.Length,
                    CompressionMethod = directory.CompressionMethods[i],
                    LastModified = zipEntry.LastWriteTime,
                    IsDirectory = zipEntry.FullName.EndsWith('/') && zipEntry.Length == 0,
                };
            }

            return new ZipContainer(
                archive, stream, ownsStream: !leaveOpen, entries, path,
                directory.ArchiveComment);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
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

        try
        {
            return _archive.Entries[entry.Index].Open();
        }
        catch (InvalidDataException ex)
        {
            throw new BookFormatException(
                $"Entry '{entry.Name}' is corrupt or uses an unsupported compression method " +
                $"({ZipCompressionMethods.ToName(entry.CompressionMethod)}).",
                ex);
        }
    }

    /// <inheritdoc />
    public void Rebuild(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);
        Throw.IfDisposed(_disposed, this);

        Create(entries, targetPath);
    }

    /// <summary>Writes a ZIP containing the given entries, in the order given.</summary>
    /// <param name="entries">The entries to write, in order.</param>
    /// <param name="targetPath">The file to create.</param>
    /// <exception cref="BookIoException">The target could not be written.</exception>
    public static void Create(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);

        try
        {
            using var output = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            // Deflate is only the default; every entry overrides it below with
            // the method it was read with.
            using var writer = new ZipWriter(output, new ZipWriterOptions(CompressionType.Deflate)
            {
                LeaveStreamOpen = true,
            });

            foreach (PendingEntry pending in entries)
            {
                var options = new ZipWriterEntryOptions
                {
                    CompressionType = pending.CompressionMethod == ZipCompressionMethods.Stored
                        ? CompressionType.None
                        : CompressionType.Deflate,
                };

                if (pending.LastModified != default)
                {
                    // ZIP timestamps are DOS format and cannot predate 1980.
                    // Clamp rather than throw: an odd timestamp on a source file
                    // is not a reason to refuse the user's metadata edit.
                    DateTimeOffset stamp = pending.LastModified < ZipMinimumTimestamp
                        ? ZipMinimumTimestamp
                        : pending.LastModified;

                    options.ModificationDateTime = stamp.UtcDateTime;
                }

                // Entries are written in the order supplied, which is what puts
                // mimetype first in an EPUB.
                using Stream source = pending.OpenContent();
                writer.Write(pending.Name, source, options);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BookIoException($"Could not write '{targetPath}'.", ex);
        }
    }

    private static readonly DateTimeOffset ZipMinimumTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archive.Dispose();

        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}

/// <summary>
/// A minimal, read-only parser for a ZIP's end-of-central-directory and central
/// directory records.
/// </summary>
internal sealed class ZipCentralDirectory
{
    private const uint EocdSignature = 0x06054B50;
    private const uint Zip64EocdLocatorSignature = 0x07064B50;
    private const uint Zip64EocdSignature = 0x06064B50;
    private const uint CentralFileHeaderSignature = 0x02014B50;

    private const int EocdFixedSize = 22;
    private const int Zip64EocdLocatorSize = 20;
    private const int CentralFileHeaderFixedSize = 46;
    private const int MaxCommentSize = 0xFFFF;

    /// <summary>Marker value meaning "the real value is in the ZIP64 extra field".</summary>
    private const uint Zip64Marker32 = 0xFFFFFFFF;
    private const ushort Zip64Marker16 = 0xFFFF;

    private ZipCentralDirectory(IReadOnlyList<ushort> compressionMethods, string? comment)
    {
        CompressionMethods = compressionMethods;
        ArchiveComment = comment;
    }

    /// <summary>
    /// Each entry's compression method code, in stored order — the reason this
    /// parser exists, since <c>ZipArchiveEntry</c> does not expose it.
    /// </summary>
    internal IReadOnlyList<ushort> CompressionMethods { get; }

    /// <summary>
    /// The archive-level comment, or <see langword="null"/> when there is none.
    /// </summary>
    internal string? ArchiveComment { get; }

    /// <summary>Parses the central directory of an open, seekable ZIP stream.</summary>
    /// <param name="stream">A readable, seekable stream positioned anywhere.</param>
    /// <returns>The parsed directory.</returns>
    /// <exception cref="BookFormatException">
    /// The stream is truncated, or its structure does not parse. Surfaced to
    /// the user as rule GEN-F001.
    /// </exception>
    internal static ZipCentralDirectory Read(Stream stream)
    {
        Throw.IfNull(stream);

        if (!stream.CanSeek)
        {
            throw new BookFormatException("A ZIP container requires a seekable stream.");
        }

        if (stream.Length < EocdFixedSize)
        {
            throw new BookFormatException(
                $"File is {stream.Length} bytes, too short to be a ZIP archive.");
        }

        (long cdOffset, long cdSize, long entryCount, string? comment) = ReadEndOfCentralDirectory(stream);

        if (cdOffset < 0 || cdSize < 0 || cdOffset + cdSize > stream.Length)
        {
            throw new BookFormatException(
                $"Central directory is declared at offset {cdOffset} with size {cdSize}, " +
                $"which lies outside the {stream.Length}-byte file.");
        }

        if (cdSize > int.MaxValue)
        {
            throw new BookFormatException(
                $"Central directory is {cdSize} bytes, larger than this build reads.");
        }

        byte[] directory = new byte[(int)cdSize];
        stream.Position = cdOffset;
        stream.ReadExactly(directory);

        return new ZipCentralDirectory(ParseRecords(directory, entryCount), comment);
    }

    private static (long CdOffset, long CdSize, long EntryCount, string? Comment) ReadEndOfCentralDirectory(
        Stream stream)
    {
        int searchLength = (int)Math.Min(stream.Length, EocdFixedSize + MaxCommentSize);
        byte[] tail = new byte[searchLength];
        stream.Position = stream.Length - searchLength;
        stream.ReadExactly(tail);

        int eocd = FindEocd(tail);
        if (eocd < 0)
        {
            throw new BookFormatException(
                "No end-of-central-directory record found. The file is not a ZIP archive, " +
                "or has been truncated.");
        }

        ReadOnlySpan<byte> span = tail.AsSpan(eocd);
        ushort entryCount16 = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10));
        uint cdSize32 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
        uint cdOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));
        ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(20));

        string? comment = commentLength > 0 && EocdFixedSize + commentLength <= span.Length
            ? DecodeComment(span.Slice(EocdFixedSize, commentLength))
            : null;

        // Any of these three being saturated means the true values live in a
        // ZIP64 record. A 500-entry EPUB never triggers this, but a comic
        // archive can exceed 4 GB and must not be silently misread.
        bool needsZip64 =
            entryCount16 == Zip64Marker16 ||
            cdSize32 == Zip64Marker32 ||
            cdOffset32 == Zip64Marker32;

        if (!needsZip64)
        {
            return (cdOffset32, cdSize32, entryCount16, comment);
        }

        long eocdAbsolute = stream.Length - searchLength + eocd;
        return ReadZip64(stream, eocdAbsolute) is var (offset, size, count)
            ? (offset, size, count, comment)
            : (cdOffset32, cdSize32, entryCount16, comment);
    }

    private static (long CdOffset, long CdSize, long EntryCount)? ReadZip64(
        Stream stream, long eocdAbsolute)
    {
        long locatorOffset = eocdAbsolute - Zip64EocdLocatorSize;
        if (locatorOffset < 0)
        {
            throw new BookFormatException(
                "Archive declares ZIP64 fields but has no ZIP64 locator.");
        }

        byte[] locator = new byte[Zip64EocdLocatorSize];
        stream.Position = locatorOffset;
        stream.ReadExactly(locator);

        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64EocdLocatorSignature)
        {
            throw new BookFormatException(
                "Archive declares ZIP64 fields but the ZIP64 locator signature is wrong.");
        }

        long zip64EocdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
        if (zip64EocdOffset < 0 || zip64EocdOffset + 56 > stream.Length)
        {
            throw new BookFormatException(
                $"ZIP64 end-of-central-directory offset {zip64EocdOffset} lies outside the file.");
        }

        byte[] zip64 = new byte[56];
        stream.Position = zip64EocdOffset;
        stream.ReadExactly(zip64);

        if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EocdSignature)
        {
            throw new BookFormatException(
                "ZIP64 end-of-central-directory signature is wrong.");
        }

        long entryCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
        long cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
        long cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
        return (cdOffset, cdSize, entryCount);
    }

    /// <summary>Finds the end-of-central-directory record, scanning backwards.</summary>
    private static int FindEocd(ReadOnlySpan<byte> tail)
    {
        for (int i = tail.Length - EocdFixedSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.Slice(i)) != EocdSignature)
            {
                continue;
            }

            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.Slice((i + 20)));
            if (i + EocdFixedSize + commentLength == tail.Length)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<ushort> ParseRecords(
        ReadOnlySpan<byte> directory, long expectedCount)
    {
        var methods = new List<ushort>(
            expectedCount is > 0 and < 4096 ? (int)expectedCount : 16);

        int offset = 0;
        while (offset + CentralFileHeaderFixedSize <= directory.Length)
        {
            ReadOnlySpan<byte> header = directory.Slice(offset);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralFileHeaderSignature)
            {
                break;
            }

            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(10));
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28));
            ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(30));
            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(32));

            int recordLength = CentralFileHeaderFixedSize + nameLength + extraLength + commentLength;
            if (offset + recordLength > directory.Length)
            {
                throw new BookFormatException(
                    $"Central directory record at offset {offset} runs past the end of the directory.");
            }

            // Names and sizes deliberately not read: ZipArchive supplies the names, and
            // ZIP64 saturates the sizes and puts the real values in an extra field.
            methods.Add(method);

            offset += recordLength;
        }

        return methods;
    }

    /// <summary>
    /// Decodes the archive comment. The spec says CP437, which is unavailable in
    /// .NET without an extra encoding provider; Latin-1 agrees with it across ASCII
    /// and never throws on the bytes that differ.
    /// </summary>
    private static string DecodeComment(ReadOnlySpan<byte> bytes) =>
        Encodings.Latin1.GetString(bytes);
}
