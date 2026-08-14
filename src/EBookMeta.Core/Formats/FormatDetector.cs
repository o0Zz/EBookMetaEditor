using System.Buffers.Binary;
using System.Text;

namespace EBookMeta.Formats;

/// <summary>What a file turned out to be, and whether its name agreed.</summary>
public sealed record DetectedFormat
{
    /// <summary>The format the content indicates.</summary>
    public required FormatId Format { get; init; }

    /// <summary>The container the content indicates.</summary>
    public required ContainerKind Container { get; init; }

    /// <summary>
    /// The format the file extension claims, or <see cref="FormatId.Unknown"/>
    /// for an unrecognised extension.
    /// </summary>
    public FormatId ClaimedByExtension { get; init; }

    /// <summary>
    /// A short note on how the decision was reached, for display in the
    /// validation panel — "RAR 5 archive", "first entry is ComicInfo.xml".
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Whether the extension is consistent with the content.
    /// </summary>
    public bool ExtensionAgrees =>
        ClaimedByExtension == FormatId.Unknown ||
        FormatIds.IsAcceptableSubstitute(ClaimedByExtension, Format);

    /// <summary>Returns a short description, for diagnostics.</summary>
    public override string ToString() =>
        Detail is null
            ? FormatIds.ToDisplayName(Format)
            : $"{FormatIds.ToDisplayName(Format)} ({Detail})";
}

/// <summary>
/// Decides what a file actually is, by content.
/// </summary>
public static class FormatDetector
{
    /// <summary>How many bytes the magic-number pass reads.</summary>
    public const int HeaderLength = 8192;

    private const uint ZipLocalFileHeaderSignature = 0x04034B50;
    private const uint ZipEmptyArchiveSignature = 0x06054B50;
    private const uint ZipSpannedSignature = 0x08074B50;

    private static ReadOnlySpan<byte> Rar4Magic => "Rar!\x1a\x07\x00"u8;
    private static ReadOnlySpan<byte> Rar5Magic => "Rar!\x1a\x07\x01\x00"u8;
    private static ReadOnlySpan<byte> SevenZipMagic => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static ReadOnlySpan<byte> PdfMagic => "%PDF-"u8;
    private static ReadOnlySpan<byte> TarMagic => "ustar"u8;
    private static ReadOnlySpan<byte> MobiPdbType => "BOOKMOBI"u8;
    private static ReadOnlySpan<byte> TextReadPdbType => "TEXtREAd"u8;
    private static ReadOnlySpan<byte> EpubMediaType => "application/epub+zip"u8;
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".jxl", ".tif", ".tiff"];

    /// <summary>Detects a file by path.</summary>
    /// <param name="path">The file to identify.</param>
    /// <returns>What the file is, and whether its extension agreed.</returns>
    /// <exception cref="BookIoException">The file could not be read.</exception>
    public static DetectedFormat Detect(string path)
    {
        Throw.IfNullOrEmpty(path);

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: HeaderLength,
                FileOptions.SequentialScan);

            return Detect(stream, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new BookIoException($"Could not read '{path}'.", path, ex);
        }
    }

    /// <summary>Detects an open stream.</summary>
    /// <param name="stream">A readable stream, positioned at the start.</param>
    /// <param name="fileName">
    /// The file name, used only to report what the extension claimed. May be
    /// <see langword="null"/>.
    /// </param>
    /// <returns>What the content is, and whether the extension agreed.</returns>
    public static DetectedFormat Detect(Stream stream, string? fileName = null)
    {
        Throw.IfNull(stream);

        long origin = stream.CanSeek ? stream.Position : 0;
        byte[] buffer = new byte[HeaderLength];
        int read = stream.ReadAtLeast(buffer, HeaderLength, throwOnEndOfStream: false);
        ReadOnlySpan<byte> head = buffer.AsSpan(0, read);

        FormatId claimed = fileName is null ? FormatId.Unknown : FormatIds.FromExtension(fileName);

        (FormatId format, ContainerKind container, string? detail) = Identify(head, stream, origin);

        var result = new DetectedFormat
        {
            Format = format,
            Container = container,
            ClaimedByExtension = claimed,
            Detail = detail,
        };

        Log.Debug(
            $"Detected {FormatIds.ToDisplayName(result.Format)}"
            + (detail is null ? string.Empty : $" ({detail})")
            + $" from {read} header bytes of '{fileName ?? "(stream)"}'.");

        // The disagreement is not logged here. Detection is a question, not a
        // verdict, and the same file is often sniffed more than once; reporting it
        // from one place — Book.Load, as rule GEN-W002 — is what keeps it to one
        // line in the log with a rule ID attached.
        return result;
    }

    private static (FormatId, ContainerKind, string?) Identify(
        ReadOnlySpan<byte> head, Stream stream, long origin)
    {
        if (head.Length >= 4)
        {
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(head);

            if (signature is ZipLocalFileHeaderSignature or ZipEmptyArchiveSignature or ZipSpannedSignature)
            {
                return IdentifyZip(head, stream, origin);
            }
        }

        if (head.StartsWith(Rar5Magic))
        {
            return (FormatId.Cbr, ContainerKind.Rar, "RAR 5 archive");
        }

        if (head.StartsWith(Rar4Magic))
        {
            return (FormatId.Cbr, ContainerKind.Rar, "RAR 4 archive");
        }

        if (head.StartsWith(SevenZipMagic))
        {
            return (FormatId.Cb7, ContainerKind.SevenZip, "7z archive");
        }

        if (head.StartsWith(PdfMagic))
        {
            return (FormatId.Pdf, ContainerKind.Raw, "PDF header");
        }

        // PalmDB stores an 8-byte type+creator pair at offset 60.
        if (head.Length >= 68)
        {
            ReadOnlySpan<byte> pdbType = head.Slice(60, 8);

            if (pdbType.SequenceEqual(MobiPdbType))
            {
                return (FormatId.Mobi, ContainerKind.PalmDb, "PalmDB BOOKMOBI");
            }

            if (pdbType.SequenceEqual(TextReadPdbType))
            {
                return (FormatId.Mobi, ContainerKind.PalmDb, "PalmDB TEXtREAd");
            }
        }

        // TAR's magic sits inside the first header block, not at offset 0.
        if (head.Length >= 262 && head.Slice(257, 5).SequenceEqual(TarMagic))
        {
            return (FormatId.Cbt, ContainerKind.Tar, "TAR archive");
        }

        if (LooksLikeFictionBook(head))
        {
            return (FormatId.Fb2, ContainerKind.Raw, "FictionBook root element");
        }

        return (FormatId.Unknown, ContainerKind.Unknown, null);
    }

    /// <summary>
    /// Distinguishes the ZIP-based formats from one another.
    /// </summary>
    /// <remarks>
    /// Tries the first local file header first, which is inside the bytes
    /// already read and settles every conformant file. An EPUB is required to
    /// store <c>mimetype</c> first and uncompressed, precisely so that a
    /// consumer can identify it without decompressing anything.
    /// </remarks>
    private static (FormatId, ContainerKind, string?) IdentifyZip(
        ReadOnlySpan<byte> head, Stream stream, long origin)
    {
        string? firstEntry = ReadFirstLocalEntryName(head, out ReadOnlySpan<byte> firstEntryData);

        if (firstEntry is not null)
        {
            if (firstEntry.Equals("mimetype", StringComparison.Ordinal) &&
                firstEntryData.StartsWith(EpubMediaType))
            {
                // The content is checked rather than the name alone, because a CBZ
                // could contain a file called mimetype too.
                return (FormatId.Epub, ContainerKind.Zip, "mimetype declares application/epub+zip");
            }

            (FormatId, ContainerKind, string?)? byName = ClassifyByEntryName(firstEntry, "first entry");
            if (byName is { } resolved)
            {
                return resolved;
            }
        }

        // Inconclusive: a non-conformant EPUB with mimetype somewhere other than
        // first, or one whose mimetype is compressed so its bytes cannot be read
        // inline, or a comic whose first entry is a stray file. Fall back to the
        // central directory — names only, nothing decompressed.
        //
        // A compressed mimetype used to stop here and be reported as an anonymous
        // ZIP, which meant the one kind of broken EPUB this tool can repair
        // outright — rule EPUB-E040, fixed by storing the entry on save — was the
        // one it refused to open. Recognising the format is not the same as
        // endorsing the file: the defect is still reported, and still corrected.
        return IdentifyZipFromCentralDirectory(stream, origin);
    }

    private static (FormatId, ContainerKind, string?) IdentifyZipFromCentralDirectory(
        Stream stream, long origin)
    {
        if (!stream.CanSeek)
        {
            return (FormatId.UnknownZip, ContainerKind.Zip, "ZIP, contents not inspected");
        }

        try
        {
            stream.Position = origin;
            var directory = Containers.ZipCentralDirectory.Read(stream, path: null);

            bool sawImage = false;
            bool sawOther = false;

            foreach (var record in directory.Records)
            {
                string name = record.Name;

                if (name.EndsWith('/'))
                {
                    continue;
                }

                if (name.Equals("mimetype", StringComparison.Ordinal))
                {
                    return (FormatId.Epub, ContainerKind.Zip, "mimetype entry present");
                }

                (FormatId, ContainerKind, string?)? byName = ClassifyByEntryName(name, "entry");
                if (byName is { } resolved)
                {
                    return resolved;
                }

                if (IsImage(name))
                {
                    sawImage = true;
                }
                else
                {
                    sawOther = true;
                }
            }

            // "Only image files" is the ComicRack convention for an untagged
            // comic — an archive of pages and nothing else.
            if (sawImage && !sawOther)
            {
                return (FormatId.Cbz, ContainerKind.Zip, "archive contains only images");
            }

            return (FormatId.UnknownZip, ContainerKind.Zip, "ZIP with no recognised metadata");
        }
        catch (BookFormatException)
        {
            return (FormatId.UnknownZip, ContainerKind.Zip, "ZIP with an unreadable central directory");
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = origin;
            }
        }
    }

    private static (FormatId, ContainerKind, string?)? ClassifyByEntryName(string name, string how)
    {
        string leaf = name.Substring(name.LastIndexOf('/') + 1);

        if (leaf.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase))
        {
            return (FormatId.Cbz, ContainerKind.Zip, $"{how} is ComicInfo.xml");
        }

        if (leaf.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
        {
            return (FormatId.Fb2Zip, ContainerKind.Zip, $"{how} is a FictionBook document");
        }

        if (leaf.Equals("comet.xml", StringComparison.OrdinalIgnoreCase))
        {
            return (FormatId.Cbz, ContainerKind.Zip, $"{how} is comet.xml");
        }

        if (IsImage(leaf) && how == "first entry")
        {
            return (FormatId.Cbz, ContainerKind.Zip, "first entry is an image");
        }

        return null;
    }

    /// <summary>
    /// Reads the first local file header's entry name and the start of its
    /// data, from bytes already in hand.
    /// </summary>
    private static string? ReadFirstLocalEntryName(ReadOnlySpan<byte> head, out ReadOnlySpan<byte> data)
    {
        data = default;

        const int LocalHeaderFixedSize = 30;
        if (head.Length < LocalHeaderFixedSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(head) != ZipLocalFileHeaderSignature)
        {
            return null;
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(6));
        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(26));
        ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(28));

        if (nameLength == 0 || LocalHeaderFixedSize + nameLength > head.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> nameBytes = head.Slice(LocalHeaderFixedSize, nameLength);
        string name = (flags & 0x0800) != 0
            ? Encoding.UTF8.GetString(nameBytes)
            : Encodings.Latin1.GetString(nameBytes);

        int dataStart = LocalHeaderFixedSize + nameLength + extraLength;
        if (dataStart < head.Length)
        {
            data = head.Slice(dataStart);
        }

        return name;
    }

    private static bool IsImage(string name)
    {
        foreach (string ext in ImageExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Looks for a <c>FictionBook</c> root element near the start of the file.
    /// </summary>
    /// <remarks>
    /// Searches rather than matching at a fixed offset because an FB2 may open
    /// with a BOM, an XML declaration, comments, or processing instructions
    /// before the root element, in any combination.
    /// </remarks>
    private static bool LooksLikeFictionBook(ReadOnlySpan<byte> head)
    {
        // Cheap gate: every FB2 is XML, so require an angle bracket early on
        // before doing any string work.
        int start = head.StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;

        ReadOnlySpan<byte> body = head.Slice(start);
        if (body.IsEmpty || body[0] != (byte)'<')
        {
            return false;
        }

        string text = Encoding.UTF8.GetString(body.Slice(0, Math.Min(body.Length, 2048)));
        return text.Contains("<FictionBook", StringComparison.Ordinal);
    }
}
