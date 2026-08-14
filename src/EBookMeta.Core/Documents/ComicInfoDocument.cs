using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using EBookMeta.Model;

namespace EBookMeta.Documents;

/// <summary>
/// <c>ComicInfo.xml</c> — the ComicRack metadata document, parsed in a way that
/// survives editing.
/// </summary>
/// <remarks>
/// <para>
/// The de facto standard for comic archives, and the only one this build writes:
/// ComicTagger produces it, Komga and Kavita read it. CoMet and the ComicBookLover
/// blob are read elsewhere for cross-checking and otherwise left alone.
/// </para>
/// <para>
/// Loaded and saved on the same terms as <see cref="OpfDocument"/> — whitespace
/// preserved, formatting disabled, declaration verbatim, original bytes retained
/// for the session — because the reasons are the same and a comic tagged by
/// somebody else's tool deserves the same "one edit, one line of diff" treatment
/// as a book.
/// </para>
/// <para>
/// Unlike an OPF there are no namespaces to negotiate and no dual conventions to
/// write: the schema is a flat sequence of elements. The one thing that sequence
/// costs is insertion order — an element added at the end would be schema-invalid,
/// so a new element goes where the XSD says it goes.
/// </para>
/// </remarks>
public sealed class ComicInfoDocument
{
    /// <summary>The entry name the convention fixes, at the archive root.</summary>
    public const string DefaultEntryName = "ComicInfo.xml";

    /// <summary>
    /// The element order the ComicInfo schema requires.
    /// </summary>
    /// <remarks>
    /// <c>ComicInfo.xsd</c> declares an <c>xsd:sequence</c>, not an
    /// <c>xsd:all</c>, so order is part of validity. Most readers are lenient
    /// about it and validators are not, and a file this tool wrote should pass
    /// both. Anything absent from this list keeps its position relative to its
    /// neighbours and is never moved.
    /// </remarks>
    private static readonly string[] SchemaOrder =
    [
        "Title", "Series", "Number", "Count", "Volume",
        "AlternateSeries", "AlternateNumber", "AlternateCount",
        "Summary", "Notes", "Year", "Month", "Day",
        "Writer", "Penciller", "Inker", "Colorist", "Letterer", "CoverArtist", "Editor",
        "Translator", "Publisher", "Imprint", "Genre", "Tags", "Web", "PageCount",
        "LanguageISO", "Format", "BlackAndWhite", "Manga", "Characters", "Teams",
        "Locations", "ScanInformation", "StoryArc", "StoryArcNumber", "SeriesGroup",
        "AgeRating", "Pages", "CommunityRating", "MainCharacterOrTeam", "Review",
        "GTIN",
    ];

    /// <summary>
    /// The creator elements, in schema order, paired with the MARC relator each
    /// maps onto.
    /// </summary>
    /// <remarks>
    /// The mapping is lossy in one direction and that is accepted: several
    /// ComicInfo roles share a relator (a penciller and an inker are both
    /// <c>ill</c>), so the native element name is kept alongside the relator and
    /// preferred when writing back to a comic archive. Round-tripping through
    /// MARC alone would turn every inker into a penciller.
    /// </remarks>
    private static readonly (string Element, string Relator)[] CreatorElements =
    [
        ("Writer", "aut"),
        ("Penciller", "ill"),
        ("Inker", "ill"),
        ("Colorist", "clr"),
        ("Letterer", "ltr"),
        ("CoverArtist", "cov"),
        ("Editor", "edt"),
        ("Translator", "trl"),
    ];

    private readonly bool _created;

    private ComicInfoDocument(
        XDocument document,
        byte[] originalBytes,
        XmlSourceFormat format,
        string entryName,
        string indent,
        bool created)
    {
        Document = document;
        OriginalBytes = originalBytes;
        Format = format;
        EntryName = entryName;
        Indent = indent;
        _created = created;
    }

    /// <summary>The parsed document.</summary>
    public XDocument Document { get; }

    /// <summary>
    /// The bytes exactly as read, or as generated for a document this build
    /// created. Retained for the session, like an OPF's.
    /// </summary>
    public byte[] OriginalBytes { get; }

    /// <summary>What the bytes said about their own encoding.</summary>
    public XmlEncodingInfo Encoding => Format.Encoding;

    /// <summary>The XML declaration exactly as it appeared, re-emitted verbatim.</summary>
    public string? DeclarationText => Format.DeclarationText;

    /// <summary>The container entry this document was read from.</summary>
    public string EntryName { get; }

    /// <summary>The <c>ComicInfo</c> root element.</summary>
    public XElement? Root => Document.Root;

    internal XmlSourceFormat Format { get; }

    /// <summary>The whitespace that separates the root's children.</summary>
    private string Indent { get; }

    /// <summary>The declared <c>PageCount</c>, or null when absent or unparseable.</summary>
    /// <remarks>Compared against the archive's actual image count by rule CBZ-E020.</remarks>
    public int? PageCount =>
        int.TryParse(Value("PageCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            ? count
            : null;

    /// <summary>
    /// The <c>Image</c> index of every <c>&lt;Page&gt;</c> element, in document
    /// order; null for one whose index is missing or unparseable.
    /// </summary>
    /// <remarks>Compared against the archive's images by rule CBZ-W021.</remarks>
    public IReadOnlyList<int?> PageImageIndexes =>
        FindChild("Pages") is not { } pages
            ? []
            : [.. pages
                .Elements()
                .Where(e => e.Name.LocalName == "Page")
                .Select(e => int.TryParse(
                    (string?)e.Attribute("Image"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int index)
                        ? index
                        : (int?)null)];

    /// <summary>Returns the text of a top-level element, trimmed.</summary>
    /// <param name="elementName">The element's local name.</param>
    /// <returns>The value, or <see langword="null"/> when the element is absent.</returns>
    /// <remarks>
    /// Exposed so validation rules can read fields the model does not carry —
    /// <c>Year</c> and <c>Month</c> separately for CBZ-W031, for instance — without
    /// this class growing a property per rule.
    /// </remarks>
    public string? Value(string elementName)
    {
        Throw.IfNullOrEmpty(elementName);

        string? value = FindChild(elementName)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Parses a <c>ComicInfo.xml</c> from its bytes.</summary>
    /// <param name="bytes">The document's bytes.</param>
    /// <param name="entryName">The container entry it came from, for diagnostics.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="BookFormatException">
    /// The document is not well-formed XML, or its root is not <c>ComicInfo</c>.
    /// Surfaced as CBZ-F001.
    /// </exception>
    public static ComicInfoDocument Parse(ReadOnlySpan<byte> bytes, string entryName = DefaultEntryName)
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

        if (document.Root is not { } root || root.Name.LocalName != "ComicInfo")
        {
            throw new BookFormatException(
                $"'{entryName}' is XML but its root element is "
                + $"'{document.Root?.Name.LocalName ?? "(none)"}', not 'ComicInfo'.",
                entryName);
        }

        return new ComicInfoDocument(
            document,
            original,
            XmlSourceFormat.Detect(text, encoding),
            entryName,
            DetectIndent(root),
            created: false);
    }

    /// <summary>
    /// Creates an empty document, for a comic archive that carries no metadata.
    /// </summary>
    /// <param name="entryName">The entry name it will be written as.</param>
    /// <returns>A document with a bare <c>ComicInfo</c> root.</returns>
    /// <remarks>
    /// Most comics in a collection have no <c>ComicInfo.xml</c> at all — that is
    /// CBZ-W010 and the ordinary case, not a defect. The two schema-instance
    /// namespace declarations are what ComicTagger writes, so a file this build
    /// creates is shaped like the ones everything else produces.
    /// </remarks>
    public static ComicInfoDocument CreateEmpty(string entryName = DefaultEntryName)
    {
        Throw.IfNullOrEmpty(entryName);

        var root = new XElement(
            "ComicInfo",
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
            new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"));

        var utf8 = new XmlEncodingInfo
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            DeclaredName = "utf-8",
        };

        // CRLF, because this file is created on Windows by a Windows utility and
        // that is what every other tool in the neighbourhood writes. The indent
        // stays LF: text nodes go through the tree, and the tree is where
        // serialisation translates line endings, so putting CRLF in one here
        // would come back out as CR CRLF.
        return new ComicInfoDocument(
            new XDocument(root),
            [],
            XmlSourceFormat.ForNewDocument(utf8, "<?xml version=\"1.0\" encoding=\"utf-8\"?>", "\r\n"),
            entryName,
            "\n  ",
            created: true);
    }

    /// <summary>Serialises the document back to bytes.</summary>
    /// <returns>The complete <c>ComicInfo.xml</c>.</returns>
    public byte[] Serialize() => Format.Compose(Document.Root);

    /// <summary>Reads the metadata this document carries.</summary>
    /// <returns>The metadata found.</returns>
    public BookMetadata ReadMetadata()
    {
        var metadata = new BookMetadata();

        if (Root is null)
        {
            return metadata;
        }

        metadata.Title = Value("Title");
        metadata.Description = Value("Summary");
        metadata.Publisher = Value("Publisher");
        metadata.Language = Value("LanguageISO");
        metadata.PublicationDate = ReadDate();

        ReadSeries(metadata);
        ReadCreators(metadata);
        ReadSubjects(metadata);
        ReadUnmapped(metadata);

        return metadata;
    }

    private void ReadSeries(BookMetadata metadata)
    {
        string? name = Value("Series");
        if (name is null)
        {
            return;
        }

        string? number = Value("Number");

        if (number is null)
        {
            metadata.Series = new SeriesInfo { Name = name };
            return;
        }

        metadata.Series = decimal.TryParse(
                number, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal index)
            ? new SeriesInfo { Name = name, Index = index }
            : new SeriesInfo { Name = name, RawIndex = number };
    }

    /// <summary>
    /// Reads the creator elements, keeping each one's native role.
    /// </summary>
    /// <remarks>
    /// ComicRack stores several names in one element, comma-separated, which is
    /// what every tool that writes these files does. The writer is the primary
    /// creator and the rest are contributors, mirroring <c>dc:creator</c> against
    /// <c>dc:contributor</c>, so "authors" means the same thing in both formats.
    /// </remarks>
    private void ReadCreators(BookMetadata metadata)
    {
        foreach ((string element, string relator) in CreatorElements)
        {
            if (Value(element) is not { } value)
            {
                continue;
            }

            foreach (string name in SplitList(value))
            {
                metadata.Creators.Add(new Creator
                {
                    Name = name,
                    NativeRole = element,
                    Role = relator,
                    Kind = element == "Writer" ? CreatorKind.Creator : CreatorKind.Contributor,
                });
            }
        }
    }

    /// <summary>
    /// Reads <c>Genre</c> as the subject list.
    /// </summary>
    /// <remarks>
    /// <c>Genre</c> only, deliberately, even though ComicInfo 2.0 also has
    /// <c>Tags</c>. Reading both would merge two fields into one list that cannot
    /// be split back apart on save, so editing subjects would quietly move a tag
    /// into the genres. <c>Tags</c> is left untouched instead, and reported as an
    /// unmapped field.
    /// </remarks>
    private void ReadSubjects(BookMetadata metadata)
    {
        if (Value("Genre") is not { } genre)
        {
            return;
        }

        foreach (string subject in SplitList(genre))
        {
            metadata.Subjects.Add(subject);
        }
    }

    private BookDate? ReadDate()
    {
        string? year = Value("Year");
        if (year is null)
        {
            return null;
        }

        string? month = Value("Month");
        string? day = Value("Day");

        // Composed as ISO rather than kept as three fields: Raw is what the editor
        // shows and what a later save writes back, so it has to be a date a person
        // recognises. Precision records how much of it the file actually claimed,
        // so a bare year is never promoted to a January date.
        if (month is null)
        {
            return OpfDocument.ParseDate(year);
        }

        string composed = day is null
            ? $"{Pad(year, 4)}-{Pad(month, 2)}"
            : $"{Pad(year, 4)}-{Pad(month, 2)}-{Pad(day, 2)}";

        return OpfDocument.ParseDate(composed);
    }

    private static string Pad(string value, int width) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')
            : value;

    /// <summary>
    /// Records elements that map onto no model field.
    /// </summary>
    /// <remarks>
    /// Informational only, exactly as for an OPF: preservation happens by leaving
    /// the element alone on write, not by writing anything here back. That is what
    /// lets a <c>Notes</c> or a whole <c>&lt;Pages&gt;</c> block survive a save
    /// without being reformatted.
    /// </remarks>
    private void ReadUnmapped(BookMetadata metadata)
    {
        foreach (XElement element in Root!.Elements())
        {
            string name = element.Name.LocalName;

            if (IsMapped(name))
            {
                continue;
            }

            var line = (IXmlLineInfo)element;

            metadata.UnmappedFields.Add(new UnmappedField
            {
                Source = "ComicInfo",
                Key = name,
                Text = element.HasElements ? null : element.Value.Trim(),
                Line = line.HasLineInfo() ? line.LineNumber : 0,
                Column = line.HasLineInfo() ? line.LinePosition : 0,
            });
        }
    }

    private static bool IsMapped(string elementName) =>
        elementName is "Title" or "Series" or "Number" or "Summary" or "Publisher"
            or "Genre" or "LanguageISO" or "Year" or "Month" or "Day" ||
        CreatorElements.Any(c => c.Element == elementName);

    /// <summary>
    /// Applies metadata to the document, touching only what changed.
    /// </summary>
    /// <param name="metadata">The metadata to write.</param>
    /// <remarks>
    /// Compared field by field against the document as it currently stands, so a
    /// field the user did not edit contributes nothing to the diff and an unedited
    /// save reproduces the file byte for byte. Anything this method does not
    /// recognise is never touched, which is how <c>Notes</c>, <c>Volume</c> and the
    /// <c>&lt;Pages&gt;</c> block survive intact.
    /// </remarks>
    public void ApplyMetadata(BookMetadata metadata)
    {
        Throw.IfNull(metadata);

        XElement root = Root
            ?? throw new BookFormatException($"'{EntryName}' has no ComicInfo element.", EntryName);

        BookMetadata current = ReadMetadata();

        SetElement(root, "Title", current.Title, metadata.Title);
        SetElement(root, "Summary", current.Description, metadata.Description);
        SetElement(root, "Publisher", current.Publisher, metadata.Publisher);
        SetElement(root, "LanguageISO", current.Language, metadata.Language);

        ApplySeries(root, current, metadata);
        ApplyDate(root, current, metadata);
        ApplySubjects(root, current, metadata);
        ApplyCreators(root, current, metadata);

        // A document this build created has no trailing whitespace to inherit, so
        // its closing tag would end up on the same line as the last field. Only
        // for a created document: adding one to a parsed file would change bytes
        // the user did not ask to change.
        if (_created && root.HasElements && root.LastNode is not XText)
        {
            root.Add(new XText("\n"));
        }
    }

    /// <summary>
    /// Records the page count on a document that does not state one.
    /// </summary>
    /// <param name="pageCount">The number of images in the archive.</param>
    /// <remarks>
    /// Only ever fills a gap. An existing <c>PageCount</c> is left alone even when
    /// it disagrees with the archive, because that disagreement is rule CBZ-E020's
    /// to report and the user's to fix — silently correcting it would hide a file
    /// that is missing pages.
    /// </remarks>
    public void SetPageCountIfAbsent(int pageCount)
    {
        if (Root is not { } root || Value("PageCount") is not null)
        {
            return;
        }

        SetElement(root, "PageCount", null, pageCount.ToString(CultureInfo.InvariantCulture));
    }

    private void ApplySeries(XElement root, BookMetadata current, BookMetadata metadata)
    {
        SeriesInfo? series = metadata.Series;

        string? number = series?.Index is { } value
            // Invariant culture: a French locale would write "2,5", which no
            // reader parses.
            ? value.ToString("0.############", CultureInfo.InvariantCulture)
            : series?.RawIndex;

        string? currentNumber = current.Series?.Index is { } currentValue
            ? currentValue.ToString("0.############", CultureInfo.InvariantCulture)
            : current.Series?.RawIndex;

        SetElement(root, "Series", current.Series?.Name, series?.Name);
        SetElement(root, "Number", currentNumber, number);
    }

    private void ApplyDate(XElement root, BookMetadata current, BookMetadata metadata)
    {
        if (Same(current.PublicationDate?.Raw, metadata.PublicationDate?.Raw))
        {
            return;
        }

        (string? year, string? month, string? day) = SplitDate(metadata.PublicationDate);

        SetElement(root, "Year", Value("Year"), year);
        SetElement(root, "Month", Value("Month"), month);
        SetElement(root, "Day", Value("Day"), day);
    }

    /// <summary>
    /// Splits a date into the three elements ComicInfo stores it as, stating no
    /// more than the source did.
    /// </summary>
    private static (string? Year, string? Month, string? Day) SplitDate(BookDate? date)
    {
        if (date is null || string.IsNullOrWhiteSpace(date.Raw))
        {
            return (null, null, null);
        }

        if (date.Value is not { } value)
        {
            // Unparseable, so the only honest thing to store is a leading year if
            // there is one. Writing the whole raw string into <Year> would put
            // "circa 1989" in a field the schema says is an integer.
            string digits = new(date.Raw.Trim().TakeWhile(char.IsDigit).ToArray());
            return digits.Length == 4 ? (digits, null, null) : (null, null, null);
        }

        string y = value.Year.ToString(CultureInfo.InvariantCulture);
        string m = value.Month.ToString(CultureInfo.InvariantCulture);
        string d = value.Day.ToString(CultureInfo.InvariantCulture);

        return date.Precision switch
        {
            DatePrecision.Year => (y, null, null),
            DatePrecision.Month => (y, m, null),
            _ => (y, m, d),
        };
    }

    private void ApplySubjects(XElement root, BookMetadata current, BookMetadata metadata)
    {
        if (current.Subjects.SequenceEqual(metadata.Subjects, StringComparer.Ordinal))
        {
            return;
        }

        SetElement(
            root,
            "Genre",
            Value("Genre"),
            metadata.Subjects.Count == 0 ? null : string.Join(", ", metadata.Subjects));
    }

    /// <summary>
    /// Writes the creator elements, grouping names by the role they carry.
    /// </summary>
    /// <remarks>
    /// The native role wins over the mapped relator, so a name read out of
    /// <c>Inker</c> goes back into <c>Inker</c> rather than into <c>Penciller</c>
    /// merely because both mean <c>ill</c>. A creator carrying neither a role this
    /// schema has nor one the relator table maps is reported rather than filed
    /// under a guess.
    /// </remarks>
    private void ApplyCreators(XElement root, BookMetadata current, BookMetadata metadata)
    {
        if (SameCreators(current.Creators, metadata.Creators))
        {
            return;
        }

        var byElement = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (Creator creator in metadata.Creators)
        {
            if (ResolveCreatorElement(creator) is not { } element)
            {
                Log.Warning(
                    $"'{creator.Name}' has role '{creator.NativeRole ?? creator.Role ?? "(none)"}', "
                    + "which ComicInfo has no element for, so it was not written.");
                continue;
            }

            if (!byElement.TryGetValue(element, out List<string>? names))
            {
                names = [];
                byElement[element] = names;
            }

            names.Add(creator.Name);
        }

        foreach ((string element, _) in CreatorElements)
        {
            byElement.TryGetValue(element, out List<string>? names);

            SetElement(
                root,
                element,
                Value(element),
                names is null || names.Count == 0 ? null : string.Join(", ", names));
        }
    }

    private static string? ResolveCreatorElement(Creator creator)
    {
        if (creator.NativeRole is { } native)
        {
            foreach ((string element, _) in CreatorElements)
            {
                if (string.Equals(element, native, StringComparison.OrdinalIgnoreCase))
                {
                    return element;
                }
            }
        }

        string? role = creator.Role ?? creator.NativeRole;

        foreach ((string element, string relator) in CreatorElements)
        {
            if (string.Equals(relator, role, StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }
        }

        // A name typed into an "authors" box arrives with no usable role at all.
        // Writer is not a guess there: it is what the schema calls the person a
        // comic is credited to.
        return creator.Kind == CreatorKind.Creator ? "Writer" : null;
    }

    private static bool SameCreators(IList<Creator> a, IList<Creator> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!Same(a[i].Name, b[i].Name) ||
                !Same(a[i].NativeRole ?? a[i].Role, b[i].NativeRole ?? b[i].Role))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates, updates or removes a top-level element, leaving it untouched when
    /// its value is already what is wanted.
    /// </summary>
    private void SetElement(XElement root, string name, string? currentValue, string? value)
    {
        string? wanted = string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        if (Same(currentValue, wanted))
        {
            return;
        }

        XElement? element = FindChild(name);

        if (wanted is null)
        {
            if (element is not null)
            {
                RemoveWithWhitespace(element);
            }

            return;
        }

        if (element is null)
        {
            element = new XElement(name);
            InsertInSchemaOrder(root, element);
        }

        if (!string.Equals(element.Value, wanted, StringComparison.Ordinal))
        {
            element.SetValue(wanted);
        }
    }

    /// <summary>
    /// Inserts a new element at the position the schema sequence gives it.
    /// </summary>
    /// <remarks>
    /// Appending would be simpler and would produce a file that fails schema
    /// validation, since <c>ComicInfo.xsd</c> is a sequence. Elements this build
    /// does not know are treated as anchors and never moved, so an unrecognised
    /// element keeps its neighbours.
    /// </remarks>
    private void InsertInSchemaOrder(XElement root, XElement element)
    {
        int position = SchemaPosition(element.Name.LocalName);

        XElement? successor = root
            .Elements()
            .FirstOrDefault(e => SchemaPosition(e.Name.LocalName) > position);

        if (successor is null)
        {
            root.Add(new XText(Indent), element);
            return;
        }

        // The new element takes over the whitespace that preceded its successor,
        // and the successor gets a fresh separator, so the indentation reads the
        // same as if the file had always had both.
        successor.AddBeforeSelf(element);
        successor.AddBeforeSelf(new XText(Indent));
    }

    private static int SchemaPosition(string elementName)
    {
        int index = Array.IndexOf(SchemaOrder, elementName);

        // Unknown elements sort last so a known one is never inserted after them,
        // which keeps the recognised prefix of the document in schema order.
        return index < 0 ? int.MaxValue - 1 : index;
    }

    private XElement? FindChild(string localName) =>
        Root?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static void RemoveWithWhitespace(XElement element)
    {
        // Take the whitespace that preceded the element too, otherwise deleting a
        // field leaves a blank line behind and the diff shows two changes.
        if (element.PreviousNode is XText text && text.Value.Trim().Length == 0)
        {
            text.Remove();
        }

        element.Remove();
    }

    private static string DetectIndent(XElement root)
    {
        // The whitespace before the first child is the document's own indentation
        // style, whatever it happens to be. Guessing two spaces instead would make
        // a generated element visibly foreign in a file that uses tabs.
        if (root.FirstNode is XText text && text.Value.Contains('\n'))
        {
            return text.Value;
        }

        return "\n  ";
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>
    /// Splits a comma-separated ComicRack list, which is how the format stores
    /// several names or genres in one element.
    /// </summary>
    private static IEnumerable<string> SplitList(string value) =>
        value.Split(',')
             .Select(part => part.Trim())
             .Where(part => part.Length > 0);
}
