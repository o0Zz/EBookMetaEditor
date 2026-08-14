using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using EBookMeta.Model;

namespace EBookMeta.Documents;

/// <summary>An entry in the OPF manifest.</summary>
public sealed record ManifestItem
{
    /// <summary>The item's <c>id</c>, unique within the manifest.</summary>
    public required string Id { get; init; }

    /// <summary>The <c>href</c>, relative to the OPF's own directory.</summary>
    public required string Href { get; init; }

    /// <summary>The declared media type.</summary>
    public string? MediaType { get; init; }

    /// <summary>The EPUB 3 <c>properties</c> attribute, when present.</summary>
    public string? Properties { get; init; }

    /// <summary>The element itself, for edits that must not disturb anything else.</summary>
    public required XElement Element { get; init; }

    /// <summary>Whether this item is marked as the cover image, EPUB 3 style.</summary>
    public bool IsCoverImage =>
        Properties is not null &&
        // A null separator array splits on any whitespace, which is what the
        // spec means by a space-separated property list.
        Properties.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                  .Contains("cover-image", StringComparer.Ordinal);
}

/// <summary>A reference from the spine to a manifest item.</summary>
public sealed record SpineItemRef
{
    /// <summary>The manifest <c>id</c> this reference points at.</summary>
    public required string IdRef { get; init; }

    /// <summary>The element itself.</summary>
    public required XElement Element { get; init; }
}

/// <summary>An EPUB 3 <c>&lt;meta refines="#id"&gt;</c> refinement.</summary>
public sealed record MetaRefinement
{
    /// <summary>The id being refined, with the leading <c>#</c> stripped.</summary>
    public required string Refines { get; init; }

    /// <summary>The property name — <c>file-as</c>, <c>role</c>, <c>group-position</c>.</summary>
    public required string Property { get; init; }

    /// <summary>The refinement's value.</summary>
    public required string Value { get; init; }

    /// <summary>The <c>scheme</c> attribute, such as <c>marc:relators</c>.</summary>
    public string? Scheme { get; init; }

    /// <summary>The element itself.</summary>
    public required XElement Element { get; init; }
}

/// <summary>
/// An EPUB package document (the OPF), parsed in a way that survives editing.
/// </summary>
public sealed partial class OpfDocument
{
    /// <summary>The OPF namespace.</summary>
    public static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";

    /// <summary>The Dublin Core elements namespace.</summary>
    public static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";

    private OpfDocument(
        XDocument document,
        byte[] originalBytes,
        XmlSourceFormat format,
        string entryName)
    {
        Document = document;
        OriginalBytes = originalBytes;
        Format = format;
        EntryName = entryName;
    }

    /// <summary>The parsed document.</summary>
    public XDocument Document { get; }

    /// <summary>
    /// The bytes exactly as read. Retained for the session so a repair edits the
    /// real file rather than a re-serialisation of it.
    /// </summary>
    public byte[] OriginalBytes { get; }

    /// <summary>What the bytes said about their own encoding.</summary>
    public XmlEncodingInfo Encoding => Format.Encoding;

    /// <summary>
    /// The XML declaration exactly as it appeared, or <see langword="null"/> if
    /// the document had none. Re-emitted verbatim on save.
    /// </summary>
    public string? DeclarationText => Format.DeclarationText;

    /// <summary>
    /// How the source was written, in the respects the parsed tree does not
    /// record — declaration, prolog, epilogue, empty-element style, line endings.
    /// </summary>
    internal XmlSourceFormat Format { get; }

    /// <summary>The container entry this document was read from.</summary>
    public string EntryName { get; }

    /// <summary>The <c>package</c> root element.</summary>
    public XElement? Package => Document.Root;

    /// <summary>The declared <c>package/@version</c>, such as <c>2.0</c> or <c>3.0</c>.</summary>
    public string? Version => (string?)Package?.Attribute("version");

    /// <summary>The <c>package/@unique-identifier</c>, naming a <c>dc:identifier</c>.</summary>
    public string? UniqueIdentifierRef => (string?)Package?.Attribute("unique-identifier");

    /// <summary>The <c>metadata</c> element.</summary>
    public XElement? Metadata => FindChild(Package, "metadata");

    /// <summary>The <c>manifest</c> element.</summary>
    public XElement? ManifestElement => FindChild(Package, "manifest");

    /// <summary>The <c>spine</c> element.</summary>
    public XElement? SpineElement => FindChild(Package, "spine");

    /// <summary>The manifest items, in document order.</summary>
    public IReadOnlyList<ManifestItem> Manifest => _manifest ??= ReadManifest();
    private List<ManifestItem>? _manifest;

    /// <summary>The spine references, in document order.</summary>
    public IReadOnlyList<SpineItemRef> Spine => _spine ??= ReadSpine();
    private List<SpineItemRef>? _spine;

    /// <summary>The EPUB 3 refinements, in document order.</summary>
    public IReadOnlyList<MetaRefinement> Refinements => _refinements ??= ReadRefinements();
    private List<MetaRefinement>? _refinements;

    /// <summary>Parses an OPF from its bytes.</summary>
    /// <param name="bytes">The document's bytes.</param>
    /// <param name="entryName">The container entry it came from, for diagnostics.</param>
    /// <returns>The parsed package document.</returns>
    /// <exception cref="BookFormatException">
    /// The document is not well-formed XML. Surfaced as EPUB-F001, and the point
    /// at which the repair path becomes relevant.
    /// </exception>
    public static OpfDocument Parse(ReadOnlySpan<byte> bytes, string entryName = "content.opf")
    {
        byte[] original = bytes.ToArray();
        XmlEncodingInfo encoding = XmlEncodingDetector.Detect(bytes);
        string text = XmlEncodingDetector.Decode(bytes, encoding);

        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new BookFormatException(
                $"'{entryName}' is not well-formed XML: {ex.Message}", entryName, ex);
        }

        // The declaration, the whitespace around the root, the empty-element
        // style and the line ending are all captured here rather than left to
        // the serialiser, because none of them survives in the parsed tree and
        // each would otherwise turn a one-field edit into a whole-file diff.
        return new OpfDocument(document, original, XmlSourceFormat.Detect(text, encoding), entryName);
    }

    private static XElement? FindChild(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private List<ManifestItem> ReadManifest()
    {
        if (ManifestElement is null)
        {
            return [];
        }

        return [.. ManifestElement
            .Elements()
            .Where(e => e.Name.LocalName == "item")
            .Select(e => new ManifestItem
            {
                Id = (string?)e.Attribute("id") ?? string.Empty,
                Href = (string?)e.Attribute("href") ?? string.Empty,
                MediaType = (string?)e.Attribute("media-type"),
                Properties = (string?)e.Attribute("properties"),
                Element = e,
            })];
    }

    private List<SpineItemRef> ReadSpine()
    {
        if (SpineElement is null)
        {
            return [];
        }

        return [.. SpineElement
            .Elements()
            .Where(e => e.Name.LocalName == "itemref")
            .Select(e => new SpineItemRef
            {
                IdRef = (string?)e.Attribute("idref") ?? string.Empty,
                Element = e,
            })];
    }

    private List<MetaRefinement> ReadRefinements()
    {
        if (Metadata is null)
        {
            return [];
        }

        var result = new List<MetaRefinement>();

        foreach (XElement meta in Metadata.Elements().Where(e => e.Name.LocalName == "meta"))
        {
            string? refines = (string?)meta.Attribute("refines");
            string? property = (string?)meta.Attribute("property");

            if (refines is null || property is null)
            {
                continue;
            }

            result.Add(new MetaRefinement
            {
                Refines = refines.TrimStart('#'),
                Property = property.Trim(),
                Value = meta.Value.Trim(),
                Scheme = (string?)meta.Attribute("scheme"),
                Element = meta,
            });
        }

        return result;
    }

    /// <summary>
    /// Reads the metadata, honouring both EPUB 2 and EPUB 3 conventions.
    /// </summary>
    /// <returns>The metadata found.</returns>
    public BookMetadata ReadMetadata()
    {
        var metadata = new BookMetadata();

        if (Metadata is null)
        {
            return metadata;
        }

        ILookup<string, MetaRefinement> refinements = Refinements.ToLookup(r => r.Refines, StringComparer.Ordinal);

        ReadTitles(metadata, refinements);
        ReadCreators(metadata, refinements);
        ReadSimpleFields(metadata);
        ReadIdentifiers(metadata);
        ReadSeries(metadata, refinements);
        ReadUnmappedMeta(metadata);

        return metadata;
    }

    private void ReadTitles(BookMetadata metadata, ILookup<string, MetaRefinement> refinements)
    {
        XElement? title = DcElements("title").FirstOrDefault();
        if (title is null)
        {
            return;
        }

        metadata.Title = title.Value.Trim();
        metadata.SortTitle = FileAsOf(title, refinements);
    }

    private void ReadCreators(BookMetadata metadata, ILookup<string, MetaRefinement> refinements)
    {
        foreach (XElement element in DcElements("creator"))
        {
            metadata.Creators.Add(ReadCreator(element, CreatorKind.Creator, refinements));
        }

        foreach (XElement element in DcElements("contributor"))
        {
            metadata.Creators.Add(ReadCreator(element, CreatorKind.Contributor, refinements));
        }
    }

    private static Creator ReadCreator(
        XElement element, CreatorKind kind, ILookup<string, MetaRefinement> refinements)
    {
        string? id = (string?)element.Attribute("id");

        // EPUB 2 puts role in an opf:role attribute; EPUB 3 in a refinement.
        // Read both, preferring whichever the file actually has.
        string? nativeRole = (string?)element.Attribute(OpfNs + "role");
        string? scheme = null;

        if (id is not null)
        {
            MetaRefinement? roleRefinement = refinements[id].FirstOrDefault(r => r.Property == "role");
            if (roleRefinement is not null)
            {
                nativeRole ??= roleRefinement.Value;
                scheme = roleRefinement.Scheme;
            }
        }

        return new Creator
        {
            Name = element.Value.Trim(),
            SortName = FileAsOf(element, refinements),
            NativeRole = nativeRole,
            // A role carried under marc:relators is already a relator code, so
            // it maps to itself. Anything else is a native string whose mapping
            // is the format's business, not the document's.
            Role = scheme is null or "marc:relators" ? nativeRole : null,
            Kind = kind,
            SourceId = id,
        };
    }

    private static string? FileAsOf(XElement element, ILookup<string, MetaRefinement> refinements)
    {
        string? fileAs = (string?)element.Attribute(OpfNs + "file-as");
        if (fileAs is not null)
        {
            return fileAs;
        }

        string? id = (string?)element.Attribute("id");
        return id is null
            ? null
            : refinements[id].FirstOrDefault(r => r.Property == "file-as")?.Value;
    }

    private void ReadSimpleFields(BookMetadata metadata)
    {
        metadata.Language = DcElements("language").FirstOrDefault()?.Value.Trim();
        metadata.Publisher = DcElements("publisher").FirstOrDefault()?.Value.Trim();
        metadata.Description = DcElements("description").FirstOrDefault()?.Value.Trim();
        metadata.Rights = DcElements("rights").FirstOrDefault()?.Value.Trim();

        foreach (XElement subject in DcElements("subject"))
        {
            string value = subject.Value.Trim();
            if (value.Length > 0)
            {
                metadata.Subjects.Add(value);
            }
        }

        foreach (XElement date in DcElements("date"))
        {
            // EPUB 2 distinguishes publication from creation with opf:event.
            string? evt = (string?)date.Attribute(OpfNs + "event");
            BookDate parsed = ParseDate(date.Value.Trim());

            switch (evt?.ToLowerInvariant())
            {
                case "creation":
                    metadata.CreationDate ??= parsed;
                    break;
                case "modification":
                    metadata.ModificationDate ??= parsed;
                    break;
                default:
                    metadata.PublicationDate ??= parsed;
                    break;
            }
        }

        // EPUB 3 states last-modified as dcterms:modified rather than dc:date.
        XElement? modifiedMeta = Metadata?
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta" &&
                                 (string?)e.Attribute("property") == "dcterms:modified");

        if (modifiedMeta is not null)
        {
            metadata.ModificationDate ??= ParseDate(modifiedMeta.Value.Trim());
        }
    }

    private void ReadIdentifiers(BookMetadata metadata)
    {
        string? uniqueRef = UniqueIdentifierRef;

        foreach (XElement element in DcElements("identifier"))
        {
            string? id = (string?)element.Attribute("id");
            string? scheme = (string?)element.Attribute(OpfNs + "scheme");

            metadata.Identifiers.Add(new Identifier
            {
                Value = element.Value.Trim(),
                Scheme = scheme,
                SourceId = id,
                IsUnique = id is not null && string.Equals(id, uniqueRef, StringComparison.Ordinal),
            });
        }
    }

    private void ReadSeries(BookMetadata metadata, ILookup<string, MetaRefinement> refinements)
    {
        // EPUB 3: belongs-to-collection, refined by collection-type and
        // group-position.
        XElement? collection = Metadata?
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta" &&
                                 (string?)e.Attribute("property") == "belongs-to-collection");

        if (collection is not null)
        {
            string? id = (string?)collection.Attribute("id");
            string? position = id is null
                ? null
                : refinements[id].FirstOrDefault(r => r.Property == "group-position")?.Value;

            metadata.Series = MakeSeries(collection.Value.Trim(), position);
            return;
        }

        // EPUB 2: calibre's convention, which is what most files actually use.
        string? name = LegacyMeta("calibre:series");
        if (name is not null)
        {
            metadata.Series = MakeSeries(name, LegacyMeta("calibre:series_index"));
        }
    }

    private static SeriesInfo MakeSeries(string name, string? index)
    {
        if (string.IsNullOrWhiteSpace(index))
        {
            return new SeriesInfo { Name = name };
        }

        return decimal.TryParse(index, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
            ? new SeriesInfo { Name = name, Index = parsed }
            : new SeriesInfo { Name = name, RawIndex = index };
    }

    /// <summary>
    /// Records <c>&lt;meta&gt;</c> elements that map onto no model field.
    /// </summary>
    private void ReadUnmappedMeta(BookMetadata metadata)
    {
        if (Metadata is null)
        {
            return;
        }

        foreach (XElement meta in Metadata.Elements().Where(e => e.Name.LocalName == "meta"))
        {
            string? name = (string?)meta.Attribute("name");
            string? property = (string?)meta.Attribute("property");
            string key = name ?? property ?? "meta";

            if (IsRecognisedMeta(name, property, meta))
            {
                continue;
            }

            var line = (IXmlLineInfo)meta;

            metadata.UnmappedFields.Add(new UnmappedField
            {
                Source = "OPF",
                Key = key,
                Text = name is not null ? (string?)meta.Attribute("content") : meta.Value.Trim(),
                Line = line.HasLineInfo() ? line.LineNumber : 0,
                Column = line.HasLineInfo() ? line.LinePosition : 0,
            });
        }
    }

    private static bool IsRecognisedMeta(string? name, string? property, XElement meta)
    {
        if (meta.Attribute("refines") is not null)
        {
            return true;
        }

        if (name is not null)
        {
            return name is "calibre:series" or "calibre:series_index" or "cover";
        }

        return property is "belongs-to-collection" or "dcterms:modified";
    }

    private string? LegacyMeta(string name) =>
        Metadata?
            .Elements()
            .Where(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == name)
            .Select(e => (string?)e.Attribute("content"))
            .FirstOrDefault(v => v is not null);

    private IEnumerable<XElement> DcElements(string localName) =>
        Metadata is null
            ? []
            : Metadata.Elements().Where(e =>
                e.Name.LocalName == localName &&
                (e.Name.Namespace == DcNs || e.Name.Namespace == XNamespace.None));

    /// <summary>
    /// Parses a book date, keeping the source text and recording how much of it
    /// was actually stated.
    /// </summary>
    /// <param name="raw">The date text as the source wrote it.</param>
    /// <returns>
    /// The parsed date. <see cref="BookDate.Raw"/> is always the input, and
    /// <see cref="BookDate.Precision"/> says how much of it was real, so a bare
    /// year is never silently promoted to a full calendar date.
    /// </returns>
    public static BookDate ParseDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new BookDate { Raw = raw, Precision = DatePrecision.Unknown };
        }

        string trimmed = raw.Trim();

        // Try most-specific first so precision is not overstated. A bare "2011"
        // must not come back as 1 January.
        (string Format, DatePrecision Precision)[] formats =
        [
            ("yyyy-MM-ddTHH:mm:ssK", DatePrecision.Time),
            ("yyyy-MM-ddTHH:mm:ss", DatePrecision.Time),
            ("yyyy-MM-dd", DatePrecision.Day),
            ("yyyy-MM", DatePrecision.Month),
            ("yyyy", DatePrecision.Year),
        ];

        foreach ((string format, DatePrecision precision) in formats)
        {
            if (DateTimeOffset.TryParseExact(
                    trimmed, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset value))
            {
                return new BookDate { Raw = trimmed, Value = value, Precision = precision };
            }
        }

        return DateTimeOffset.TryParse(
                trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset loose)
            ? new BookDate { Raw = trimmed, Value = loose, Precision = DatePrecision.Day }
            : new BookDate { Raw = trimmed, Precision = DatePrecision.Unknown };
    }
}
