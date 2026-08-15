using EBookMeta.Xml;
using EBookMeta.Model;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using System.Xml;

namespace EBookMeta.Formats;

/// <summary>
/// Reads and writes comic archive metadata: an archive of page images plus
/// <c>ComicInfo.xml</c>.
/// </summary>
/// <remarks>
/// This file is the <see cref="IBookFormat"/> implementation — reading, writing,
/// and the corrections a write can prove, such as a <c>PageCount</c> recomputed
/// from the images actually present. The validation rules live beside it in
/// <c>CbzFormat.Rules.cs</c>, which is the same class.
/// <para>
/// One instance serves CBZ and another serves CBT, because the two differ only in
/// the container they are stored in: the metadata document, the rules and the
/// corrections are identical, and nothing here names <c>ZipContainer</c>. The
/// rule IDs stay <c>CBZ-</c> prefixed for both — they are namespaced by metadata
/// convention rather than by container, and a second copy of the table under a
/// <c>CBT-</c> prefix would be one more thing to keep in step for no gain.
/// </para>
/// </remarks>
public sealed partial class CbzFormat : IBookFormat
{
    /// <summary>The CoMet metadata document, read for cross-checking only.</summary>
    private const string CometEntryName = "comet.xml";


    /// <summary>Creates the format for one flavour of comic archive.</summary>
    /// <param name="id">
    /// Which one — <see cref="FormatId.Cbz"/> or <see cref="FormatId.Cbt"/>. The
    /// default is what every caller outside <see cref="BookFormats"/> wants.
    /// </param>
    public CbzFormat(FormatId id = FormatId.Cbz)
    {
        Id = id;

        Capabilities = new FormatCapabilities
        {
            Format = id,

            // ComicInfo has no sort forms, no identifiers and no rights statement,
            // so those fields stay off. That is the point of declaring
            // capabilities: a user must not type a sort title into a comic and
            // have it silently discarded on save.
            ReadableFields =
                MetadataField.Title | MetadataField.Creators | MetadataField.CreatorRoles |
                MetadataField.Series | MetadataField.SeriesIndex | MetadataField.Description |
                MetadataField.Publisher | MetadataField.PublicationDate | MetadataField.Language |
                MetadataField.Subjects | MetadataField.Cover,

            // Everything readable except the cover. A comic's cover is its first
            // page image, so replacing it means replacing a page — and page-image
            // processing is deliberately out of scope.
            WritableFields =
                MetadataField.Title | MetadataField.Creators | MetadataField.CreatorRoles |
                MetadataField.Series | MetadataField.SeriesIndex | MetadataField.Description |
                MetadataField.Publisher | MetadataField.PublicationDate | MetadataField.Language |
                MetadataField.Subjects,
        };

        Extensions = id == FormatId.Cbt ? [".cbt"] : [".cbz"];
    }

    /// <inheritdoc />
    public FormatId Id { get; }

    /// <inheritdoc />
    public FormatCapabilities Capabilities { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// The extensions this build treats as page images, lowercase and with the dot.
    /// </summary>
    /// <remarks>
    /// One list, used both to recognise an untagged comic and to count its pages
    /// for CBZ-E020. Two would let a comic be recognised and then have its pages
    /// miscounted.
    /// </remarks>
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".jxl", ".tif", ".tiff"];

    /// <inheritdoc />
    /// <remarks>
    /// The two registered instances answer for different containers and never for
    /// each other's: a TAR is a CBT, a ZIP is a CBZ, and neither has to know the
    /// other exists. A TAR needs nothing further, because CBT is the only
    /// TAR-based format this build reads.
    /// <para>
    /// Confidence is what keeps a comic from outbidding an EPUB over the same ZIP.
    /// A <c>ComicInfo.xml</c> or a <c>comet.xml</c> is a document only a comic
    /// carries; "nothing but images" is the ComicRack convention for an untagged
    /// comic and no more than a good guess, so it is claimed as weak.
    /// </para>
    /// </remarks>
    public FormatClaim? TryOpen(BookSource source)
    {
        Throw.IfNull(source);

        if (Id == FormatId.Cbt)
        {
            return source.ContainerKind == ContainerKind.Tar
                ? Claim(FormatId.Cbt, "TAR archive", MatchConfidence.Strong)
                : null;
        }

        if (source.ContainerKind != ContainerKind.Zip)
        {
            return null;
        }

        bool sawImage = false;
        bool sawOther = false;

        foreach (ContainerEntry entry in source.Container.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            if (Named(entry.Name) is { } found)
            {
                return Claim(FormatId.Cbz, $"archive contains {found}", MatchConfidence.Strong);
            }

            if (IsImage(entry))
            {
                sawImage = true;
            }
            else
            {
                sawOther = true;
            }
        }

        return sawImage && !sawOther
            ? Claim(FormatId.Cbz, "archive contains only images", MatchConfidence.Weak)
            : null;
    }

    private static FormatClaim Claim(FormatId format, string detail, MatchConfidence confidence) =>
        new() { Format = format, Detail = detail, Confidence = confidence };

    /// <summary>The metadata document this name is, if it is one a comic carries.</summary>
    private static string? Named(string entryName)
    {
        string leaf = entryName.Substring(entryName.LastIndexOf('/') + 1);

        if (leaf.Equals(ComicInfoDocument.DefaultEntryName, StringComparison.OrdinalIgnoreCase))
        {
            return ComicInfoDocument.DefaultEntryName;
        }

        return leaf.Equals(CometEntryName, StringComparison.OrdinalIgnoreCase)
            ? CometEntryName
            : null;
    }

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// <c>ComicInfo.xml</c> is present but not well-formed (CBZ-F001).
    /// </exception>
    public BookMetadata Read(
        IContainer container, ReadOptions? options = null)
    {
        Throw.IfNull(container);

        options ??= ReadOptions.Default;

        ContainerEntry? entry = FindComicInfo(container);
        ComicInfoDocument? document = null;
        BookMetadata metadata;

        if (entry is null)
        {
            metadata = new BookMetadata();
        }
        else
        {
            document = Parse(container, entry);
            metadata = document.ReadMetadata();
        }

        if (options.IncludeCover)
        {
            ReadCover(container, metadata);
        }

        // Checked here rather than on request, because none of it costs anything:
        // every rule below reads the parsed document or entry names the central
        // directory already gave us, and nothing is decompressed to do it.
        CheckLayout(container, entry);
        CheckPages(container, entry, document);
        CheckFields(entry, document);

        Log.Info(
            entry is null
                ? $"Read comic archive metadata: no '{ComicInfoDocument.DefaultEntryName}', "
                    + $"{CountImages(container)} images."
                : $"Read comic archive metadata from '{entry.Name}': "
                    + $"series={Log.Describe(metadata.Series?.Name)}, title={Log.Describe(metadata.Title)}, "
                    + $"creators={metadata.Creators.Count}.");

        return metadata;
    }

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// The archive carries a comment — a ComicBookLover blob — which a rebuild
    /// cannot reproduce, or its <c>ComicInfo.xml</c> is not well-formed.
    /// </exception>
    public void Write(
        IContainer container,
        BookMetadata metadata,
        string targetPath)
    {
        Throw.IfNull(container);
        Throw.IfNull(metadata);
        Throw.IfNullOrEmpty(targetPath);

        // Refused, not warned about and proceeded with. The comment is the only
        // copy of whatever it holds, a rebuild cannot write one back, and losing
        // a user's ComicBookLover metadata to a title edit is not a trade this
        // tool gets to make on their behalf.
        if (!string.IsNullOrEmpty(container.ArchiveComment))
        {
            throw new BookFormatException(
                "This archive carries a ZIP comment, which usually holds ComicBookLover "
                + "metadata. Saving would lose it, because a rebuilt archive cannot carry "
                + "one, so nothing was written.",
                targetPath);
        }

        ContainerEntry? entry = FindComicInfo(container);
        ComicInfoDocument document =
            entry is null ? ComicInfoDocument.CreateEmpty() : Parse(container, entry);

        document.ApplyMetadata(metadata);

        int images = CountImages(container);
        int? declared = document.PageCount;

        // Corrected, not merely reported. The images are right here to be counted,
        // so a PageCount that disagrees with them is wrong rather than evidence of
        // anything, and every reader that trusts it is misled until it is fixed.
        if (document.SetPageCount(images) && entry is not null)
        {
            Log.Rule(LogLevel.Warning, "CBZ-E020", declared is null
                    ? $"No PageCount was declared; set to {images} on save."
                    : $"PageCount said {declared} but the archive holds {images} "
                        + $"image{(images == 1 ? "" : "s")}; corrected on save.", entry.Name);
        }

        // A nested ComicInfo.xml is one most readers never find, and the rebuild is
        // already composing a fresh entry list, so moving it costs nothing. Only
        // the metadata document moves: the images keep their order, which for a
        // comic is the reading order.
        bool relocate = entry is not null && entry.Name.IndexOf('/') >= 0;

        if (relocate)
        {
            Log.Rule(
                LogLevel.Warning,
                "CBZ-E011",
                $"'{entry!.Name}' was not at the archive root, where readers look "
                    + $"for it; moved to '{ComicInfoDocument.DefaultEntryName}' on save.",
                entry.Name);
        }

        byte[] bytes = document.Serialize();
        bool replaceInPlace = entry is not null && !relocate;

        var entries = new List<PendingEntry>(container.Entries.Count + 1);

        foreach (ContainerEntry existing in container.Entries)
        {
            if (relocate && existing.Index == entry!.Index)
            {
                continue;
            }

            entries.Add(replaceInPlace && existing.Index == entry!.Index
                ? PendingEntry.Replacing(existing, bytes)
                : PendingEntry.CopyOf(container, existing));
        }

        if (!replaceInPlace)
        {
            // Appended rather than inserted first. Every reader finds the entry by
            // name, so its position buys nothing — while putting it anywhere but
            // the end would move existing entries, and preserving their order is
            // an invariant. For a comic the entry order is also the reading order.
            entries.Add(PendingEntry.FromBytes(
                ComicInfoDocument.DefaultEntryName,
                bytes,
                entry?.CompressionMethod ?? ZipCompressionMethods.Deflate,
                entry?.LastModified ?? default));
        }

        container.Rebuild(entries, targetPath);

        Log.Info(
            replaceInPlace
                ? $"Wrote {entries.Count} entries, replacing '{entry!.Name}'."
                : entry is null
                    ? $"Wrote {entries.Count} entries, adding "
                        + $"'{ComicInfoDocument.DefaultEntryName}'."
                    : $"Wrote {entries.Count} entries, moving '{entry.Name}' to "
                        + $"'{ComicInfoDocument.DefaultEntryName}'.");
    }

    /// <summary>
    /// Parses the metadata document, reporting CBZ-F001 before giving up.
    /// </summary>
    private static ComicInfoDocument Parse(
        IContainer container, ContainerEntry entry)
    {
        try
        {
            return ComicInfoDocument.Parse(container.ReadAllBytes(entry), entry.Name);
        }
        catch (BookFormatException ex)
        {
            Log.Rule(LogLevel.Error, "CBZ-F001", ex.Message, entry.Name);
            throw;
        }
    }

    /// <summary>
    /// Reads the cover: the first page, in reading order.
    /// </summary>
    private static void ReadCover(IContainer container, BookMetadata metadata)
    {
        ContainerEntry? first = Images(container)
            .OrderBy(e => e.Name, NaturalNameComparer.Instance)
            .FirstOrDefault();

        if (first is null)
        {
            return;
        }

        metadata.Cover = new CoverImage
        {
            Data = container.ReadAllBytes(first),
            MediaType = MediaTypeOf(first.Name),
            SourceEntryName = first.Name,
        };
    }

    private static string MediaTypeOf(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            ".jxl" => "image/jxl",
            ".tif" or ".tiff" => "image/tiff",
            _ => "image/jpeg",
        };

    /// <summary>
    /// Finds <c>ComicInfo.xml</c>, tolerating the casing and location producers
    /// get wrong.
    /// </summary>
    private static ContainerEntry? FindComicInfo(IContainer container)
    {
        ContainerEntry? exact = null;
        ContainerEntry? caseInsensitive = null;
        ContainerEntry? nested = null;

        foreach (ContainerEntry entry in container.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            if (entry.Name.Equals(ComicInfoDocument.DefaultEntryName, StringComparison.Ordinal))
            {
                exact ??= entry;
            }
            else if (entry.Name.Equals(ComicInfoDocument.DefaultEntryName, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitive ??= entry;
            }
            else if (Path.GetFileName(entry.Name)
                .Equals(ComicInfoDocument.DefaultEntryName, StringComparison.OrdinalIgnoreCase))
            {
                nested ??= entry;
            }
        }

        return exact ?? caseInsensitive ?? nested;
    }

    private static IEnumerable<ContainerEntry> Images(IContainer container) =>
        container.Entries.Where(e => !e.IsDirectory && IsImage(e));

    private static int CountImages(IContainer container) => Images(container).Count();

    private static bool IsImage(ContainerEntry entry) =>
        ImageExtensions.Contains(Path.GetExtension(entry.Name).ToLowerInvariant());

    private static bool IsMetadata(ContainerEntry entry)
    {
        string name = Path.GetFileName(entry.Name);

        return name.Equals(ComicInfoDocument.DefaultEntryName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(CometEntryName, StringComparison.OrdinalIgnoreCase);
    }

}

/// <summary>
/// The comic archive validation rules, by stable rule ID.
/// </summary>
/// <remarks>
/// Every rule here works from the parsed <c>ComicInfo.xml</c> or from entry names
/// the ZIP central directory already supplied. Nothing is decompressed, so a
/// 300-page comic costs no more to check than a one-page one — which is why these
/// run on every read instead of waiting to be asked for.
/// <para>
/// A rule goes where its evidence is. A defect a write can prove and fix — a
/// <c>PageCount</c> that disagrees with the images present, a nested
/// <c>ComicInfo.xml</c> — is corrected in <c>CbzFormat.cs</c> instead, and reports
/// what it changed.
/// </para>
/// </remarks>
public sealed partial class CbzFormat
{
    /// <summary>
    /// Reports where the metadata document is, or that there is none.
    /// </summary>
    private static void CheckLayout(
        IContainer container, ContainerEntry? entry)
    {
        if (entry is null)
        {
            // Not a defect. Most comics in a collection have never been tagged,
            // and this is the message that explains what saving will do.
            Log.Rule(
                LogLevel.Warning,
                "CBZ-W010",
                $"There is no '{ComicInfoDocument.DefaultEntryName}'. "
                    + "One will be created when you save.");
        }
        else if (entry.Name.IndexOf('/') >= 0)
        {
            Log.Rule(
                LogLevel.Error,
                "CBZ-E011",
                $"'{entry.Name}' is not at the archive root, so most readers "
                    + "will not find it.",
                entry.Name);
        }

        bool hasCoMet = container.Entries.Any(e =>
            e.Name.Equals(CometEntryName, StringComparison.OrdinalIgnoreCase));
        bool hasComicBookLover = !string.IsNullOrEmpty(container.ArchiveComment);

        if (hasCoMet || hasComicBookLover)
        {
            // Disagreement between conventions is not read here — resolving it
            // would mean parsing two more documents on open. Naming them is
            // enough for the user to know why two applications show different
            // titles for the same file.
            Log.Rule(
                LogLevel.Warning,
                "CBZ-W012",
                "This archive carries more than one metadata convention "
                    + $"({string.Join(" and ", Conventions(entry, hasCoMet, hasComicBookLover))}). "
                    + $"Only '{ComicInfoDocument.DefaultEntryName}' is written; the others are "
                    + "left as they are, so applications reading them may disagree.");
        }
    }

    private static IEnumerable<string> Conventions(
        ContainerEntry? comicInfo, bool hasCoMet, bool hasComicBookLover)
    {
        if (comicInfo is not null)
        {
            yield return comicInfo.Name;
        }

        if (hasCoMet)
        {
            yield return CometEntryName;
        }

        if (hasComicBookLover)
        {
            yield return "a ComicBookLover blob in the ZIP comment";
        }
    }

    /// <summary>
    /// Cross-checks the declared pages against the images actually present.
    /// </summary>
    /// <remarks>
    /// Every check here works from entry names alone, which the ZIP central
    /// directory already gave us. Nothing is decompressed, so a 300-page comic
    /// costs no more to check than a one-page one — which is why these run on every
    /// read instead of waiting to be asked for.
    /// </remarks>
    private static void CheckPages(
        IContainer container,
        ContainerEntry? entry,
        ComicInfoDocument? document)
    {
        List<ContainerEntry> images = Images(container).ToList();

        if (document is not null)
        {
            if (document.PageCount is { } declared && declared != images.Count)
            {
                Log.Rule(
                    LogLevel.Error,
                    "CBZ-E020",
                    $"PageCount says {declared} but the archive holds "
                        + $"{images.Count} image{(images.Count == 1 ? "" : "s")}.",
                    entry!.Name);
            }

            IReadOnlyList<int?> pages = document.PageImageIndexes;

            if (pages.Count > 0 &&
                (pages.Count != images.Count || pages.Any(p => p is null || p < 0 || p >= images.Count)))
            {
                Log.Rule(
                    LogLevel.Warning,
                    "CBZ-W021",
                    $"The <Pages> block describes {pages.Count} page(s) and does not "
                        + $"match the {images.Count} image(s) in the archive.",
                    entry!.Name);
            }
        }

        if (!SortsIntoReadingOrder(images))
        {
            Log.Rule(
                LogLevel.Warning,
                "CBZ-W022",
                "The page filenames do not sort into reading order. Unpadded "
                    + "numbers are the usual cause — '10.jpg' sorts before '2.jpg' — and "
                    + "readers that sort by name will show the pages jumbled.");
        }

        List<string> extras = container.Entries
            .Where(e => !e.IsDirectory && !IsImage(e) && !IsMetadata(e))
            .Select(e => e.Name)
            .ToList();

        if (extras.Count > 0)
        {
            Log.Rule(LogLevel.Warning, "CBZ-W023", $"The archive holds {extras.Count} entr"
                    + (extras.Count == 1 ? "y" : "ies")
                    + " that are neither images nor metadata.");
        }
    }

    /// <summary>
    /// Checks the fields whose content can be wrong on its own terms.
    /// </summary>
    private static void CheckFields(
        ContainerEntry? entry, ComicInfoDocument? document)
    {
        if (document is null || entry is null)
        {
            return;
        }

        if (document.Value("Number") is { } number && document.Value("Series") is null)
        {
            Log.Rule(
                LogLevel.Warning,
                "CBZ-W030",
                $"Number is '{number}' but no Series is given, so a library will "
                    + "file this issue under no series at all.",
                entry.Name);
        }

        if (ImpossibleDate(document) is { } date)
        {
            Log.Rule(
                LogLevel.Warning,
                "CBZ-W031",
                $"Year, Month and Day do not form a real date ({date}).",
                entry.Name);
        }

        if (document.Value("LanguageISO") is { } language && !IsIso639Part1(language))
        {
            Log.Rule(
                LogLevel.Warning,
                "CBZ-W032",
                $"LanguageISO is '{language}', which is not an ISO 639-1 code. "
                    + "The schema wants two letters, such as 'en' or 'fr'.",
                entry.Name);
        }
    }

    /// <summary>
    /// Returns the offending date when Year, Month and Day cannot all be true at
    /// once, or <see langword="null"/> when they can.
    /// </summary>
    private static string? ImpossibleDate(ComicInfoDocument document)
    {
        string? rawYear = document.Value("Year");
        string? rawMonth = document.Value("Month");
        string? rawDay = document.Value("Day");

        if (rawYear is null && rawMonth is null && rawDay is null)
        {
            return null;
        }

        string described = string.Join(
            "-", new[] { rawYear, rawMonth, rawDay }.Where(p => p is not null));

        int? year = ParseNumber(rawYear);
        int? month = ParseNumber(rawMonth);
        int? day = ParseNumber(rawDay);

        if ((rawYear is not null && year is null) ||
            (rawMonth is not null && month is null) ||
            (rawDay is not null && day is null))
        {
            return described;
        }

        if (month is < 1 or > 12)
        {
            return described;
        }

        if (day is null)
        {
            return null;
        }

        // A day with no month cannot be checked and cannot be right either: the
        // schema has no way to express one, and readers ignore it.
        if (month is null)
        {
            return day is < 1 or > 31 ? described : null;
        }

        int daysInMonth = DateTime.DaysInMonth(year ?? 2000, month.Value);
        return day < 1 || day > daysInMonth ? described : null;
    }

    private static int? ParseNumber(string? value) =>
        value is not null &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : null;

    /// <summary>
    /// Whether a language tag is a two-letter code a real language uses.
    /// </summary>
    /// <remarks>
    /// Two checks, not one: the shape, because ComicInfo's schema says ISO 639-1
    /// and <c>en-US</c> is a BCP 47 tag rather than one; and membership of the
    /// neutral-culture table, because <c>zz</c> has the right shape and means
    /// nothing. <c>CultureInfo.GetCultureInfo("zz")</c> is not the check — Windows
    /// hands back a synthesised culture for any well-formed tag, so it accepts
    /// every two-letter string there is.
    /// </remarks>
    private static bool IsIso639Part1(string language) =>
        language.Length == 2 &&
        language.All(char.IsLetter) &&
        KnownLanguageCodes.Value.Contains(language);

    /// <summary>
    /// Every two-letter language code this machine knows about.
    /// </summary>
    /// <remarks>
    /// Lazy on purpose: enumerating the culture table costs several hundred
    /// allocations, and a validation rule nobody has run yet has no business
    /// spending them against a 400 ms launch budget.
    /// </remarks>
    private static readonly Lazy<HashSet<string>> KnownLanguageCodes = new(() =>
        new HashSet<string>(
            CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .Select(culture => culture.TwoLetterISOLanguageName)
                .Where(code => code.Length == 2),
            StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Whether an ordinary name sort produces the same order as a numeric-aware
    /// one.
    /// </summary>
    private static bool SortsIntoReadingOrder(List<ContainerEntry> images)
    {
        if (images.Count < 2)
        {
            return true;
        }

        List<string> names = images.Select(e => e.Name).ToList();

        List<string> ordinal = [.. names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        List<string> natural = [.. names.OrderBy(n => n, NaturalNameComparer.Instance)];

        return ordinal.SequenceEqual(natural, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// <c>ComicInfo.xml</c> — the ComicRack metadata document, parsed in a way that
/// survives editing.
/// </summary>
public sealed class ComicInfoDocument
{
    /// <summary>The entry name the convention fixes, at the archive root.</summary>
    public const string DefaultEntryName = "ComicInfo.xml";

    /// <summary>
    /// The element order the ComicInfo schema requires.
    /// </summary>
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
    public int? PageCount =>
        int.TryParse(Value("PageCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            ? count
            : null;

    /// <summary>
    /// The <c>Image</c> index of every <c>&lt;Page&gt;</c> element, in document
    /// order; null for one whose index is missing or unparseable.
    /// </summary>
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
            return BookDate.Parse(year);
        }

        string composed = day is null
            ? $"{Pad(year, 4)}-{Pad(month, 2)}"
            : $"{Pad(year, 4)}-{Pad(month, 2)}-{Pad(day, 2)}";

        return BookDate.Parse(composed);
    }

    private static string Pad(string value, int width) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')
            : value;

    /// <summary>
    /// Records elements that map onto no model field.
    /// </summary>
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
    /// Records the archive's real page count, replacing a wrong one.
    /// </summary>
    /// <param name="pageCount">The number of images actually in the archive.</param>
    /// <returns>
    /// <see langword="true"/> when the document changed, so the caller can report
    /// the correction.
    /// </returns>
    public bool SetPageCount(int pageCount)
    {
        string wanted = pageCount.ToString(CultureInfo.InvariantCulture);

        if (Root is not { } root || string.Equals(Value("PageCount"), wanted, StringComparison.Ordinal))
        {
            return false;
        }

        SetElement(root, "PageCount", Value("PageCount"), wanted);
        return true;
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
