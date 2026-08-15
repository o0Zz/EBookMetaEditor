using EBookMeta.Xml;
using EBookMeta.Model;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace EBookMeta.Formats;

/// <summary>
/// Reads and writes MOBI-family metadata: a PalmDB database whose header record
/// carries an EXTH block.
/// </summary>
/// <remarks>
/// One implementation serves MOBI, PRC, AZW and AZW3, because they are the same
/// container and the same header — the differences are in the text format, which
/// this build never reads.
/// <para>
/// An AZW3 from kindlegen is often a joint file: an old MOBI 6 book and a KF8 one
/// in the same database, each with its own header record and its own EXTH. Readers
/// prefer the KF8 part, so that is where metadata is read from, and <em>both</em>
/// are written — a file whose two halves disagree about its title is a file that
/// shows the old one on half the devices that open it.
/// </para>
/// <para>
/// The rules live beside this in <c>MobiFormat.Rules.cs</c>, which is the same
/// class.
/// </para>
/// </remarks>
public sealed partial class MobiFormat : IBookFormat
{
    /// <summary>Creates the format for one flavour of the MOBI family.</summary>
    /// <param name="id">
    /// <see cref="FormatId.Mobi"/> for MOBI and PRC, <see cref="FormatId.Azw3"/>
    /// for AZW and AZW3.
    /// </param>
    public MobiFormat(FormatId id = FormatId.Mobi)
    {
        Id = id;

        Capabilities = new FormatCapabilities
        {
            Format = id,

            // No series. MOBI has no EXTH record for one that this build can
            // verify against the published description of the format, and writing
            // a guessed record number would put a user's series into a field that
            // means something else. Better to grey the box out than to be wrong
            // in a file the user cannot inspect.
            //
            // No sort forms either: EXTH has no field for them.
            ReadableFields =
                MetadataField.Title | MetadataField.Creators | MetadataField.Description |
                MetadataField.Publisher | MetadataField.PublicationDate |
                MetadataField.Language | MetadataField.Subjects |
                MetadataField.Identifiers | MetadataField.Rights | MetadataField.Cover,

            // Everything readable except the cover and the identifiers. The cover
            // is a whole PalmDB record; replacing it would resize the record list,
            // and page-image processing is out of scope. Identifiers are read but
            // not written because an ASIN is Amazon's, not the user's, and an
            // edited one breaks a book's link to its store entry.
            WritableFields =
                MetadataField.Title | MetadataField.Creators | MetadataField.Description |
                MetadataField.Publisher | MetadataField.PublicationDate |
                MetadataField.Language | MetadataField.Subjects | MetadataField.Rights,
        };

        Extensions = id == FormatId.Azw3 ? [".azw", ".azw3"] : [".mobi", ".prc"];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Only the MOBI instance answers, and it answers for the whole family. The
    /// PalmDB header says <c>BOOKMOBI</c> for every one of them and nothing in it
    /// distinguishes an AZW3 from a MOBI — that difference is in the text format,
    /// which this build never reads. Guessing between them would be inventing
    /// information, so the extension is left to make the finer claim and
    /// <see cref="BookFormats.IsAcceptableSubstitute"/> keeps the pairing from
    /// being reported as a mismatch.
    /// <para>
    /// The container sniff settles it, so the database is never opened to decline
    /// a file that is not one.
    /// </para>
    /// </remarks>
    public FormatClaim? TryOpen(BookSource source)
    {
        Throw.IfNull(source);

        return Id == FormatId.Mobi && source.ContainerKind == ContainerKind.PalmDb
            ? new FormatClaim
            {
                Format = FormatId.Mobi,
                Detail = "PalmDB MOBI header",
                Confidence = MatchConfidence.Strong,
            }
            : null;
    }

    /// <inheritdoc />
    public FormatId Id { get; }

    /// <inheritdoc />
    public FormatCapabilities Capabilities { get; }

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// The database carries no MOBI header (MOBI-F001), or its text is encrypted
    /// (MOBI-F002).
    /// </exception>
    public BookMetadata Read(
        IContainer container, ReadOptions? options = null)
    {
        Throw.IfNull(container);

        options ??= ReadOptions.Default;

        List<MobiDocument> headers = ReadHeaders(container);
        MobiDocument preferred = headers[headers.Count - 1];

        BookMetadata metadata = preferred.ReadMetadata();

        if (options.IncludeCover)
        {
            ReadCover(container, preferred, metadata);
        }

        CheckRequiredMetadata(preferred, metadata);
        CheckHeaders(container, headers);
        CheckCover(container, preferred);

        Log.Info(
            $"Read MOBI metadata from {headers.Count} header record"
            + $"{(headers.Count == 1 ? "" : "s")}: title={Log.Describe(metadata.Title)}, "
            + $"creators={metadata.Creators.Count}, encoding={preferred.TextEncoding.WebName}.");

        return metadata;
    }

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// The text is encrypted, or the database cannot be rebuilt.
    /// </exception>
    public void Write(
        IContainer container,
        BookMetadata metadata,
        string targetPath)
    {
        Throw.IfNull(container);
        Throw.IfNull(metadata);
        Throw.IfNullOrEmpty(targetPath);

        List<MobiDocument> headers = ReadHeaders(container);

        // What Read handed the caller, so the difference between it and what came
        // back is exactly what the user edited.
        BookMetadata baseline = headers[headers.Count - 1].ReadMetadata();

        var rewritten = new Dictionary<int, byte[]>();
        bool modified = false;

        foreach (MobiDocument header in headers)
        {
            // Only the edited fields are propagated, never the whole record. The
            // two halves of a joint file often carry different metadata, and
            // writing one over the other would delete whatever the other had and
            // this one does not — on a save the user may have made without
            // editing anything at all.
            header.ApplyMetadata(Merge(baseline, metadata, header.ReadMetadata()));

            rewritten[IndexOf(header)] = header.Serialize();
            modified |= header.IsModified;
        }

        if (headers.Count > 1 && modified)
        {
            Log.Rule(
                LogLevel.Warning,
                "MOBI-W030",
                "This is a joint MOBI and KF8 file; the fields you changed were "
                    + $"written to both of its {headers.Count} headers so that every reader "
                    + "shows the same thing.");
        }

        var entries = new List<PendingEntry>(container.Entries.Count);

        foreach (ContainerEntry existing in container.Entries)
        {
            entries.Add(rewritten.TryGetValue(existing.Index, out byte[]? bytes)
                ? PendingEntry.Replacing(existing, bytes)
                : PendingEntry.CopyOf(container, existing));
        }

        container.Rebuild(entries, targetPath);

        Log.Info($"Wrote {entries.Count} PalmDB records, rewriting {rewritten.Count} header(s).");
    }

    /// <summary>
    /// Produces the metadata to write into one header: its own, with the fields
    /// the user actually edited overlaid.
    /// </summary>
    /// <param name="baseline">The metadata the read handed the caller.</param>
    /// <param name="edited">What came back, possibly changed.</param>
    /// <param name="current">What this particular header holds now.</param>
    /// <returns>The metadata to apply to this header.</returns>
    /// <remarks>
    /// For the header the read came from, <paramref name="current"/> and
    /// <paramref name="baseline"/> are the same and this is just
    /// <paramref name="edited"/>. It matters for the other half of a joint file,
    /// where applying <paramref name="edited"/> wholesale would overwrite fields
    /// the user never saw with values from a header they were not editing.
    /// </remarks>
    private static BookMetadata Merge(
        BookMetadata baseline, BookMetadata edited, BookMetadata current)
    {
        static bool Changed(string? was, string? now) =>
            !string.Equals(was, now, StringComparison.Ordinal);

        static IEnumerable<string> Names(BookMetadata m) => m.Creators.Select(c => c.Name);

        var merged = new BookMetadata
        {
            Title = Changed(baseline.Title, edited.Title) ? edited.Title : current.Title,
            Description = Changed(baseline.Description, edited.Description)
                ? edited.Description
                : current.Description,
            Publisher = Changed(baseline.Publisher, edited.Publisher)
                ? edited.Publisher
                : current.Publisher,
            Rights = Changed(baseline.Rights, edited.Rights) ? edited.Rights : current.Rights,
            Language = Changed(baseline.Language, edited.Language)
                ? edited.Language
                : current.Language,
            PublicationDate = Changed(baseline.PublicationDate?.Raw, edited.PublicationDate?.Raw)
                ? edited.PublicationDate
                : current.PublicationDate,
        };

        BookMetadata creators = Names(baseline).SequenceEqual(Names(edited), StringComparer.Ordinal)
            ? current
            : edited;

        foreach (Creator creator in creators.Creators)
        {
            merged.Creators.Add(creator);
        }

        BookMetadata subjects =
            baseline.Subjects.SequenceEqual(edited.Subjects, StringComparer.Ordinal)
                ? current
                : edited;

        foreach (string subject in subjects.Subjects)
        {
            merged.Subjects.Add(subject);
        }

        return merged;
    }

    /// <summary>
    /// Reads every header record in the database: record 0, and the KF8 part's own
    /// if this is a joint file.
    /// </summary>
    /// <returns>The headers, in record order, so the last is the preferred one.</returns>
    private static List<MobiDocument> ReadHeaders(
        IContainer container)
    {
        MobiDocument first = ParseHeader(container, 0);

        if (first.HasDrm)
        {
            // Reported and then thrown, in that order, so the rule id reaches the
            // log before the open fails and the user has something to search for.
            const string drm =
                "This book's text is encrypted with DRM. Rewriting its metadata "
                + "would produce a file no reader would open, so nothing was changed.";

            Log.Rule(LogLevel.Error, "MOBI-F002", drm, container.Entries[0].Name);
            throw new BookFormatException(drm, path: null);
        }

        var headers = new List<MobiDocument> { first };

        if (first.Kf8BoundaryRecord is not { } boundary ||
            boundary <= 0 || boundary >= container.Entries.Count)
        {
            return headers;
        }

        try
        {
            MobiDocument kf8 = ParseHeader(container, boundary);

            if (!kf8.HasDrm)
            {
                headers.Add(kf8);
            }
        }
        catch (BookFormatException)
        {
            // A boundary pointing at something that is not a header is worth
            // saying, but the MOBI 6 half is still perfectly editable.
            Log.Rule(
                LogLevel.Warning,
                "MOBI-W031",
                $"EXTH record 121 says the KF8 part starts at record {boundary}, "
                    + "but no MOBI header is there. Only the first header will be updated.");
        }

        return headers;
    }

    private static MobiDocument ParseHeader(
        IContainer container, int index)
    {
        ContainerEntry entry = container.Entries[index];

        try
        {
            return MobiDocument.Parse(container.ReadAllBytes(entry), entry.Name);
        }
        catch (BookFormatException ex)
        {
            Log.Rule(LogLevel.Error, "MOBI-F001", ex.Message, entry.Name);
            throw;
        }
    }

    /// <summary>
    /// Which record a header came from, worked out from its location.
    /// </summary>
    private static int IndexOf(MobiDocument header) =>
        int.TryParse(
            header.Location.Replace("record", string.Empty),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int index)
            ? index
            : 0;

    /// <summary>
    /// Reads the cover out of the image record EXTH 201 points at.
    /// </summary>
    /// <remarks>
    /// The offset is relative to the first image record rather than absolute,
    /// which is the one place MOBI's metadata refers to its own record numbering.
    /// </remarks>
    private static void ReadCover(
        IContainer container,
        MobiDocument header,
        BookMetadata metadata)
    {
        if (CoverRecord(container, header) is not { } index)
        {
            return;
        }

        byte[] data = container.ReadAllBytes(container.Entries[index]);

        if (MediaTypeOf(data) is not { } mediaType)
        {
            Log.Rule(
                LogLevel.Warning,
                "MOBI-W021",
                $"Record {index} is declared as the cover but does not begin like "
                    + "an image.",
                container.Entries[index].Name);
            return;
        }

        metadata.Cover = new CoverImage { Data = data, MediaType = mediaType };
    }

    /// <summary>
    /// The absolute record index of the cover, or null when there is not one.
    /// </summary>
    private static int? CoverRecord(IContainer container, MobiDocument header)
    {
        if (header.CoverImageOffset is not { } offset || header.FirstImageIndex < 0)
        {
            return null;
        }

        long index = (long)header.FirstImageIndex + offset;

        return index > 0 && index < container.Entries.Count ? (int)index : null;
    }

    /// <summary>
    /// Identifies an image by its magic number, since MOBI records carry no type.
    /// </summary>
    private static string? MediaTypeOf(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
        {
            return "image/png";
        }

        if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
        {
            return "image/gif";
        }

        return null;
    }

}

/// <summary>
/// The MOBI validation rules — the half of <see cref="MobiFormat"/> that reports
/// rather than reads.
/// </summary>
/// <remarks>
/// Every rule works from the header record the read already parsed, or from the
/// PalmDB record table the container already walked, so running all of them costs
/// a read nothing. Nothing here decompresses a text record: this build has no
/// reason to look at a book's contents and does not.
/// </remarks>
public sealed partial class MobiFormat
{
    /// <summary>
    /// Checks the fields a book needs before a library can file it.
    /// </summary>
    private static void CheckRequiredMetadata(
        MobiDocument header, BookMetadata metadata)
    {
        string location = header.Location;

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            Log.Rule(
                LogLevel.Error,
                "MOBI-E010",
                "The book has no title, in either the header's name field or "
                    + "EXTH record 503.",
                location);
        }

        if (!metadata.PrimaryCreators.Any())
        {
            Log.Rule(
                LogLevel.Warning,
                "MOBI-W011",
                "There is no author (EXTH record 100), so a library will file "
                    + "this book under no author at all.",
                location);
        }

        if (metadata.Language is null)
        {
            Log.Rule(
                LogLevel.Warning,
                "MOBI-W012",
                "There is no language (EXTH record 524). The MOBI header's own "
                    + "locale field is not read as a substitute, because mapping its "
                    + "numeric code back to a language tag would be a guess.",
                location);
        }
    }

    /// <summary>
    /// Reports what the database's header records say about each other.
    /// </summary>
    private static void CheckHeaders(
        IContainer container, List<MobiDocument> headers)
    {
        if (headers.Count < 2)
        {
            return;
        }

        // Both halves are read, so a disagreement can be stated rather than
        // guessed at. It is reported and left alone: neither half is provably the
        // right one, and copying the KF8 half over the MOBI 6 one would delete
        // whatever fields only the older half carries. A save propagates what the
        // user edits and nothing else.
        BookMetadata first = headers[0].ReadMetadata();
        BookMetadata second = headers[1].ReadMetadata();

        if (!string.Equals(first.Title, second.Title, StringComparison.Ordinal))
        {
            Log.Rule(
                LogLevel.Warning,
                "MOBI-W020",
                "The MOBI and KF8 halves of this file carry different titles "
                    + $"('{first.Title}' and '{second.Title}'). The KF8 one is shown, "
                    + "because that is the half readers use. Editing the title writes both; "
                    + "saving without editing changes neither.");
        }
    }

    /// <summary>
    /// Reports a cover reference that points outside the database.
    /// </summary>
    private static void CheckCover(
        IContainer container, MobiDocument header)
    {
        if (header.CoverImageOffset is not { } offset)
        {
            Log.Rule(
                LogLevel.Warning,
                "MOBI-W022",
                "No cover is declared (EXTH record 201), so readers will show "
                    + "this book with a generated one.",
                header.Location);
            return;
        }

        if (header.FirstImageIndex < 0)
        {
            Log.Rule(
                LogLevel.Error,
                "MOBI-E023",
                $"A cover is declared at image {offset}, but the header does not "
                    + "say which record the images start at, so it cannot be found.",
                header.Location);
            return;
        }

        long index = (long)header.FirstImageIndex + offset;

        if (index <= 0 || index >= container.Entries.Count)
        {
            Log.Rule(
                LogLevel.Error,
                "MOBI-E023",
                $"The cover points at record {index}, which is outside this "
                    + $"database's {container.Entries.Count} records.",
                header.Location);
        }
    }
}

/// <summary>
/// The header record of a MOBI database: the PalmDOC and MOBI headers, and the
/// EXTH block where the metadata lives.
/// </summary>
/// <remarks>
/// Implemented from the published description of the format. Nothing here is
/// derived from calibre, whose <c>MetadataUpdater</c> is GPL-3.0 and incompatible
/// with this project's licence.
/// <para>
/// A rebuild copies the PalmDOC header, the MOBI header and everything between the
/// EXTH block and the title byte for byte, patching only the one MOBI header field
/// that has to move — <c>fullNameOffset</c> — and re-emitting the EXTH records.
/// EXTH records this build has no field for are carried through in their original
/// order and bytes, which is what stops a title edit from discarding an ASIN, a
/// watermark or a page-map.
/// </para>
/// <para>
/// An unedited save returns the original bytes rather than a reconstruction, so
/// byte-identity does not depend on reproducing another tool's padding decisions.
/// </para>
/// </remarks>
public sealed class MobiDocument
{
    private const int PalmDocHeaderLength = 16;
    private const int EncryptionTypeOffset = 12;

    private const int MobiIdentifierOffset = 16;
    private const int MobiHeaderLengthOffset = 20;

    // Offsets within the MOBI header, which begins at MobiIdentifierOffset.
    private const int TextEncodingField = 0x0C;
    private const int FullNameOffsetField = 0x44;
    private const int FullNameLengthField = 0x48;
    private const int FirstImageIndexField = 0x5C;
    private const int ExthFlagsField = 0x70;

    /// <summary>The bit in <c>exthFlags</c> that says an EXTH block follows.</summary>
    private const uint ExthPresentFlag = 0x40;

    private readonly byte[] _original;
    private readonly byte[] _palmDoc;
    private readonly byte[] _mobiHeader;
    private readonly byte[] _gap;
    private readonly byte[] _trailer;
    private readonly List<ExthRecord> _records;
    private readonly Encoding _encoding;
    private string? _fullName;
    private bool _dirty;

    /// <summary>One EXTH record: a type number and its payload.</summary>
    private sealed record ExthRecord(int Type, byte[] Data);

    private MobiDocument(
        byte[] original,
        byte[] palmDoc,
        byte[] mobiHeader,
        byte[] gap,
        byte[] trailer,
        List<ExthRecord> records,
        Encoding encoding,
        string? fullName,
        string location)
    {
        _original = original;
        _palmDoc = palmDoc;
        _mobiHeader = mobiHeader;
        _gap = gap;
        _trailer = trailer;
        _records = records;
        _encoding = encoding;
        _fullName = fullName;
        Location = location;
    }

    /// <summary>Where this record came from, for diagnostics.</summary>
    public string Location { get; }

    /// <summary>
    /// Whether <see cref="ApplyMetadata"/> changed anything, so a caller can tell
    /// a rewritten header from one that was left alone.
    /// </summary>
    public bool IsModified => _dirty;

    /// <summary>
    /// Whether the text is encrypted. DRM is out of scope, and a file carrying it
    /// is refused rather than worked around.
    /// </summary>
    public bool HasDrm =>
        BinaryPrimitives.ReadUInt16BigEndian(_palmDoc.AsSpan(EncryptionTypeOffset)) != 0;

    /// <summary>The record index the book's images start at, or -1 if unstated.</summary>
    /// <remarks>
    /// Record 0 is this header, so a zero here means the field was never filled in
    /// rather than that the images start at the beginning. kindlegen writes
    /// <c>0xFFFFFFFF</c> for the same thing.
    /// </remarks>
    public int FirstImageIndex
    {
        get
        {
            uint value = ReadMobiField(FirstImageIndexField, uint.MaxValue);
            return value is 0 or uint.MaxValue ? -1 : (int)value;
        }
    }

    /// <summary>
    /// The cover's position relative to <see cref="FirstImageIndex"/>, from EXTH
    /// 201, or <see langword="null"/> when no cover is declared.
    /// </summary>
    public int? CoverImageOffset => ReadUInt32Record(ExthCoverOffset) is { } value
        ? (int)value
        : null;

    /// <summary>
    /// The record index where the KF8 part of a joint MOBI/KF8 file begins, from
    /// EXTH 121, or <see langword="null"/> for a plain MOBI.
    /// </summary>
    /// <remarks>
    /// An AZW3 produced by kindlegen often carries both an old MOBI 6 book and a
    /// KF8 one in the same database. Readers prefer the KF8 part, so its metadata
    /// is the metadata that matters — and both have to be written, or the file
    /// says two different things about itself.
    /// </remarks>
    public int? Kf8BoundaryRecord
    {
        get
        {
            uint? value = ReadUInt32Record(ExthKf8Boundary);

            // kindlegen writes 0xFFFFFFFF to mean "no boundary" rather than
            // leaving the record out.
            return value is null or uint.MaxValue ? null : (int)value.Value;
        }
    }

    /// <summary>The text encoding the EXTH strings are stored in.</summary>
    public Encoding TextEncoding => _encoding;

    // The EXTH record types this build understands. Everything else is preserved
    // without being interpreted.
    private const int ExthAuthor = 100;
    private const int ExthPublisher = 101;
    private const int ExthDescription = 103;
    private const int ExthIsbn = 104;
    private const int ExthSubject = 105;
    private const int ExthPublishingDate = 106;
    private const int ExthContributor = 108;
    private const int ExthRights = 109;
    private const int ExthKf8Boundary = 121;
    private const int ExthCoverOffset = 201;
    private const int ExthAsin = 113;
    private const int ExthUpdatedTitle = 503;
    private const int ExthLanguage = 524;

    /// <summary>Parses a MOBI header record.</summary>
    /// <param name="record">The record's bytes.</param>
    /// <param name="location">Where it came from, for diagnostics.</param>
    /// <returns>The parsed header.</returns>
    /// <exception cref="BookFormatException">
    /// The record is not a MOBI header. Surfaced as MOBI-F001.
    /// </exception>
    public static MobiDocument Parse(byte[] record, string location)
    {
        Throw.IfNull(record);
        Throw.IfNullOrEmpty(location);

        if (record.Length < MobiIdentifierOffset + 8)
        {
            throw new BookFormatException(
                "The first record is too short to hold a MOBI header.", location);
        }

        if (Encoding.ASCII.GetString(record, MobiIdentifierOffset, 4) != "MOBI")
        {
            throw new BookFormatException(
                "The first record carries no MOBI header, so this database is a PalmDB "
                + "but not a book this build can edit.",
                location);
        }

        int mobiLength = (int)BinaryPrimitives.ReadUInt32BigEndian(
            record.AsSpan(MobiHeaderLengthOffset));

        if (mobiLength < 8 || MobiIdentifierOffset + mobiLength > record.Length)
        {
            throw new BookFormatException(
                $"The MOBI header claims to be {mobiLength} bytes, which does not fit in "
                + "the record.",
                location);
        }

        byte[] palmDoc = record.AsSpan(0, PalmDocHeaderLength).ToArray();
        byte[] mobiHeader = record.AsSpan(MobiIdentifierOffset, mobiLength).ToArray();

        Encoding encoding = EncodingOf(mobiHeader, mobiLength);

        int exthStart = MobiIdentifierOffset + mobiLength;
        var records = new List<ExthRecord>();
        int exthEnd = exthStart;

        bool declared = (ReadField(mobiHeader, mobiLength, ExthFlagsField, 0) & ExthPresentFlag) != 0;

        if (declared && exthStart + 12 <= record.Length &&
            Encoding.ASCII.GetString(record, exthStart, 4) == "EXTH")
        {
            exthEnd = ParseExth(record, exthStart, records, location);
        }

        // The title's own offset is stated by the header, and what sits between the
        // EXTH block and it is padding this build copies rather than models.
        long nameOffset = ReadField(mobiHeader, mobiLength, FullNameOffsetField, 0);
        long nameLength = ReadField(mobiHeader, mobiLength, FullNameLengthField, 0);

        string? fullName = null;
        byte[] gap = [];
        byte[] trailer = [];

        if (nameOffset >= exthEnd && nameOffset + nameLength <= record.Length)
        {
            fullName = encoding.GetString(record, (int)nameOffset, (int)nameLength);
            gap = record.AsSpan(exthEnd, (int)(nameOffset - exthEnd)).ToArray();
            trailer = record.AsSpan((int)(nameOffset + nameLength)).ToArray();
        }
        else
        {
            // No usable title field. Everything past the EXTH block is still the
            // user's and is carried through untouched.
            trailer = record.AsSpan(exthEnd).ToArray();
        }

        return new MobiDocument(
            record, palmDoc, mobiHeader, gap, trailer, records, encoding, fullName, location);
    }

    /// <summary>
    /// Reads the EXTH records, returning where the block ends.
    /// </summary>
    private static int ParseExth(
        byte[] record, int start, List<ExthRecord> into, string location)
    {
        int declaredLength = (int)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(start + 4));
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(start + 8));

        if (declaredLength < 12 || start + declaredLength > record.Length)
        {
            throw new BookFormatException(
                $"The EXTH block claims to be {declaredLength} bytes, which does not fit "
                + "in the record.",
                location);
        }

        int position = start + 12;
        int limit = start + declaredLength;

        for (int i = 0; i < count; i++)
        {
            if (position + 8 > limit)
            {
                throw new BookFormatException(
                    $"The EXTH block claims {count} records but runs out after {i}.",
                    location);
            }

            int type = (int)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(position));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(position + 4));

            if (length < 8 || position + length > limit)
            {
                throw new BookFormatException(
                    $"EXTH record {i} declares a length of {length}, which does not fit "
                    + "in the block.",
                    location);
            }

            into.Add(new ExthRecord(type, record.AsSpan(position + 8, length - 8).ToArray()));
            position += length;
        }

        return limit;
    }

    /// <summary>
    /// The encoding EXTH strings use, which the MOBI header states as a code page.
    /// </summary>
    private static Encoding EncodingOf(byte[] mobiHeader, int mobiLength)
    {
        uint code = ReadField(mobiHeader, mobiLength, TextEncodingField, 1252);

        try
        {
            // 65001 is UTF-8 and 1252 is Windows Latin-1; those are the only two
            // in practice, but the header is free to say otherwise.
            return code == 65001
                ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                : Encoding.GetEncoding((int)code);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encodings.Latin1;
        }
    }

    private static uint ReadField(byte[] mobiHeader, int mobiLength, int field, uint fallback) =>
        field + 4 <= mobiLength
            ? BinaryPrimitives.ReadUInt32BigEndian(mobiHeader.AsSpan(field))
            : fallback;

    private uint ReadMobiField(int field, uint fallback) =>
        ReadField(_mobiHeader, _mobiHeader.Length, field, fallback);

    private uint? ReadUInt32Record(int type)
    {
        ExthRecord? record = _records.FirstOrDefault(r => r.Type == type);

        return record is { Data.Length: 4 }
            ? BinaryPrimitives.ReadUInt32BigEndian(record.Data)
            : null;
    }

    /// <summary>Reads the metadata this header carries.</summary>
    /// <returns>The metadata found.</returns>
    public BookMetadata ReadMetadata()
    {
        var metadata = new BookMetadata
        {
            // EXTH 503 is the authoritative title when it is present: Amazon adds
            // it to correct the one baked into the header, and readers prefer it.
            Title = Single(ExthUpdatedTitle) ?? NullIfEmpty(_fullName),
            Publisher = Single(ExthPublisher),
            Description = Single(ExthDescription),
            Rights = Single(ExthRights),
            Language = Single(ExthLanguage),
        };

        if (Single(ExthPublishingDate) is { } date)
        {
            metadata.PublicationDate = MakeDate(date);
        }

        foreach (string author in All(ExthAuthor))
        {
            metadata.Creators.Add(new Creator { Name = author, NativeRole = "author" });
        }

        foreach (string contributor in All(ExthContributor))
        {
            metadata.Creators.Add(new Creator
            {
                Name = contributor,
                Kind = CreatorKind.Contributor,
                NativeRole = "contributor",
            });
        }

        foreach (string subject in All(ExthSubject))
        {
            metadata.Subjects.Add(subject);
        }

        if (Single(ExthIsbn) is { } isbn)
        {
            metadata.Identifiers.Add(new Identifier { Value = isbn, Scheme = "ISBN" });
        }

        if (Single(ExthAsin) is { } asin)
        {
            metadata.Identifiers.Add(new Identifier { Value = asin, Scheme = "MOBI-ASIN" });
        }

        ReadUnmapped(metadata);

        return metadata;
    }

    /// <summary>
    /// Records every EXTH type this build does not map, with its bytes.
    /// </summary>
    /// <remarks>
    /// The bytes matter, not just the text: these are the only copy, and a rebuild
    /// writes them back exactly. Keeping them on the model as well is what lets the
    /// UI show a user what else is in their file.
    /// </remarks>
    private void ReadUnmapped(BookMetadata metadata)
    {
        foreach (ExthRecord record in _records)
        {
            if (IsManaged(record.Type))
            {
                continue;
            }

            metadata.UnmappedFields.Add(new UnmappedField
            {
                Source = "EXTH",
                Key = record.Type.ToString(CultureInfo.InvariantCulture),
                Text = Printable(record.Data),
                Bytes = record.Data,
            });
        }
    }

    private static bool IsManaged(int type) =>
        type is ExthAuthor or ExthPublisher or ExthDescription or ExthIsbn or ExthSubject
            or ExthPublishingDate or ExthContributor or ExthRights or ExthAsin
            or ExthUpdatedTitle or ExthLanguage;

    /// <summary>
    /// Renders a record's payload as text when it plausibly is text.
    /// </summary>
    private string? Printable(byte[] data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        // A four-byte payload is a number far more often than a word, and showing
        // a user the Latin-1 rendering of an integer helps nobody.
        if (data.Length == 4 && data.Any(b => b < 0x20))
        {
            return BinaryPrimitives.ReadUInt32BigEndian(data)
                .ToString(CultureInfo.InvariantCulture);
        }

        string text = _encoding.GetString(data);
        return text.Any(char.IsControl) ? null : text;
    }

    private static BookDate MakeDate(string raw)
    {
        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset value))
        {
            // MOBI conventionally stores a full ISO timestamp, but plenty of files
            // hold just a year.
            DatePrecision precision = raw.Length switch
            {
                4 => DatePrecision.Year,
                7 => DatePrecision.Month,
                10 => DatePrecision.Day,
                _ => DatePrecision.Time,
            };

            return new BookDate { Raw = raw, Value = value, Precision = precision };
        }

        return new BookDate { Raw = raw, Precision = DatePrecision.Unknown };
    }

    private string? Single(int type)
    {
        ExthRecord? record = _records.FirstOrDefault(r => r.Type == type);
        return record is null ? null : NullIfEmpty(_encoding.GetString(record.Data));
    }

    private List<string> All(int type) =>
        [.. _records
            .Where(r => r.Type == type)
            .Select(r => _encoding.GetString(r.Data).Trim())
            .Where(s => s.Length > 0)];

    private static string? NullIfEmpty(string? value)
    {
        string? trimmed = value?.Trim('\0').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Applies metadata to the header, touching only what changed.
    /// </summary>
    /// <param name="metadata">The metadata to write.</param>
    public void ApplyMetadata(BookMetadata metadata)
    {
        Throw.IfNull(metadata);

        BookMetadata current = ReadMetadata();

        SetSingle(ExthPublisher, current.Publisher, metadata.Publisher);
        SetSingle(ExthDescription, current.Description, metadata.Description);
        SetSingle(ExthRights, current.Rights, metadata.Rights);
        SetSingle(ExthLanguage, current.Language, metadata.Language);
        SetSingle(
            ExthPublishingDate, current.PublicationDate?.Raw, metadata.PublicationDate?.Raw);

        SetMultiple(
            ExthAuthor,
            [.. current.PrimaryCreators.Select(c => c.Name)],
            [.. metadata.PrimaryCreators.Select(c => c.Name)]);

        SetMultiple(
            ExthContributor,
            [.. current.Creators.Where(c => c.Kind == CreatorKind.Contributor).Select(c => c.Name)],
            [.. metadata.Creators.Where(c => c.Kind == CreatorKind.Contributor).Select(c => c.Name)]);

        SetMultiple(ExthSubject, [.. current.Subjects], [.. metadata.Subjects]);

        SetTitle(current.Title, metadata.Title);
    }

    /// <summary>
    /// Writes the title to both places a MOBI can keep one.
    /// </summary>
    /// <remarks>
    /// The header's own name field is always updated. EXTH 503 is updated only when
    /// the file already had one: adding it to a file that did not would change what
    /// the file claims about itself beyond what was asked, and the header field is
    /// the one every reader falls back to.
    /// </remarks>
    private void SetTitle(string? current, string? wanted)
    {
        if (Same(current, wanted))
        {
            return;
        }

        string title = wanted?.Trim() ?? string.Empty;

        _fullName = title;
        _dirty = true;

        if (_records.Any(r => r.Type == ExthUpdatedTitle))
        {
            ReplaceAll(ExthUpdatedTitle, title.Length == 0 ? [] : [title]);
        }
    }

    private void SetSingle(int type, string? current, string? wanted)
    {
        if (Same(current, wanted))
        {
            return;
        }

        ReplaceAll(type, string.IsNullOrWhiteSpace(wanted) ? [] : [wanted!.Trim()]);
    }

    private void SetMultiple(int type, List<string> current, List<string> wanted)
    {
        if (current.SequenceEqual(wanted, StringComparer.Ordinal))
        {
            return;
        }

        ReplaceAll(type, wanted);
    }

    /// <summary>
    /// Replaces every record of a type with new ones, in the place the first of
    /// them held.
    /// </summary>
    /// <remarks>
    /// Position is preserved rather than appending, so a rewritten author stays
    /// where it was among the records this build does not understand. Order within
    /// EXTH carries no meaning the specification defines, but reproducing it keeps
    /// the diff to what actually changed.
    /// </remarks>
    private void ReplaceAll(int type, IReadOnlyList<string> values)
    {
        int at = _records.FindIndex(r => r.Type == type);
        _records.RemoveAll(r => r.Type == type);

        if (at < 0)
        {
            at = _records.Count;
        }

        for (int i = 0; i < values.Count; i++)
        {
            _records.Insert(at + i, new ExthRecord(type, _encoding.GetBytes(values[i])));
        }

        _dirty = true;
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(
            string.IsNullOrWhiteSpace(a) ? null : a!.Trim(),
            string.IsNullOrWhiteSpace(b) ? null : b!.Trim(),
            StringComparison.Ordinal);

    /// <summary>Serialises the header record back to bytes.</summary>
    /// <returns>The complete record 0.</returns>
    /// <remarks>
    /// Returns the original bytes untouched when nothing was changed, so an
    /// unedited save cannot differ by so much as a padding byte — this build does
    /// not have to reproduce another tool's choices about how to round out an EXTH
    /// block, only its own.
    /// </remarks>
    public byte[] Serialize()
    {
        if (!_dirty)
        {
            return _original;
        }

        byte[] exth = BuildExth();
        byte[] name = _encoding.GetBytes(_fullName ?? string.Empty);

        // The gap the original kept between the EXTH block and the title is
        // reproduced, unless there was none to observe.
        byte[] gap = _gap.Length > 0 ? _gap : [0, 0];

        int nameOffset = MobiIdentifierOffset + _mobiHeader.Length + exth.Length + gap.Length;

        byte[] mobiHeader = (byte[])_mobiHeader.Clone();
        WriteMobiField(mobiHeader, FullNameOffsetField, (uint)nameOffset);
        WriteMobiField(mobiHeader, FullNameLengthField, (uint)name.Length);

        if (exth.Length > 0)
        {
            WriteMobiField(
                mobiHeader, ExthFlagsField, ReadMobiField(ExthFlagsField, 0) | ExthPresentFlag);
        }

        using var output = new MemoryStream(_original.Length + 256);

        output.Write(_palmDoc, 0, _palmDoc.Length);
        output.Write(mobiHeader, 0, mobiHeader.Length);
        output.Write(exth, 0, exth.Length);
        output.Write(gap, 0, gap.Length);
        output.Write(name, 0, name.Length);
        output.Write(_trailer, 0, _trailer.Length);

        // Two terminating NULs and padding to a four-byte boundary is what every
        // producer writes after the title, and some readers rely on the record
        // being aligned.
        if (_trailer.Length == 0)
        {
            int padding = 2 + (4 - ((name.Length + 2) % 4)) % 4;
            output.Write(new byte[padding], 0, padding);
        }

        return output.ToArray();
    }

    private void WriteMobiField(byte[] mobiHeader, int field, uint value)
    {
        if (field + 4 <= mobiHeader.Length)
        {
            BinaryPrimitives.WriteUInt32BigEndian(mobiHeader.AsSpan(field), value);
        }
    }

    /// <summary>
    /// Builds the EXTH block: a twelve-byte header, the records, and padding to a
    /// four-byte boundary.
    /// </summary>
    private byte[] BuildExth()
    {
        if (_records.Count == 0)
        {
            return [];
        }

        int length = 12;

        foreach (ExthRecord record in _records)
        {
            length += 8 + record.Data.Length;
        }

        int padded = length + ((4 - (length % 4)) % 4);
        byte[] block = new byte[padded];

        Encoding.ASCII.GetBytes("EXTH", 0, 4, block, 0);

        // The length field counts the padding, which is why it is written from the
        // padded total rather than the sum of the records.
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), (uint)padded);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8), (uint)_records.Count);

        int position = 12;

        foreach (ExthRecord record in _records)
        {
            BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(position), (uint)record.Type);
            BinaryPrimitives.WriteUInt32BigEndian(
                block.AsSpan(position + 4), (uint)(record.Data.Length + 8));

            record.Data.CopyTo(block.AsSpan(position + 8));
            position += 8 + record.Data.Length;
        }

        return block;
    }
}
