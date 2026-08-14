using System.Buffers.Binary;
using System.Text;

namespace EBookMeta.Containers;

/// <summary>One central directory record, reduced to the fields EBookMetaEditor needs.</summary>
internal sealed record ZipCentralDirectoryRecord
{
    /// <summary>The entry name as stored, decoded best-effort for diagnostics.</summary>
    internal required string Name { get; init; }

    /// <summary>The compression method code — the reason this parser exists.</summary>
    internal required ushort CompressionMethod { get; init; }

    /// <summary>General purpose bit flags.</summary>
    internal ushort Flags { get; init; }

    /// <summary>Stored size in bytes.</summary>
    internal long CompressedSize { get; init; }

    /// <summary>Uncompressed size in bytes.</summary>
    internal long UncompressedSize { get; init; }
}

/// <summary>
/// A minimal, read-only parser for a ZIP's end-of-central-directory and central
/// directory records.
/// </summary>
/// <remarks>
/// <para>
/// This exists for one reason: <c>ZipArchiveEntry</c> does not expose the
/// compression method, and preserving it per entry is a hard invariant.
/// Comparing <c>CompressedLength</c> to <c>Length</c> is not a sound substitute
/// — a deflate stream can equal or exceed its input for small or
/// already-compressed content, so that heuristic misreports exactly the case
/// rule EPUB-E040 cares most about, a <c>mimetype</c> entry that only looks
/// stored.
/// </para>
/// <para>
/// It reads structure only, never content. Decompression stays with
/// <c>ZipArchive</c>, which is well tested and not worth reimplementing.
/// The two views are paired by index rather than by name, because ZIP does not
/// guarantee unique names and malformed archives in the wild do repeat them.
/// </para>
/// </remarks>
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

    private ZipCentralDirectory(IReadOnlyList<ZipCentralDirectoryRecord> records, string? comment)
    {
        Records = records;
        ArchiveComment = comment;
    }

    /// <summary>The central directory records, in stored order.</summary>
    internal IReadOnlyList<ZipCentralDirectoryRecord> Records { get; }

    /// <summary>
    /// The archive-level comment, or <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// Read because comic archives sometimes carry a ComicBookLover JSON blob
    /// here, which is one of the three metadata conventions EBookMetaEditor reads.
    /// <para>
    /// Note for phase 2: <c>System.IO.Compression</c> cannot write an archive
    /// comment, so a CBZ carrying one cannot currently be rebuilt with it
    /// intact. That is a real conflict with "preserve the others untouched" and
    /// needs a decision before CBZ writing ships — it is recorded here rather
    /// than discovered later.
    /// </para>
    /// </remarks>
    internal string? ArchiveComment { get; }

    /// <summary>
    /// Parses the central directory of an open, seekable ZIP stream.
    /// </summary>
    /// <param name="stream">A readable, seekable stream positioned anywhere.</param>
    /// <param name="path">The file path, for error messages only.</param>
    /// <returns>The parsed directory.</returns>
    /// <exception cref="BookFormatException">
    /// The stream is truncated, or its structure does not parse. Surfaced to
    /// the user as rule GEN-F001.
    /// </exception>
    internal static ZipCentralDirectory Read(Stream stream, string? path)
    {
        Throw.IfNull(stream);

        if (!stream.CanSeek)
        {
            throw new BookFormatException("A ZIP container requires a seekable stream.", path);
        }

        if (stream.Length < EocdFixedSize)
        {
            throw new BookFormatException(
                $"File is {stream.Length} bytes, too short to be a ZIP archive.", path);
        }

        (long cdOffset, long cdSize, long entryCount, string? comment) = ReadEndOfCentralDirectory(stream, path);

        if (cdOffset < 0 || cdSize < 0 || cdOffset + cdSize > stream.Length)
        {
            throw new BookFormatException(
                $"Central directory is declared at offset {cdOffset} with size {cdSize}, " +
                $"which lies outside the {stream.Length}-byte file.", path);
        }

        if (cdSize > int.MaxValue)
        {
            throw new BookFormatException(
                $"Central directory is {cdSize} bytes, larger than this build reads.", path);
        }

        byte[] directory = new byte[(int)cdSize];
        stream.Position = cdOffset;
        stream.ReadExactly(directory);

        List<ZipCentralDirectoryRecord> records = ParseRecords(directory, entryCount, path);
        return new ZipCentralDirectory(records, comment);
    }

    private static (long CdOffset, long CdSize, long EntryCount, string? Comment) ReadEndOfCentralDirectory(
        Stream stream, string? path)
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
                "or has been truncated.", path);
        }

        ReadOnlySpan<byte> span = tail.AsSpan(eocd);
        ushort entryCount16 = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10));
        uint cdSize32 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
        uint cdOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));
        ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(20));

        string? comment = commentLength > 0 && EocdFixedSize + commentLength <= span.Length
            ? DecodeName(span.Slice(EocdFixedSize, commentLength), utf8: false)
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
        return ReadZip64(stream, eocdAbsolute, path) is var (offset, size, count)
            ? (offset, size, count, comment)
            : (cdOffset32, cdSize32, entryCount16, comment);
    }

    private static (long CdOffset, long CdSize, long EntryCount)? ReadZip64(
        Stream stream, long eocdAbsolute, string? path)
    {
        long locatorOffset = eocdAbsolute - Zip64EocdLocatorSize;
        if (locatorOffset < 0)
        {
            throw new BookFormatException(
                "Archive declares ZIP64 fields but has no ZIP64 locator.", path);
        }

        byte[] locator = new byte[Zip64EocdLocatorSize];
        stream.Position = locatorOffset;
        stream.ReadExactly(locator);

        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64EocdLocatorSignature)
        {
            throw new BookFormatException(
                "Archive declares ZIP64 fields but the ZIP64 locator signature is wrong.", path);
        }

        long zip64EocdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
        if (zip64EocdOffset < 0 || zip64EocdOffset + 56 > stream.Length)
        {
            throw new BookFormatException(
                $"ZIP64 end-of-central-directory offset {zip64EocdOffset} lies outside the file.", path);
        }

        byte[] zip64 = new byte[56];
        stream.Position = zip64EocdOffset;
        stream.ReadExactly(zip64);

        if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EocdSignature)
        {
            throw new BookFormatException(
                "ZIP64 end-of-central-directory signature is wrong.", path);
        }

        long entryCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
        long cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
        long cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
        return (cdOffset, cdSize, entryCount);
    }

    /// <summary>
    /// Finds the end-of-central-directory record, scanning backwards.
    /// </summary>
    /// <remarks>
    /// Backwards because the signature can legitimately occur inside the
    /// archive comment that follows it, and the real record is the last one.
    /// The comment length is then cross-checked against the remaining bytes,
    /// which rejects a coincidental match inside a comment.
    /// </remarks>
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

    private static List<ZipCentralDirectoryRecord> ParseRecords(
        ReadOnlySpan<byte> directory, long expectedCount, string? path)
    {
        var records = new List<ZipCentralDirectoryRecord>(
            expectedCount is > 0 and < 4096 ? (int)expectedCount : 16);

        int offset = 0;
        while (offset + CentralFileHeaderFixedSize <= directory.Length)
        {
            ReadOnlySpan<byte> header = directory.Slice(offset);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralFileHeaderSignature)
            {
                break;
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8));
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(10));
            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20));
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24));
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28));
            ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(30));
            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(32));

            int recordLength = CentralFileHeaderFixedSize + nameLength + extraLength + commentLength;
            if (offset + recordLength > directory.Length)
            {
                throw new BookFormatException(
                    $"Central directory record at offset {offset} runs past the end of the directory.",
                    path);
            }

            string name = DecodeName(
                header.Slice(CentralFileHeaderFixedSize, nameLength),
                utf8: (flags & 0x0800) != 0);

            long realCompressed = compressedSize;
            long realUncompressed = uncompressedSize;

            if (compressedSize == Zip64Marker32 || uncompressedSize == Zip64Marker32)
            {
                ReadZip64Extra(
                    header.Slice(CentralFileHeaderFixedSize + nameLength, extraLength),
                    ref realUncompressed,
                    ref realCompressed,
                    uncompressedSize,
                    compressedSize);
            }

            records.Add(new ZipCentralDirectoryRecord
            {
                Name = name,
                CompressionMethod = method,
                Flags = flags,
                CompressedSize = realCompressed,
                UncompressedSize = realUncompressed,
            });

            offset += recordLength;
        }

        return records;
    }

    /// <summary>
    /// Reads the ZIP64 extended information extra field (header id 0x0001).
    /// </summary>
    /// <remarks>
    /// The field contains only those values that were saturated in the base
    /// record, in a fixed order: uncompressed size, compressed size, local
    /// header offset, disk number. So the fields present cannot be determined
    /// from the field's length alone — each must be consumed conditionally, in
    /// order, which is why this reads the way it does.
    /// </remarks>
    private static void ReadZip64Extra(
        ReadOnlySpan<byte> extra,
        ref long uncompressed,
        ref long compressed,
        uint rawUncompressed,
        uint rawCompressed)
    {
        int offset = 0;
        while (offset + 4 <= extra.Length)
        {
            ushort headerId = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(offset));
            ushort dataSize = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice((offset + 2)));
            int dataStart = offset + 4;

            if (dataStart + dataSize > extra.Length)
            {
                return;
            }

            if (headerId == 0x0001)
            {
                ReadOnlySpan<byte> data = extra.Slice(dataStart, dataSize);
                int cursor = 0;

                if (rawUncompressed == Zip64Marker32 && cursor + 8 <= data.Length)
                {
                    uncompressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor));
                    cursor += 8;
                }

                if (rawCompressed == Zip64Marker32 && cursor + 8 <= data.Length)
                {
                    compressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor));
                }

                return;
            }

            offset = dataStart + dataSize;
        }
    }

    /// <summary>
    /// Decodes an entry name. Bit 11 of the general purpose flags means UTF-8;
    /// otherwise the spec says CP437, which is unavailable in .NET without an
    /// extra encoding provider. Latin-1 agrees with CP437 across ASCII, which
    /// covers effectively every ebook and comic entry name, and never throws on
    /// the bytes that differ.
    /// </summary>
    /// <remarks>
    /// Names parsed here are used for diagnostics and cross-checking only. The
    /// authoritative name for each entry comes from <c>ZipArchive</c>, so a
    /// disagreement in this fallback cannot corrupt a rebuild.
    /// </remarks>
    private static string DecodeName(ReadOnlySpan<byte> bytes, bool utf8) =>
        utf8 ? Encoding.UTF8.GetString(bytes) : Encodings.Latin1.GetString(bytes);
}
