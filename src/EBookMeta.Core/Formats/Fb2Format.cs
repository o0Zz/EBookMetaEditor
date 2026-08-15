using EBookMeta.Xml;
using EBookMeta.Model;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using System.Xml;

namespace EBookMeta.Formats;

/// <summary>
/// Reads and writes FictionBook metadata: a single XML file, or one inside a ZIP.
/// </summary>
/// <remarks>
/// The two flavours are the same document in different containers, so one
/// implementation is registered twice — <see cref="FormatId.Fb2"/> over a
/// <c>RawContainer</c> and <see cref="FormatId.Fb2Zip"/> over a
/// <c>ZipContainer</c>. Which one a file gets is <c>BookContainers</c>'s decision;
/// nothing here names a container.
/// </remarks>
public sealed partial class Fb2Format : IBookFormat
{
    /// <summary>Creates the format for one flavour of FictionBook.</summary>
    /// <param name="id">
    /// <see cref="FormatId.Fb2"/> for a bare document, <see cref="FormatId.Fb2Zip"/>
    /// for one inside a ZIP.
    /// </param>
    public Fb2Format(FormatId id = FormatId.Fb2)
    {
        Id = id;

        Capabilities = new FormatCapabilities
        {
            Format = id,

            // No sort title and no rights statement: FictionBook has neither, and
            // a box the user can type into whose content is discarded on save is
            // exactly what declaring capabilities exists to prevent. Per-creator
            // sort names are absent for the same reason — FB2 splits a name into
            // parts but has no separate sort form.
            ReadableFields =
                MetadataField.Title | MetadataField.Creators | MetadataField.CreatorRoles |
                MetadataField.Series | MetadataField.SeriesIndex | MetadataField.Description |
                MetadataField.Publisher | MetadataField.PublicationDate | MetadataField.Language |
                MetadataField.Subjects | MetadataField.Identifiers | MetadataField.Cover,

            // Everything readable except the cover and the identifiers. A cover
            // lives in a base64 <binary> at the far end of the file, and replacing
            // it would mean rewriting the part of the document this format
            // deliberately never parses.
            WritableFields =
                MetadataField.Title | MetadataField.Creators | MetadataField.CreatorRoles |
                MetadataField.Series | MetadataField.SeriesIndex | MetadataField.Description |
                MetadataField.Publisher | MetadataField.PublicationDate | MetadataField.Language |
                MetadataField.Subjects,
        };

        // .fb2.zip is deliberately absent. SystemFileAssociations keys on a single
        // extension, so registering it would mean claiming ".zip" and putting this
        // app's verb on every archive on the machine.
        Extensions = id == FormatId.Fb2Zip ? [] : [".fb2"];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; }

    /// <inheritdoc />
    /// <remarks>
    /// The two instances split by container: a bare document is sniffed from its
    /// root element, a zipped one from an entry name.
    /// <para>
    /// FB2 is the one supported format with no magic number — it is an ordinary XML
    /// file, and <c>&lt;FictionBook&gt;</c> is the only thing that distinguishes it.
    /// The search is bounded and gated on the file starting with an angle bracket,
    /// so it costs nothing for everything that is not XML.
    /// </para>
    /// </remarks>
    public FormatClaim? TryOpen(BookSource source)
    {
        Throw.IfNull(source);

        if (Id == FormatId.Fb2Zip)
        {
            if (source.ContainerKind != ContainerKind.Zip)
            {
                return null;
            }

            foreach (ContainerEntry entry in source.Container.Entries)
            {
                if (entry.Name.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
                {
                    return new FormatClaim
                    {
                        Format = FormatId.Fb2Zip,
                        Detail = "archive contains a FictionBook document",
                        Confidence = MatchConfidence.Strong,
                    };
                }
            }

            return null;
        }

        // Answered from the head rather than the container, so a file that is not
        // FictionBook is declined without a RawContainer ever being opened for it.
        if (source.ContainerKind != ContainerKind.Raw)
        {
            return null;
        }

        return source.LeadingText().Contains("<FictionBook", StringComparison.Ordinal)
            ? new FormatClaim
            {
                Format = FormatId.Fb2,
                Detail = "FictionBook root element",
                Confidence = MatchConfidence.Certain,
            }
            : null;
    }

    /// <inheritdoc />
    public FormatId Id { get; }

    /// <inheritdoc />
    public FormatCapabilities Capabilities { get; }

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// The document is not well-formed XML (FB2-F001), or carries no
    /// <c>&lt;description&gt;</c> (FB2-F002).
    /// </exception>
    public BookMetadata Read(
        IContainer container, ReadOptions? options = null)
    {
        Throw.IfNull(container);

        options ??= ReadOptions.Default;

        ContainerEntry entry = FindDocument(container);
        Fb2Document document = Parse(container, entry);
        BookMetadata metadata = document.ReadMetadata();

        if (options.IncludeCover)
        {
            ReadCover(container, entry, document, metadata);
        }

        Log.Info(
            $"Read FictionBook metadata from '{entry.Name}': "
            + $"title={Log.Describe(metadata.Title)}, creators={metadata.Creators.Count}, "
            + $"series={Log.Describe(metadata.Series?.Name)}.");

        return metadata;
    }

    /// <inheritdoc />
    public void Write(
        IContainer container,
        BookMetadata metadata,
        string targetPath)
    {
        Throw.IfNull(container);
        Throw.IfNull(metadata);
        Throw.IfNullOrEmpty(targetPath);

        ContainerEntry entry = FindDocument(container);
        Fb2Document document = Parse(container, entry);

        document.ApplyMetadata(metadata);

        byte[] bytes = document.Serialize();

        var entries = new List<PendingEntry>(container.Entries.Count);

        foreach (ContainerEntry existing in container.Entries)
        {
            entries.Add(existing.Index == entry.Index
                ? PendingEntry.Replacing(existing, bytes)
                : PendingEntry.CopyOf(container, existing));
        }

        container.Rebuild(entries, targetPath);

        Log.Info($"Wrote FictionBook metadata to '{entry.Name}' ({bytes.Length} bytes).");
    }

    /// <summary>
    /// Finds the FictionBook document — the only entry of a bare file, or the
    /// <c>.fb2</c> inside a ZIP.
    /// </summary>
    /// <exception cref="BookFormatException">The archive holds no FB2 document.</exception>
    private static ContainerEntry FindDocument(
        IContainer container)
    {
        List<ContainerEntry> candidates = [.. container.Entries
            .Where(e => !e.IsDirectory &&
                e.Name.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))];

        if (candidates.Count == 0)
        {
            // A bare .fb2 opened through RawContainer has one entry named after the
            // file, which need not end in .fb2 — the extension is the user's, not
            // the format's.
            List<ContainerEntry> files = [.. container.Entries.Where(e => !e.IsDirectory)];

            if (files.Count == 1)
            {
                return files[0];
            }

            throw new BookFormatException(
                "This archive contains no FictionBook document.", path: null);
        }

        return candidates[0];
    }

    /// <summary>
    /// Parses the document, reporting FB2-F001 or FB2-F002 before giving up.
    /// </summary>
    private static Fb2Document Parse(
        IContainer container, ContainerEntry entry)
    {
        try
        {
            return Fb2Document.Parse(container.ReadAllBytes(entry), entry.Name);
        }
        catch (BookFormatException ex)
        {
            Log.Rule(
                LogLevel.Error,
                ex.Message.Contains("<description>", StringComparison.Ordinal)
                    ? "FB2-F002"
                    : "FB2-F001",
                ex.Message,
                entry.Name);
            throw;
        }
    }

    /// <summary>
    /// Pulls the cover out of the <c>&lt;binary&gt;</c> the cover page points at.
    /// </summary>
    /// <remarks>
    /// A streaming pass over the document, because the binaries sit past the body
    /// and the parsed part of the file stops at <c>&lt;/description&gt;</c>. Only
    /// done when a cover was asked for: the batch grid reads three hundred books
    /// with <c>ReadOptions.WithoutCover</c> and never walks a single one of them.
    /// </remarks>
    private static void ReadCover(
        IContainer container,
        ContainerEntry entry,
        Fb2Document document,
        BookMetadata metadata)
    {
        if (document.CoverImageId() is not { } id)
        {
            return;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = true,
            CheckCharacters = false,
        };

        try
        {
            using Stream stream = container.OpenRead(entry);
            using XmlReader reader = XmlReader.Create(stream, settings);

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "binary")
                {
                    continue;
                }

                if (reader.GetAttribute("id") != id)
                {
                    continue;
                }

                string mediaType = reader.GetAttribute("content-type") ?? "image/jpeg";
                string base64 = reader.ReadElementContentAsString();

                metadata.Cover = new CoverImage
                {
                    Data = Convert.FromBase64String(base64.Trim()),
                    MediaType = mediaType,
                    SourceManifestId = id,
                };

                return;
            }
        }
        catch (Exception ex) when (ex is XmlException or FormatException)
        {
            // A cover that will not decode is not a reason to refuse the file: the
            // metadata is all still readable, and only the image is lost.
            Log.Debug($"The cover image '{id}' in '{entry.Name}' could not be decoded: {ex.Message}");
        }
    }

}

/// <summary>
/// A FictionBook document, of which only <c>&lt;description&gt;</c> is parsed.
/// </summary>
/// <remarks>
/// FB2 puts the metadata and the whole book in one XML file, often with every
/// illustration base64-encoded into it, so a ten-megabyte file is ordinary. Parsing
/// all of that to change a title would cost the startup budget several times over,
/// and re-serialising it would rewrite every line of a book the user never touched.
/// <para>
/// So this is hard invariant 16 applied to a whole document: the
/// <c>&lt;description&gt;</c> element is located, parsed and edited, and on save its
/// serialised form is spliced back into the original text at the offsets it came
/// from. Everything outside that span — the body, the binaries, the trailing
/// newline — is the original characters, copied through. Byte-identity for an
/// unedited save is a property of the design rather than of careful serialisation.
/// </para>
/// </remarks>
public sealed class Fb2Document
{
    private readonly string _text;
    private readonly int _descriptionStart;
    private readonly int _descriptionEnd;

    /// <summary>
    /// The parsed description. Its parent is a throwaway element carrying the
    /// root's namespace declarations, which is what makes prefixes resolve the way
    /// they did in the file and stops them being re-declared on the way out.
    /// </summary>
    private readonly XElement _description;
    private readonly XmlEncodingInfo _encoding;
    private readonly bool _selfClosingHasSpace;
    private readonly string _newLine;
    private readonly string _indent;

    private Fb2Document(
        string text,
        int descriptionStart,
        int descriptionEnd,
        XElement description,
        XmlEncodingInfo encoding,
        byte[] originalBytes,
        string entryName)
    {
        _text = text;
        _descriptionStart = descriptionStart;
        _descriptionEnd = descriptionEnd;
        _description = description;
        _encoding = encoding;

        string span = text.Substring(descriptionStart, descriptionEnd - descriptionStart);
        _selfClosingHasSpace = span.Contains(" />", StringComparison.Ordinal);
        _newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _indent = DetectIndent(description);

        OriginalBytes = originalBytes;
        EntryName = entryName;
    }

    /// <summary>The document's bytes exactly as read.</summary>
    public byte[] OriginalBytes { get; }

    /// <summary>The container entry this came from, for diagnostics.</summary>
    public string EntryName { get; }

    /// <summary>What the bytes said about their own encoding.</summary>
    public XmlEncodingInfo Encoding => _encoding;

    /// <summary>The parsed <c>&lt;description&gt;</c> element.</summary>
    public XElement Description => _description;

    /// <summary>Parses a FictionBook document from its bytes.</summary>
    /// <param name="bytes">The document's bytes.</param>
    /// <param name="entryName">The container entry it came from, for diagnostics.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="BookFormatException">
    /// The document is not well-formed XML, its root is not <c>FictionBook</c>, or
    /// it has no <c>&lt;description&gt;</c>. Surfaced as FB2-F001 and FB2-F002.
    /// </exception>
    public static Fb2Document Parse(ReadOnlySpan<byte> bytes, string entryName)
    {
        Throw.IfNullOrEmpty(entryName);

        byte[] original = bytes.ToArray();
        XmlEncodingInfo encoding = XmlEncodingDetector.Detect(bytes);
        string text = XmlEncodingDetector.Decode(bytes, encoding);

        (int start, int end, XElement description) = Locate(text, entryName);

        return new Fb2Document(text, start, end, description, encoding, original, entryName);
    }

    /// <summary>
    /// Finds the <c>&lt;description&gt;</c> span and reconstructs the namespace
    /// scope it sat in.
    /// </summary>
    private static (int Start, int End, XElement Description) Locate(string text, string entryName)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = false,
            CheckCharacters = false,
        };

        using var reader = XmlReader.Create(new StringReader(text), settings);
        var lineInfo = (IXmlLineInfo)reader;
        int[] lineStarts = XmlLineIndex.Starts(text);

        string? rootName = null;
        var declarations = new List<(string Prefix, string Uri)>();
        int depth = -1;

        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (rootName is null)
                {
                    rootName = reader.Name;
                    depth = reader.Depth;
                    CollectNamespaceDeclarations(reader, declarations);

                    if (reader.LocalName != "FictionBook")
                    {
                        throw new BookFormatException(
                            $"'{entryName}' is XML but its root element is "
                            + $"'{reader.LocalName}', not 'FictionBook'.",
                            entryName);
                    }

                    continue;
                }

                if (reader.Depth != depth + 1 || reader.LocalName != "description")
                {
                    continue;
                }

                // LinePosition points at the first character of the name, so the
                // '<' is one before it.
                int start = XmlLineIndex.Offset(lineStarts, lineInfo) - 1;
                string name = reader.Name;
                bool empty = reader.IsEmptyElement;

                if (start < 0 ||
                    !text.Substring(start).StartsWith("<" + name, StringComparison.Ordinal))
                {
                    throw new BookFormatException(
                        $"'{entryName}' could not be read: the description element was not "
                        + "where the parser said it was.",
                        entryName);
                }

                // An empty <description/> ends at its own '>'; anything else ends
                // at the matching close tag.
                int end = empty
                    ? text.IndexOf('>', start) + 1
                    : FindCloseTagEnd(text, start, name, entryName);

                XElement wrapper = XElement.Parse(
                    Wrap(declarations, text.Substring(start, end - start)),
                    LoadOptions.PreserveWhitespace);

                return (start, end, wrapper.Elements().First());
            }
        }
        catch (XmlException ex)
        {
            throw new BookFormatException(
                $"'{entryName}' is not well-formed XML: {ex.Message}", entryName, ex);
        }

        throw new BookFormatException(
            rootName is null
                ? $"'{entryName}' contains no XML elements."
                : $"'{entryName}' has no <description> element, so it carries no metadata.",
            entryName);
    }

    /// <summary>
    /// Wraps the description text in an element carrying the root's namespace
    /// declarations, so prefixes resolve as they did in the file.
    /// </summary>
    /// <remarks>
    /// The wrapper is never serialised. Its only job is to hold the declarations
    /// in scope above the description, which is what stops
    /// <see cref="XmlExactWriter"/> re-declaring them on the way out — an
    /// <c>xmlns</c> added to an element that never had one is a change the user did
    /// not ask for.
    /// </remarks>
    private static string Wrap(List<(string Prefix, string Uri)> declarations, string description)
    {
        var builder = new StringBuilder("<scope");

        foreach ((string prefix, string uri) in declarations)
        {
            builder.Append(' ')
                .Append(prefix.Length == 0 ? "xmlns" : "xmlns:" + prefix)
                .Append("=\"")
                .Append(uri.Replace("&", "&amp;").Replace("\"", "&quot;"))
                .Append('"');
        }

        return builder.Append('>').Append(description).Append("</scope>").ToString();
    }

    private static void CollectNamespaceDeclarations(
        XmlReader reader, List<(string Prefix, string Uri)> into)
    {
        if (!reader.MoveToFirstAttribute())
        {
            return;
        }

        do
        {
            if (reader.Prefix == "xmlns")
            {
                into.Add((reader.LocalName, reader.Value));
            }
            else if (reader.Name == "xmlns")
            {
                into.Add((string.Empty, reader.Value));
            }
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
    }

    /// <summary>
    /// Returns the offset just past the element's closing tag, counting nested
    /// opens so a same-named descendant cannot end the search early.
    /// </summary>
    private static int FindCloseTagEnd(string text, int start, string name, string entryName)
    {
        string open = "<" + name;
        string close = "</" + name;
        int depth = 0;
        int i = start;

        while (i < text.Length)
        {
            int nextOpen = text.IndexOf(open, i, StringComparison.Ordinal);
            int nextClose = text.IndexOf(close, i, StringComparison.Ordinal);

            if (nextClose < 0)
            {
                break;
            }

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                // Only a real element start, not a longer name that merely begins
                // the same way.
                if (IsNameEnd(text, nextOpen + open.Length))
                {
                    depth++;
                }

                i = nextOpen + open.Length;
                continue;
            }

            depth--;
            i = nextClose + close.Length;

            if (depth == 0)
            {
                int end = text.IndexOf('>', i);
                if (end >= 0)
                {
                    return end + 1;
                }

                break;
            }
        }

        throw new BookFormatException(
            $"'{entryName}' has a <{name}> element that is never closed.", entryName);
    }

    private static bool IsNameEnd(string text, int index) =>
        index >= text.Length || text[index] is ' ' or '\t' or '\r' or '\n' or '>' or '/';

    /// <summary>Serialises the document back to bytes.</summary>
    /// <returns>The complete FictionBook file.</returns>
    /// <remarks>
    /// Only the description span is regenerated; the prefix and suffix are the
    /// original characters. Line endings inside the regenerated span are restored
    /// to the source's, because XML parsing normalised them to LF on the way in.
    /// </remarks>
    public byte[] Serialize()
    {
        string body = XmlExactWriter.Write(_description, _selfClosingHasSpace);

        if (_newLine != "\n")
        {
            body = body.Replace("\n", _newLine);
        }

        string text = _text.Substring(0, _descriptionStart)
            + body
            + _text.Substring(_descriptionEnd);

        return XmlEncodingDetector.Encode(text, _encoding);
    }

    /// <summary>Reads the metadata this document carries.</summary>
    /// <returns>The metadata found.</returns>
    public BookMetadata ReadMetadata()
    {
        var metadata = new BookMetadata();

        XElement? titleInfo = Child(_description, "title-info");
        XElement? publishInfo = Child(_description, "publish-info");
        XElement? documentInfo = Child(_description, "document-info");

        metadata.Title = Text(Child(titleInfo, "book-title"));
        metadata.Language = Text(Child(titleInfo, "lang"));
        metadata.Publisher = Text(Child(publishInfo, "publisher"));
        metadata.Description = ReadAnnotation(Child(titleInfo, "annotation"));
        metadata.PublicationDate = ReadDate(titleInfo, publishInfo);
        metadata.Series = ReadSeries(titleInfo);

        ReadCreators(titleInfo, metadata);
        ReadSubjects(titleInfo, metadata);
        ReadIdentifiers(publishInfo, documentInfo, metadata);
        ReadUnmapped(documentInfo, metadata);

        return metadata;
    }

    /// <summary>
    /// The id of the image the cover page points at, without its leading hash.
    /// </summary>
    /// <returns>The binary id, or <see langword="null"/> when none is declared.</returns>
    /// <remarks>
    /// Only the reference is resolved here. Pulling the image itself means walking
    /// the whole file, which <c>Fb2Format</c> does only when a cover was asked for.
    /// </remarks>
    public string? CoverImageId()
    {
        XElement? image = Child(Child(Child(_description, "title-info"), "coverpage"), "image");

        string? href = image?.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == "href")?.Value;

        if (href is null || href.Length == 0)
        {
            return null;
        }

        return href.StartsWith('#') ? href.Substring(1) : href;
    }

    private static string? ReadAnnotation(XElement? annotation)
    {
        if (annotation is null)
        {
            return null;
        }

        // An annotation holds block elements rather than plain text. Paragraphs
        // become lines, which is what the single-line-per-paragraph text box the
        // user edits can round-trip.
        var paragraphs = annotation.Elements()
            .Where(e => e.Name.LocalName is "p" or "subtitle" or "empty-line")
            .Select(e => e.Value.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (paragraphs.Count > 0)
        {
            return string.Join("\n", paragraphs);
        }

        string text = annotation.Value.Trim();
        return text.Length == 0 ? null : text;
    }

    private static BookDate? ReadDate(XElement? titleInfo, XElement? publishInfo)
    {
        // publish-info/year is the publication date proper; title-info/date is when
        // the work was written, which is the better answer than nothing.
        if (Text(Child(publishInfo, "year")) is { } year)
        {
            return MakeDate(year);
        }

        XElement? date = Child(titleInfo, "date");
        if (date is null)
        {
            return null;
        }

        // The value attribute is the machine-readable form; the text is what a
        // human wrote, and may be "spring 1989".
        string? attribute = (string?)date.Attribute("value");
        string raw = string.IsNullOrWhiteSpace(attribute) ? date.Value.Trim() : attribute!.Trim();

        return raw.Length == 0 ? null : MakeDate(raw);
    }

    private static BookDate MakeDate(string raw)
    {
        if (DateTimeOffset.TryParseExact(
                raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset day))
        {
            return new BookDate { Raw = raw, Value = day, Precision = DatePrecision.Day };
        }

        if (DateTimeOffset.TryParseExact(
                raw, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset month))
        {
            return new BookDate { Raw = raw, Value = month, Precision = DatePrecision.Month };
        }

        if (raw.Length == 4 &&
            int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int yearOnly))
        {
            return new BookDate
            {
                Raw = raw,
                Value = new DateTimeOffset(yearOnly, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Precision = DatePrecision.Year,
            };
        }

        return new BookDate { Raw = raw, Precision = DatePrecision.Unknown };
    }

    private static SeriesInfo? ReadSeries(XElement? titleInfo)
    {
        XElement? sequence = Child(titleInfo, "sequence");

        if (sequence is null || (string?)sequence.Attribute("name") is not { } name ||
            string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string? number = (string?)sequence.Attribute("number");

        if (string.IsNullOrWhiteSpace(number))
        {
            return new SeriesInfo { Name = name.Trim() };
        }

        return decimal.TryParse(
            number!.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal index)
            ? new SeriesInfo { Name = name.Trim(), Index = index }
            : new SeriesInfo { Name = name.Trim(), RawIndex = number.Trim() };
    }

    private static void ReadCreators(XElement? titleInfo, BookMetadata metadata)
    {
        if (titleInfo is null)
        {
            return;
        }

        foreach (XElement person in titleInfo.Elements())
        {
            bool translator = person.Name.LocalName == "translator";

            if (person.Name.LocalName != "author" && !translator)
            {
                continue;
            }

            if (PersonName(person) is not { } name)
            {
                continue;
            }

            metadata.Creators.Add(new Creator
            {
                Name = name,
                Role = translator ? "trl" : null,
                NativeRole = translator ? "translator" : "author",
                Kind = translator ? CreatorKind.Contributor : CreatorKind.Creator,
            });
        }
    }

    /// <summary>
    /// Assembles a display name from the parts FB2 stores separately.
    /// </summary>
    private static string? PersonName(XElement person)
    {
        string?[] parts =
        [
            Text(Child(person, "first-name")),
            Text(Child(person, "middle-name")),
            Text(Child(person, "last-name")),
        ];

        string name = string.Join(" ", parts.Where(p => p is not null));

        // A nickname is the whole name for authors who publish under one, and the
        // schema allows it in place of the others.
        return name.Length > 0 ? name : Text(Child(person, "nickname"));
    }

    private static void ReadSubjects(XElement? titleInfo, BookMetadata metadata)
    {
        if (titleInfo is null)
        {
            return;
        }

        foreach (XElement genre in titleInfo.Elements().Where(e => e.Name.LocalName == "genre"))
        {
            string value = genre.Value.Trim();
            if (value.Length > 0 && !metadata.Subjects.Contains(value))
            {
                metadata.Subjects.Add(value);
            }
        }

        foreach (string keyword in (Text(Child(titleInfo, "keywords")) ?? string.Empty)
            .Split(','))
        {
            string value = keyword.Trim();
            if (value.Length > 0 && !metadata.Subjects.Contains(value))
            {
                metadata.Subjects.Add(value);
            }
        }
    }

    private static void ReadIdentifiers(
        XElement? publishInfo, XElement? documentInfo, BookMetadata metadata)
    {
        if (Text(Child(publishInfo, "isbn")) is { } isbn)
        {
            metadata.Identifiers.Add(new Identifier { Value = isbn, Scheme = "ISBN" });
        }

        if (Text(Child(documentInfo, "id")) is { } id)
        {
            metadata.Identifiers.Add(new Identifier { Value = id, Scheme = "FB2-ID" });
        }
    }

    /// <summary>
    /// Records the document-info fields, which describe the FB2 file rather than
    /// the book and so map onto nothing.
    /// </summary>
    private static void ReadUnmapped(XElement? documentInfo, BookMetadata metadata)
    {
        if (documentInfo is null)
        {
            return;
        }

        foreach (XElement field in documentInfo.Elements())
        {
            if (field.Name.LocalName is "id")
            {
                continue;
            }

            string value = field.Name.LocalName == "author"
                ? PersonName(field) ?? string.Empty
                : field.Value.Trim();

            if (value.Length == 0)
            {
                continue;
            }

            metadata.UnmappedFields.Add(new UnmappedField
            {
                Source = "FB2 document-info",
                Key = field.Name.LocalName,
                Text = value,
            });
        }
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Text(XElement? element)
    {
        string? value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Works out the indentation a new element should be written with.
    /// </summary>
    private static string DetectIndent(XElement description)
    {
        foreach (XNode node in description.Nodes())
        {
            if (node is XText text && text.Value.Contains('\n'))
            {
                int last = text.Value.LastIndexOf('\n');
                return text.Value.Substring(last);
            }
        }

        return "\n  ";
    }

    /// <summary>Where each element belongs inside <c>title-info</c>.</summary>
    /// <remarks>
    /// FB2's schema is a sequence, not a set, so an element inserted in the wrong
    /// place makes the document invalid even though every value in it is right.
    /// </remarks>
    private static readonly string[] TitleInfoOrder =
    [
        "genre", "author", "book-title", "annotation", "keywords", "date",
        "coverpage", "lang", "src-lang", "translator", "sequence",
    ];

    private static readonly string[] PublishInfoOrder =
    [
        "book-name", "publisher", "city", "year", "isbn", "sequence",
    ];

    /// <summary>
    /// Applies metadata to the document, touching only what changed.
    /// </summary>
    /// <param name="metadata">The metadata to write.</param>
    /// <remarks>
    /// Compared field by field against the document as it stands, so a field the
    /// user did not edit contributes nothing and an unedited save reproduces the
    /// file byte for byte.
    /// </remarks>
    public void ApplyMetadata(BookMetadata metadata)
    {
        Throw.IfNull(metadata);

        BookMetadata current = ReadMetadata();

        XElement titleInfo = Ensure(_description, "title-info", ["title-info", "document-info", "publish-info"]);

        SetElement(titleInfo, "book-title", current.Title, metadata.Title, TitleInfoOrder);
        SetElement(titleInfo, "lang", current.Language, metadata.Language, TitleInfoOrder);

        ApplySubjects(titleInfo, current, metadata);
        ApplyCreators(titleInfo, current, metadata);
        ApplySeries(titleInfo, current, metadata);
        ApplyAnnotation(titleInfo, current, metadata);
        ApplyPublishInfo(current, metadata);
    }

    private void ApplyPublishInfo(BookMetadata current, BookMetadata metadata)
    {
        bool publisherChanged = !Same(current.Publisher, metadata.Publisher);
        bool dateChanged = !Same(current.PublicationDate?.Raw, metadata.PublicationDate?.Raw);

        if (!publisherChanged && !dateChanged)
        {
            return;
        }

        XElement publishInfo = Ensure(
            _description, "publish-info", ["title-info", "document-info", "publish-info"]);

        if (publisherChanged)
        {
            SetElement(publishInfo, "publisher", current.Publisher, metadata.Publisher, PublishInfoOrder);
        }

        if (dateChanged)
        {
            // publish-info holds a year and nothing finer, so a full date is
            // narrowed rather than written into a field that cannot hold it.
            SetElement(
                publishInfo, "year", Text(Child(publishInfo, "year")), YearOf(metadata.PublicationDate),
                PublishInfoOrder);
        }
    }

    private static string? YearOf(BookDate? date)
    {
        if (date is null || string.IsNullOrWhiteSpace(date.Raw))
        {
            return null;
        }

        if (date.Value is { } value && date.Precision != DatePrecision.Unknown)
        {
            return value.Year.ToString(CultureInfo.InvariantCulture);
        }

        string digits = new(date.Raw.Trim().TakeWhile(char.IsDigit).ToArray());
        return digits.Length == 4 ? digits : null;
    }

    private void ApplySeries(XElement titleInfo, BookMetadata current, BookMetadata metadata)
    {
        string? currentNumber = IndexText(current.Series);
        string? number = IndexText(metadata.Series);

        if (Same(current.Series?.Name, metadata.Series?.Name) && Same(currentNumber, number))
        {
            return;
        }

        XElement? sequence = Child(titleInfo, "sequence");

        if (metadata.Series?.Name is not { } name || name.Trim().Length == 0)
        {
            Remove(sequence);
            return;
        }

        if (sequence is null)
        {
            sequence = new XElement(titleInfo.Name.Namespace + "sequence");
            Insert(titleInfo, sequence, TitleInfoOrder);
        }

        sequence.SetAttributeValue("name", name.Trim());
        sequence.SetAttributeValue("number", number);
    }

    private static string? IndexText(SeriesInfo? series) =>
        series?.Index is { } value
            ? value.ToString("0.############", CultureInfo.InvariantCulture)
            : series?.RawIndex;

    private void ApplyAnnotation(XElement titleInfo, BookMetadata current, BookMetadata metadata)
    {
        if (Same(current.Description, metadata.Description))
        {
            return;
        }

        XElement? annotation = Child(titleInfo, "annotation");

        if (string.IsNullOrWhiteSpace(metadata.Description))
        {
            Remove(annotation);
            return;
        }

        if (annotation is null)
        {
            annotation = new XElement(titleInfo.Name.Namespace + "annotation");
            Insert(titleInfo, annotation, TitleInfoOrder);
        }
        else
        {
            annotation.RemoveNodes();
        }

        // One <p> per line: an annotation is block content, and text dropped
        // straight into the element would not be valid FB2.
        string inner = _indent + "  ";

        foreach (string line in metadata.Description!.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            annotation.Add(new XText(inner));
            annotation.Add(new XElement(titleInfo.Name.Namespace + "p", line.Trim()));
        }

        annotation.Add(new XText(_indent));
    }

    private void ApplyCreators(XElement titleInfo, BookMetadata current, BookMetadata metadata)
    {
        List<Creator> wanted = [.. metadata.Creators.Where(c => c.Kind == CreatorKind.Creator)];
        List<Creator> existing = [.. current.Creators.Where(c => c.Kind == CreatorKind.Creator)];

        // Compared element by element rather than as one joined string, which
        // would make ["AB", "C"] and ["A", "BC"] indistinguishable.
        if (existing.Select(c => c.Name)
            .SequenceEqual(wanted.Select(c => c.Name), StringComparer.Ordinal))
        {
            return;
        }

        // Translators are left exactly where they are: this replaces the author
        // list, and a contributor the user never touched is not part of it.
        foreach (XElement author in titleInfo.Elements()
            .Where(e => e.Name.LocalName == "author").ToList())
        {
            Remove(author);
        }

        foreach (Creator creator in wanted)
        {
            var author = new XElement(titleInfo.Name.Namespace + "author");
            AddNameParts(author, creator.Name);
            Insert(titleInfo, author, TitleInfoOrder);
        }
    }

    /// <summary>
    /// Splits a display name into the parts FB2 stores it as.
    /// </summary>
    /// <remarks>
    /// The last whitespace-separated word is the family name and the rest is
    /// given names, which is right for the Western order FB2's own examples use
    /// and wrong for some names. A single word goes in <c>nickname</c>, because
    /// guessing whether "Voltaire" is a first or last name is worse than saying
    /// neither.
    /// </remarks>
    private static void AddNameParts(XElement author, string name)
    {
        XNamespace ns = author.Name.Namespace;
        string[] words = name.Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            return;
        }

        if (words.Length == 1)
        {
            author.Add(new XElement(ns + "nickname", words[0]));
            return;
        }

        author.Add(new XElement(ns + "first-name", words[0]));

        if (words.Length > 2)
        {
            author.Add(new XElement(
                ns + "middle-name",
                string.Join(" ", words.Skip(1).Take(words.Length - 2))));
        }

        author.Add(new XElement(ns + "last-name", words[words.Length - 1]));
    }

    private void ApplySubjects(XElement titleInfo, BookMetadata current, BookMetadata metadata)
    {
        if (current.Subjects.SequenceEqual(metadata.Subjects, StringComparer.Ordinal))
        {
            return;
        }

        // Written to <keywords>, not <genre>: genre values come from a closed FB2
        // vocabulary, and putting "Space opera" where a reader expects "sf" would
        // produce a document that no longer validates. Existing genres stay.
        var genres = new HashSet<string>(
            titleInfo.Elements().Where(e => e.Name.LocalName == "genre").Select(e => e.Value.Trim()),
            StringComparer.Ordinal);

        List<string> keywords = [.. metadata.Subjects.Where(s => !genres.Contains(s))];

        SetElement(
            titleInfo,
            "keywords",
            Text(Child(titleInfo, "keywords")),
            keywords.Count == 0 ? null : string.Join(", ", keywords),
            TitleInfoOrder);
    }

    /// <summary>
    /// Sets an element's text, creating or removing it as the value requires.
    /// </summary>
    private void SetElement(
        XElement parent, string localName, string? current, string? wanted, string[] order)
    {
        if (Same(current, wanted))
        {
            return;
        }

        XElement? element = Child(parent, localName);

        if (string.IsNullOrWhiteSpace(wanted))
        {
            Remove(element);
            return;
        }

        if (element is null)
        {
            element = new XElement(parent.Name.Namespace + localName);
            Insert(parent, element, order);
        }

        element.SetValue(wanted!);
    }

    /// <summary>
    /// Returns a child, creating it in schema order if it is missing.
    /// </summary>
    private XElement Ensure(XElement parent, string localName, string[] order)
    {
        XElement? element = Child(parent, localName);

        if (element is not null)
        {
            return element;
        }

        element = new XElement(parent.Name.Namespace + localName);
        Insert(parent, element, order);

        return element;
    }

    /// <summary>
    /// Inserts an element where the schema's sequence says it belongs.
    /// </summary>
    private void Insert(XElement parent, XElement element, string[] order)
    {
        int position = Array.IndexOf(order, element.Name.LocalName);

        XElement? after = null;

        foreach (XElement sibling in parent.Elements())
        {
            int siblingPosition = Array.IndexOf(order, sibling.Name.LocalName);

            // An element the order does not mention keeps its place: it is
            // something this build does not understand and must not reshuffle.
            if (siblingPosition >= 0 && siblingPosition <= position)
            {
                after = sibling;
            }
        }

        int depth = DepthOf(parent);
        var indent = new XText(IndentAt(depth));

        if (after is null)
        {
            parent.AddFirst(element);
            parent.AddFirst(indent);
        }
        else
        {
            after.AddAfterSelf(element);
            after.AddAfterSelf(indent);
        }

        // Without this the parent's closing tag would end up on the same line as
        // the element just added.
        if (parent.LastNode is XElement)
        {
            parent.Add(new XText(IndentAt(depth - 1)));
        }
    }

    /// <summary>How many levels below the description an element sits.</summary>
    private int DepthOf(XElement element)
    {
        int depth = 0;

        for (XElement? e = element; e is not null && e != _description; e = e.Parent)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>
    /// The whitespace introducing an element at the given depth below the
    /// description.
    /// </summary>
    private string IndentAt(int depth) =>
        depth <= -1 ? "\n" : _indent + new string(' ', 2 * depth);

    /// <summary>
    /// Removes an element and the whitespace that indented it.
    /// </summary>
    private static void Remove(XElement? element)
    {
        if (element is null)
        {
            return;
        }

        if (element.PreviousNode is XText whitespace && whitespace.Value.Trim().Length == 0)
        {
            whitespace.Remove();
        }

        element.Remove();
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(
            string.IsNullOrWhiteSpace(a) ? null : a,
            string.IsNullOrWhiteSpace(b) ? null : b,
            StringComparison.Ordinal);
}
