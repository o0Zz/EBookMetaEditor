using EBookMeta.Containers;
using EBookMeta.Documents;
using EBookMeta.Model;

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
        IContainer container, ReadOptions? options = null, ICollection<Finding>? findings = null)
    {
        Throw.IfNull(container);

        options ??= ReadOptions.Default;

        List<MobiDocument> headers = ReadHeaders(container, findings);
        MobiDocument preferred = headers[headers.Count - 1];

        BookMetadata metadata = preferred.ReadMetadata();

        if (options.IncludeCover)
        {
            ReadCover(container, preferred, metadata, findings);
        }

        if (findings is not null)
        {
            CheckRequiredMetadata(preferred, metadata, findings);
            CheckHeaders(container, headers, findings);
            CheckCover(container, preferred, findings);
        }

        Log.Info(
            $"Read MOBI metadata from {headers.Count} header record"
            + $"{(headers.Count == 1 ? "" : "s")}: title={Describe(metadata.Title)}, "
            + $"creators={metadata.Creators.Count}, encoding={preferred.TextEncoding.WebName}.");

        return metadata;
    }

    private static string Describe(string? value) => value is null ? "(none)" : $"\"{value}\"";

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// The text is encrypted, or the database cannot be rebuilt.
    /// </exception>
    public void Write(
        IContainer container,
        BookMetadata metadata,
        string targetPath,
        ICollection<Finding>? findings = null)
    {
        Throw.IfNull(container);
        Throw.IfNull(metadata);
        Throw.IfNullOrEmpty(targetPath);

        List<MobiDocument> headers = ReadHeaders(container, findings);

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
            findings?.Add(new Finding
            {
                RuleId = "MOBI-W030",
                Severity = Severity.Warning,
                Message = "This is a joint MOBI and KF8 file; the fields you changed were "
                    + $"written to both of its {headers.Count} headers so that every reader "
                    + "shows the same thing.",
                HasAutofix = true,
            });
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
        IContainer container, ICollection<Finding>? findings)
    {
        MobiDocument first = ParseHeader(container, 0, findings);

        if (first.HasDrm)
        {
            var finding = new Finding
            {
                RuleId = "MOBI-F002",
                Severity = Severity.Fatal,
                Message = "This book's text is encrypted with DRM. Rewriting its metadata "
                    + "would produce a file no reader would open, so nothing was changed.",
                Location = container.Entries[0].Name,
            };

            findings?.Add(finding);
            throw new BookFormatException(finding.Message, path: null);
        }

        var headers = new List<MobiDocument> { first };

        if (first.Kf8BoundaryRecord is not { } boundary ||
            boundary <= 0 || boundary >= container.Entries.Count)
        {
            return headers;
        }

        try
        {
            MobiDocument kf8 = ParseHeader(container, boundary, findings: null);

            if (!kf8.HasDrm)
            {
                headers.Add(kf8);
            }
        }
        catch (BookFormatException)
        {
            // A boundary pointing at something that is not a header is worth
            // saying, but the MOBI 6 half is still perfectly editable.
            findings?.Add(new Finding
            {
                RuleId = "MOBI-W031",
                Severity = Severity.Warning,
                Message = $"EXTH record 121 says the KF8 part starts at record {boundary}, "
                    + "but no MOBI header is there. Only the first header will be updated.",
                Detail = boundary.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        return headers;
    }

    private static MobiDocument ParseHeader(
        IContainer container, int index, ICollection<Finding>? findings)
    {
        ContainerEntry entry = container.Entries[index];

        try
        {
            return MobiDocument.Parse(ReadAllBytes(container, entry), entry.Name);
        }
        catch (BookFormatException ex)
        {
            findings?.Add(new Finding
            {
                RuleId = "MOBI-F001",
                Severity = Severity.Fatal,
                Message = ex.Message,
                Location = entry.Name,
            });

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
        BookMetadata metadata,
        ICollection<Finding>? findings)
    {
        if (CoverRecord(container, header) is not { } index)
        {
            return;
        }

        byte[] data = ReadAllBytes(container, container.Entries[index]);

        if (MediaTypeOf(data) is not { } mediaType)
        {
            findings?.Add(new Finding
            {
                RuleId = "MOBI-W021",
                Severity = Severity.Warning,
                Message = $"Record {index} is declared as the cover but does not begin like "
                    + "an image.",
                Location = container.Entries[index].Name,
            });

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

    private static byte[] ReadAllBytes(IContainer container, ContainerEntry entry)
    {
        using Stream stream = container.OpenRead(entry);
        using var buffer = new MemoryStream(entry.Length > 0 ? (int)entry.Length : 4096);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
