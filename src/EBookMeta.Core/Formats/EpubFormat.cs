using EBookMeta.Xml;
using EBookMeta.Model;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using System.Xml;

namespace EBookMeta.Formats;

/// <summary>
/// Reads and writes EPUB 2 and EPUB 3 metadata.
/// </summary>
/// <remarks>
/// This file is the <see cref="IBookFormat"/> implementation — reading, writing,
/// and the corrections a write can prove. The validation rules live beside it in
/// <c>EpubFormat.Rules.cs</c>, which is the same class: they are half the code
/// and none of the interface, so keeping them here would bury what this type
/// actually is.
/// </remarks>
public sealed partial class EpubFormat : IBookFormat
{
    /// <summary>The entry every EPUB must store first, uncompressed.</summary>
    private const string MimetypeEntryName = "mimetype";

    /// <summary>Its exact required content — no BOM, no trailing newline.</summary>
    private const string EpubMediaType = "application/epub+zip";

    /// <inheritdoc />
    public FormatId Id => FormatId.Epub;

    /// <inheritdoc />
    public FormatCapabilities Capabilities { get; } = new()
    {
        Format = FormatId.Epub,

        // EPUB expresses every field the model carries.
        ReadableFields = MetadataField.All,
        WritableFields = MetadataField.All,
    };

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".epub"];

    /// <inheritdoc />
    /// <remarks>
    /// The <c>mimetype</c> entry is what identifies an EPUB, and its content is
    /// checked rather than its name alone because a CBZ may contain a file called
    /// <c>mimetype</c> too. Twenty stored bytes, from the container that is already
    /// open.
    /// <para>
    /// An entry whose content is wrong, or which is compressed or not first, still
    /// claims the file — at <see cref="MatchConfidence.Strong"/> rather than
    /// certain. That is deliberate: those are exactly the defects EPUB-E040
    /// describes and a save corrects, so declining here would refuse to open the
    /// one kind of broken EPUB this tool repairs outright. Recognising a format is
    /// not endorsing the file.
    /// </para>
    /// </remarks>
    public FormatClaim? TryOpen(BookSource source)
    {
        Throw.IfNull(source);

        if (source.ContainerKind != ContainerKind.Zip)
        {
            return null;
        }

        foreach (ContainerEntry entry in source.Container.Entries)
        {
            if (!entry.Name.Equals(MimetypeEntryName, StringComparison.Ordinal))
            {
                continue;
            }

            bool declaresEpub =
                entry.Length == EpubMediaType.Length &&
                Encoding.ASCII.GetString(source.Container.ReadAllBytes(entry))
                    .Equals(EpubMediaType, StringComparison.Ordinal);

            return new FormatClaim
            {
                Format = FormatId.Epub,
                Detail = declaresEpub
                    ? $"mimetype declares {EpubMediaType}"
                    : "mimetype entry present",
                Confidence = declaresEpub ? MatchConfidence.Certain : MatchConfidence.Strong,
            };
        }

        return null;
    }

    /// <summary>
    /// Opens the package document referenced by <c>META-INF/container.xml</c>.
    /// </summary>
    /// <param name="container">The open EPUB container.</param>
    /// <returns>The parsed package document and the entry it came from.</returns>
    /// <exception cref="BookFormatException">
    /// The container file or the package document is missing or malformed.
    /// </exception>
    public static OpfDocument OpenPackageDocument(
        IContainer container)
    {
        Throw.IfNull(container);

        ContainerEntry entry = LocatePackageDocument(container);
        byte[] bytes = container.ReadAllBytes(entry);

        try
        {
            return OpfDocument.Parse(bytes, entry.Name);
        }
        catch (BookFormatException)
        {
            // Recoverable damage is corrected here, on the way in, so every
            // caller downstream gets a document that parses. Read shows the user
            // correct metadata, Write serialises a correct tree, and the
            // correction reaches the disk only when they save — there is no
            // repair-specific write path, and therefore no way for a repair to
            // rewrite a file on its own.
            //
            // Only a repair that fully succeeds is used. A partial fix would
            // hand back a document that still does not parse, so the original
            // error is the more useful answer.
            NamespaceRepairResult? repair = RepairNamespaces(bytes);

            if (repair is not { IsComplete: true, HasChanges: true })
            {
                if (repair?.Skipped.Count > 0)
                {
                    Log.Error(
                        $"'{entry.Name}' cannot be repaired: prefixes {string.Join(", ", repair.Skipped)} "
                        + "are undeclared and no known namespace applies to them.");
                }

                throw;
            }

            // Logged as a warning, which invariant 14 requires of every repair: a
            // repair is the one thing that changes a user's file without them
            // asking, so it must never be silent.
            Log.Rule(
                LogLevel.Warning,
                "EPUB-W070",
                $"'{entry.Name}' was missing namespace declarations and has been repaired "
                    + $"in memory: added xmlns for {string.Join(", ", repair.Added)}. "
                    + "Save to keep the correction.",
                entry.Name);

            return OpfDocument.Parse(repair.RepairedBytes, entry.Name);
        }
    }

    /// <summary>
    /// Locates the package document and returns its bytes without parsing them.
    /// </summary>
    /// <param name="container">The EPUB container.</param>
    /// <returns>The entry name and its raw bytes.</returns>
    /// <exception cref="BookFormatException">
    /// <c>container.xml</c> is missing, declares no rootfile, or points at an
    /// entry that is not in the archive.
    /// </exception>
    /// <remarks>
    /// The repair path needs this. <see cref="OpenPackageDocument"/> parses, and
    /// therefore throws on exactly the documents a repair exists to fix — so the
    /// bytes of a package document that will not parse are unreachable through
    /// it. Resolving the rootfile only requires <c>container.xml</c>, which is a
    /// separate document and usually intact.
    /// </remarks>
    public static RawPackageDocument ReadRawPackageDocument(IContainer container)
    {
        Throw.IfNull(container);

        ContainerEntry entry = LocatePackageDocument(container);

        return new RawPackageDocument
        {
            EntryName = entry.Name,
            Bytes = container.ReadAllBytes(entry),
        };
    }

    /// <summary>
    /// Resolves <c>container.xml</c>'s rootfile to the entry holding it.
    /// </summary>
    /// <exception cref="BookFormatException">
    /// <c>container.xml</c> is missing, declares no rootfile, or points at an
    /// entry that is not in the archive.
    /// </exception>
    private static ContainerEntry LocatePackageDocument(IContainer container)
    {
        string? opfPath = ContainerXml.Read(container).PrimaryRootfilePath;

        if (opfPath is null)
        {
            throw new BookFormatException(
                $"'{ContainerXml.EntryName}' declares no rootfile.", ContainerXml.EntryName);
        }

        return FindEntry(container, opfPath)
            ?? throw new BookFormatException(
                $"'{ContainerXml.EntryName}' points at '{opfPath}', which is not in the archive.",
                ContainerXml.EntryName);
    }

    /// <inheritdoc />
    public BookMetadata Read(
        IContainer container, ReadOptions? options = null)
    {
        Throw.IfNull(container);

        options ??= ReadOptions.Default;

        OpfDocument opf = OpenPackageDocument(container);
        BookMetadata metadata = opf.ReadMetadata();

        if (options.IncludeCover)
        {
            ReadCover(container, opf, metadata);
        }

        Log.Info(
            $"Read EPUB {opf.Version ?? "(unversioned)"} metadata from '{opf.EntryName}': "
            + $"title={Log.Describe(metadata.Title)}, creators={metadata.Creators.Count}, "
            + $"cover={(metadata.Cover is null ? "none" : metadata.Cover.MediaType)}.");

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

        OpfDocument opf = OpenPackageDocument(container);
        opf.ApplyMetadata(metadata);

        byte[]? coverBytes = ApplyCover(opf, metadata, out string? coverEntryName);
        byte[] opfBytes = opf.Serialize();

        // Every entry is copied through byte for byte except the ones we mean to
        // change. Content files, stylesheets and page images never go near a
        // parser, and order and per-entry compression method are carried over,
        // so an unedited save reproduces the original exactly.
        var entries = new List<PendingEntry>(container.Entries.Count);

        foreach (ContainerEntry entry in container.Entries)
        {
            if (entry.Name.Equals(opf.EntryName, StringComparison.Ordinal))
            {
                entries.Add(PendingEntry.Replacing(entry, opfBytes));
            }
            else if (coverBytes is not null &&
                     entry.Name.Equals(coverEntryName, StringComparison.Ordinal))
            {
                entries.Add(PendingEntry.Replacing(entry, coverBytes));
            }
            else
            {
                entries.Add(PendingEntry.CopyOf(container, entry));
            }
        }

        RepairMimetype(entries);
        RepairNcxIdentifier(container, opf, entries);

        container.Rebuild(entries, targetPath);

        Log.Info(
            $"Wrote {entries.Count} entries, replacing '{opf.EntryName}'"
            + (coverEntryName is null ? "." : $" and '{coverEntryName}'."));
    }

    /// <summary>
    /// Declares the cover in both conventions and reports replacement image
    /// bytes, when the user supplied a new image.
    /// </summary>
    /// <returns>
    /// The new image bytes to substitute, or <see langword="null"/> when the
    /// cover image itself is unchanged.
    /// </returns>
    /// <remarks>
    /// Replacing the bytes of the existing entry rather than adding a new one
    /// keeps the manifest, the spine and every href pointing where they already
    /// pointed. Adding an entry would mean rewriting references, which is a much
    /// larger change to a file the user only wanted a new cover on.
    /// </remarks>
    private static byte[]? ApplyCover(OpfDocument opf, BookMetadata metadata, out string? coverEntryName)
    {
        coverEntryName = null;

        if (metadata.Cover is not { } cover)
        {
            return null;
        }

        string? manifestId = cover.SourceManifestId
            ?? opf.Manifest.FirstOrDefault(i => i.IsCoverImage)?.Id;

        // Only rewrite the declarations if they do not already say what we are
        // about to say. Otherwise opening an EPUB 3 and saving it unchanged
        // would add the EPUB 2 <meta name="cover"> form, altering a file the
        // user never edited.
        if (manifestId is not null && !opf.CoverIsAlreadyDeclaredAs(manifestId))
        {
            opf.ApplyCoverDeclaration(manifestId);
        }

        coverEntryName = cover.SourceEntryName;
        return cover.SourceEntryName is null ? null : cover.Data;
    }

    /// <summary>
    /// Puts <c>mimetype</c> back where the specification requires it.
    /// </summary>
    private static void RepairMimetype(List<PendingEntry> entries)
    {
        int index = entries.FindIndex(e => e.Name.Equals(MimetypeEntryName, StringComparison.Ordinal));
        PendingEntry? existing = index < 0 ? null : entries[index];

        bool wrongPlace = index != 0;
        bool wrongStorage = existing is not null && existing.CompressionMethod != ZipCompressionMethods.Stored;

        if (existing is not null && !wrongPlace && !wrongStorage)
        {
            return;
        }

        if (index >= 0)
        {
            entries.RemoveAt(index);
        }

        entries.Insert(0, PendingEntry.FromBytes(
            MimetypeEntryName,
            Encoding.ASCII.GetBytes(EpubMediaType),
            ZipCompressionMethods.Stored));

        Log.Rule(
            LogLevel.Warning,
            "EPUB-E040",
            existing is null
                ? $"'{MimetypeEntryName}' was missing; written as the first entry, stored."
                : wrongPlace
                    ? $"'{MimetypeEntryName}' was not the first entry; moved to the front on save."
                    : $"'{MimetypeEntryName}' was compressed; stored on save.",
            MimetypeEntryName);
    }

    /// <summary>The NCX media type, which is how the table of contents is found.</summary>
    private const string NcxMediaType = "application/x-dtbncx+xml";

    /// <summary>
    /// Puts the NCX's <c>dtb:uid</c> back in step with the package's unique
    /// identifier, which EPUB 2 requires to be the same string.
    /// </summary>
    /// <remarks>
    /// An EPUB 2 stores the book's identity twice — as the <c>dc:identifier</c> the
    /// package points at, and again as <c>&lt;meta name="dtb:uid"&gt;</c> in the
    /// NCX — and OPF 2.0.1 requires them to match. Converters leave them
    /// disagreeing constantly; epubcheck 3.0.1 reported it and KDP still rejects it.
    /// <para>
    /// Which one is right is not a judgement call, which is what makes this a
    /// correction rather than a report: the package document is authoritative on the
    /// book's identity by specification, and <c>dtb:uid</c> is required to be a copy
    /// of it. So the OPF value wins and the NCX is brought into line.
    /// </para>
    /// <para>
    /// The edit is a splice at the offsets of the existing <c>content="…"</c>, not a
    /// parse and re-emit. Every other byte of the NCX — every <c>navPoint</c>, every
    /// line ending — is the original. That is hard invariant 16 applied to a
    /// document this build does not otherwise model, and it is why the NCX is never
    /// handed to <c>XDocument</c>.
    /// </para>
    /// <para>
    /// EPUB 3 is skipped: the nav document supersedes the NCX and nothing requires
    /// the two to agree, so rewriting a legacy NCX there would be changing a file
    /// for no reason.
    /// </para>
    /// </remarks>
    private static void RepairNcxIdentifier(
        IContainer container, OpfDocument opf, List<PendingEntry> entries)
    {
        if (opf.Version is { Length: > 0 } version && version.StartsWith('3'))
        {
            return;
        }

        if (UniqueIdentifierValue(opf) is not { Length: > 0 } expected)
        {
            return;
        }

        ManifestItem? ncx = opf.Manifest.FirstOrDefault(
            i => string.Equals(i.MediaType, NcxMediaType, StringComparison.OrdinalIgnoreCase));

        if (ncx is null || ncx.Href.Length == 0)
        {
            return;
        }

        string resolved = ResolveHref(opf.EntryName, ncx.Href);
        ContainerEntry? entry = FindEntry(container, resolved);

        if (entry is null)
        {
            return;
        }

        byte[] original = container.ReadAllBytes(entry);
        XmlEncodingInfo encoding = XmlEncodingDetector.Detect(original);
        string text = XmlEncodingDetector.Decode(original, encoding);

        if (DtbUidSpan(text) is not var (start, length) || length < 0)
        {
            return;
        }

        string actual = text.Substring(start, length);

        if (actual.Trim().Equals(expected, StringComparison.Ordinal))
        {
            return;
        }

        string patched = text.Substring(0, start) + expected + text.Substring(start + length);

        int index = entries.FindIndex(e => e.Name.Equals(entry.Name, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        entries[index] = PendingEntry.Replacing(entry, XmlEncodingDetector.Encode(patched, encoding));

        Log.Rule(
            LogLevel.Warning,
            "EPUB-W062",
            $"The table of contents said the book's identifier was '{actual.Trim()}' but the "
                + $"package says '{expected}'; corrected on save so readers cannot treat this "
                + "as two different books.",
            entry.Name);
    }

    /// <summary>
    /// Locates the value inside <c>&lt;meta name="dtb:uid" content="…"&gt;</c>.
    /// </summary>
    /// <returns>
    /// The offset and length of the value, or <see langword="null"/> when the NCX
    /// declares no uid — in which case there is nothing to bring into line.
    /// </returns>
    private static (int Start, int Length)? DtbUidSpan(string text)
    {
        int name = text.IndexOf("dtb:uid", StringComparison.OrdinalIgnoreCase);
        if (name < 0)
        {
            return null;
        }

        int content = text.IndexOf("content", name, StringComparison.OrdinalIgnoreCase);
        if (content < 0)
        {
            return null;
        }

        int open = text.IndexOfAny(['"', '\''], content);
        if (open < 0)
        {
            return null;
        }

        int close = text.IndexOf(text[open], open + 1);
        return close < 0 ? null : (open + 1, close - open - 1);
    }

    /// <summary>The value of the <c>dc:identifier</c> the package points at.</summary>
    private static string? UniqueIdentifierValue(OpfDocument opf)
    {
        if (opf.UniqueIdentifierRef is not { Length: > 0 } id || opf.Metadata is null)
        {
            return null;
        }

        return opf.Metadata
            .Elements(OpfDocument.DcNs + "identifier")
            .FirstOrDefault(e => (string?)e.Attribute("id") == id)
            ?.Value.Trim();
    }

    /// <summary>
    /// Resolves the cover image through both declaration conventions.
    /// </summary>
    private static void ReadCover(IContainer container, OpfDocument opf, BookMetadata metadata)
    {
        ManifestItem? item = opf.Manifest.FirstOrDefault(i => i.IsCoverImage);

        if (item is null)
        {
            string? coverId = opf.Metadata?
                .Elements()
                .Where(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == "cover")
                .Select(e => (string?)e.Attribute("content"))
                .FirstOrDefault(v => v is not null);

            if (coverId is not null)
            {
                item = opf.Manifest.FirstOrDefault(i => string.Equals(i.Id, coverId, StringComparison.Ordinal));
            }
        }

        if (item is null || item.Href.Length == 0)
        {
            return;
        }

        string resolved = ResolveHref(opf.EntryName, item.Href);
        ContainerEntry? entry = FindEntry(container, resolved);

        if (entry is null)
        {
            // A cover declaration pointing at nothing is EPUB-E030's business.
            // Reading is not the place to complain, only to not crash.
            return;
        }

        metadata.Cover = new CoverImage
        {
            Data = container.ReadAllBytes(entry),
            MediaType = item.MediaType ?? GuessMediaType(entry.Name),
            SourceEntryName = entry.Name,
            SourceManifestId = item.Id,
        };
    }

    /// <summary>
    /// Resolves a manifest href against the package document's own directory.
    /// </summary>
    /// <remarks>
    /// Hrefs are URL-encoded, so a file called <c>my cover.jpg</c> appears as
    /// <c>my%20cover.jpg</c> and will not match any entry name until decoded.
    /// Traversal is not resolved here: an href containing <c>..</c> is left as
    /// found so rule GEN-E003 can report it rather than the reader silently
    /// following it out of the archive.
    /// </remarks>
    internal static string ResolveHref(string opfEntryName, string href)
    {
        string decoded = Uri.UnescapeDataString(href);

        int fragment = decoded.IndexOf('#');
        if (fragment >= 0)
        {
            decoded = decoded.Substring(0, fragment);
        }

        int lastSlash = opfEntryName.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return decoded;
        }

        string directory = opfEntryName.Substring(0, lastSlash + 1);
        return decoded.StartsWith('/') ? decoded.TrimStart('/') : directory + decoded;
    }

    private static ContainerEntry? FindEntry(IContainer container, string name)
    {
        foreach (ContainerEntry entry in container.Entries)
        {
            if (entry.Name.Equals(name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        // ZIP entry names are case-sensitive, but Windows-authored archives are
        // frequently inconsistent about it. Accept a case-insensitive match on
        // the second pass so a book still opens; the mismatch is reported
        // separately rather than being made fatal here.
        foreach (ContainerEntry entry in container.Entries)
        {
            if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string GuessMediaType(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }
}

/// <summary>
/// A package document as bytes, before any attempt to parse it.
/// </summary>
/// <seealso cref="EpubFormat.ReadRawPackageDocument" />
public sealed record RawPackageDocument
{
    /// <summary>The container entry the document came from — <c>OEBPS/content.opf</c>.</summary>
    public required string EntryName { get; init; }

    /// <summary>The document's bytes, exactly as stored.</summary>
    public required byte[] Bytes { get; init; }
}

/// <summary>
/// What repairing a package document's namespace declarations would produce.
/// </summary>
public sealed record NamespaceRepairResult
{
    /// <summary>The document's bytes with the missing declarations added.</summary>
    public required byte[] RepairedBytes { get; init; }

    /// <summary>
    /// Whether the repaired document parses as well-formed, namespace-correct XML.
    /// </summary>
    public required bool IsComplete { get; init; }

    /// <summary>Prefixes that were declared, in first-use order.</summary>
    public required IReadOnlyList<string> Added { get; init; }

    /// <summary>The line of the first undeclared prefix, 1-based.</summary>
    public int Line { get; init; }

    /// <summary>The column of the first undeclared prefix, 1-based.</summary>
    public int Column { get; init; }

    /// <summary>
    /// Prefixes left alone because no specification says what they mean.
    /// </summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>
    /// Why the repaired document still does not parse, or <see langword="null"/>
    /// when it does.
    /// </summary>
    public string? RemainingError { get; init; }

    /// <summary>Whether anything was changed.</summary>
    public bool HasChanges => Added.Count > 0;
}

/// <summary>
/// The one repair an EPUB read performs: supplying namespace declarations the
/// package document uses but never declares (EPUB-W070).
/// </summary>
/// <remarks>
/// The third face of <see cref="EpubFormat"/>, beside <c>EpubFormat.cs</c> and
/// <c>EpubFormat.Rules.cs</c> and the same class as both. It lives here rather
/// than at the Core root because every prefix it knows how to bind is an EPUB
/// prefix, the rule it answers is an <c>EPUB-</c> rule, and nothing outside this
/// format has ever called it — a general-purpose XML repair is what it looked
/// like, not what it is.
/// <para>
/// The repair is an insertion into the original text, never a reserialisation.
/// Parsing permissively and re-emitting through a strict writer would fix the
/// document and rewrite every line of it doing so, which is invariant 16.
/// </para>
/// </remarks>
public sealed partial class EpubFormat
{
    /// <summary>
    /// Namespace URIs a missing declaration can be recovered from, by prefix.
    /// </summary>
    /// <remarks>
    /// Every entry is fixed by a published specification. A prefix absent from
    /// here is reported and never bound: inventing a plausible URI would fabricate
    /// metadata that was never in the file, and the user would have no reason to
    /// doubt it.
    /// </remarks>
    private static readonly Dictionary<string, string> KnownNamespaces = new(StringComparer.Ordinal)
    {
        ["opf"] = "http://www.idpf.org/2007/opf",
        ["dc"] = "http://purl.org/dc/elements/1.1/",
        ["dcterms"] = "http://purl.org/dc/terms/",
        ["epub"] = "http://www.idpf.org/2007/ops",
        ["xhtml"] = "http://www.w3.org/1999/xhtml",
        ["xsi"] = "http://www.w3.org/2001/XMLSchema-instance",
        ["xlink"] = "http://www.w3.org/1999/xlink",
        ["svg"] = "http://www.w3.org/2000/svg",
        ["ncx"] = "http://www.daisy.org/z3986/2005/ncx/",
        ["ocf"] = "urn:oasis:names:tc:opendocument:xmlns:container",
        ["oebpf"] = "http://openebook.org/namespaces/oeb-package/1.0/",
    };

    /// <summary>Whether a missing declaration for this prefix can be recovered.</summary>
    /// <param name="prefix">The prefix, without the colon.</param>
    /// <returns><see langword="true"/> when a specification fixes the URI.</returns>
    public static bool IsKnownNamespacePrefix(string prefix) =>
        prefix is not null && KnownNamespaces.ContainsKey(prefix);

    /// <summary>
    /// Repairs a package document's missing namespace declarations.
    /// </summary>
    /// <param name="bytes">The document's bytes.</param>
    /// <returns>
    /// The result, or <see langword="null"/> when every prefix the document uses
    /// is declared and there is nothing to repair.
    /// </returns>
    public static NamespaceRepairResult? RepairNamespaces(ReadOnlySpan<byte> bytes)
    {
        XmlEncodingInfo encoding = XmlEncodingDetector.Detect(bytes);
        string text = XmlEncodingDetector.Decode(bytes, encoding);

        List<Undeclared> undeclared = FindUndeclared(text, out bool reachedEnd, out string? stoppedBecause);

        if (undeclared.Count == 0)
        {
            return null;
        }

        List<string> added = [];
        List<string> skipped = [];
        string repairedText = text;

        foreach (Undeclared use in undeclared)
        {
            if (IsKnownNamespacePrefix(use.Prefix))
            {
                added.Add(use.Prefix);
            }
            else
            {
                skipped.Add(use.Prefix);
            }
        }

        if (added.Count > 0 && FindRootTagInsertionPoint(text, out int insertAt))
        {
            // One insertion carrying every recoverable declaration. The root
            // element is where the format expects them and where a single edit
            // covers the whole document, however many prefixes are missing.
            var declarations = new StringBuilder();
            foreach (string prefix in added)
            {
                declarations.Append(" xmlns:").Append(prefix)
                            .Append("=\"").Append(KnownNamespaces[prefix]).Append('"');
            }

            repairedText = text.Insert(insertAt, declarations.ToString());
        }
        else
        {
            added.Clear();
        }

        string? remaining = StrictParseError(repairedText);

        // The scan stopping early means there is more wrong than namespaces, and
        // any prefix after the break was never seen — so completeness cannot be
        // claimed even if what we changed does parse.
        if (remaining is null && !reachedEnd)
        {
            remaining = stoppedBecause;
        }

        return new NamespaceRepairResult
        {
            RepairedBytes = added.Count == 0 ? bytes.ToArray() : XmlEncodingDetector.Encode(repairedText, encoding),
            IsComplete = remaining is null && skipped.Count == 0,
            Added = added,
            Skipped = skipped,
            RemainingError = remaining,
            Line = undeclared[0].Line,
            Column = undeclared[0].Column,
        };
    }

    /// <summary>One prefix used without a declaration, and where it was first seen.</summary>
    private readonly record struct Undeclared(string Prefix, int Line, int Column);

    /// <summary>
    /// Finds prefixes used on an element or attribute name that no
    /// <c>xmlns:</c> declaration binds.
    /// </summary>
    private static List<Undeclared> FindUndeclared(
        string text, out bool reachedEnd, out string? stoppedBecause)
    {
        var used = new List<Undeclared>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);

        reachedEnd = false;
        stoppedBecause = null;

        using var stringReader = new StringReader(text);
        using var reader = new XmlTextReader(stringReader)
        {
            Namespaces = false,

            // Never fetch an external DTD. A malicious or merely broken document
            // could otherwise make this reach out over the network.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };

        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                Record(reader.Name, reader, used, seen);

                if (!reader.HasAttributes)
                {
                    continue;
                }

                while (reader.MoveToNextAttribute())
                {
                    string name = reader.Name;

                    if (name.Equals("xmlns", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (name.StartsWith("xmlns:", StringComparison.Ordinal))
                    {
                        declared.Add(name.Substring("xmlns:".Length));
                        continue;
                    }

                    Record(name, reader, used, seen);
                }

                reader.MoveToElement();
            }

            reachedEnd = true;
        }
        catch (XmlException ex)
        {
            // Something beyond a namespace problem — an unclosed tag, a bare
            // ampersand. What was found so far is still true; the caller needs to
            // know the picture is incomplete.
            stoppedBecause = ex.Message;
        }

        return used.Where(u => !declared.Contains(u.Prefix)).ToList();
    }

    private static void Record(
        string qualifiedName, XmlTextReader reader, List<Undeclared> used, HashSet<string> seen)
    {
        int colon = qualifiedName.IndexOf(':');
        if (colon <= 0)
        {
            return;
        }

        string prefix = qualifiedName.Substring(0, colon);

        // Both are bound by the XML specification, so using them without a
        // declaration is correct and needs no repair.
        if (prefix.Equals("xml", StringComparison.Ordinal) ||
            prefix.Equals("xmlns", StringComparison.Ordinal))
        {
            return;
        }

        if (seen.Add(prefix))
        {
            used.Add(new Undeclared(prefix, reader.LineNumber, reader.LinePosition));
        }
    }

    /// <summary>
    /// Finds the offset in the root element's start tag at which an attribute may
    /// be inserted.
    /// </summary>
    private static bool FindRootTagInsertionPoint(string text, out int insertAt)
    {
        insertAt = 0;
        int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }

            if (Peek(text, i + 1) == '?')
            {
                int close = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return false;
                }

                i = close + 2;
                continue;
            }

            if (Peek(text, i + 1) == '!')
            {
                if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
                {
                    int close = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return false;
                    }

                    i = close + 3;
                    continue;
                }

                i = SkipDoctype(text, i);
                if (i < 0)
                {
                    return false;
                }

                continue;
            }

            char first = Peek(text, i + 1);
            if (char.IsLetter(first) || first is '_' or ':')
            {
                return EndOfStartTag(text, i, out insertAt);
            }

            i++;
        }

        return false;
    }

    private static bool EndOfStartTag(string text, int start, out int insertAt)
    {
        insertAt = 0;

        int nameEnd = start + 1;
        while (nameEnd < text.Length && IsNameChar(text[nameEnd]))
        {
            nameEnd++;
        }

        char quote = '\0';
        for (int i = nameEnd; i < text.Length; i++)
        {
            char c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c != '>')
            {
                continue;
            }

            // Insert before the '/' of a self-closing tag, and before any
            // whitespace preceding the '>', so the result reads naturally.
            int at = i;
            if (at > nameEnd && text[at - 1] == '/')
            {
                at--;
            }

            while (at > nameEnd && char.IsWhiteSpace(text[at - 1]))
            {
                at--;
            }

            insertAt = at;
            return true;
        }

        // Unterminated start tag: broken well beyond a missing declaration, so
        // report nothing rather than guess where it ends.
        return false;
    }

    private static int SkipDoctype(string text, int start)
    {
        int depth = 0;
        char quote = '\0';

        for (int i = start + 2; i < text.Length; i++)
        {
            char c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    break;
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case '>' when depth <= 0:
                    return i + 1;
            }
        }

        return -1;
    }

    private static string? StrictParseError(string text)
    {
        try
        {
            XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            return null;
        }
        catch (XmlException ex)
        {
            return ex.Message;
        }
    }

    private static char Peek(string text, int index) =>
        index < text.Length ? text[index] : '\0';

    private static bool IsNameChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or ':' or '-' or '.';
}

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
            BookDate parsed = BookDate.Parse(date.Value.Trim());

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
            metadata.ModificationDate ??= BookDate.Parse(modifiedMeta.Value.Trim());
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
}

/// <summary>
/// The write half of <see cref="OpfDocument"/>: applying edits to the parsed
/// tree and serialising it back without disturbing anything else.
/// </summary>
public sealed partial class OpfDocument
{
    /// <summary>
    /// Serialises the document back to bytes.
    /// </summary>
    /// <returns>The complete package document.</returns>
    public byte[] Serialize() => Format.Compose(Document.Root);

    /// <summary>
    /// Applies metadata to the document, writing both EPUB 2 and EPUB 3
    /// conventions.
    /// </summary>
    /// <param name="metadata">The metadata to write.</param>
    public void ApplyMetadata(BookMetadata metadata)
    {
        Throw.IfNull(metadata);

        XElement metadataElement = Metadata
            ?? throw new BookFormatException("The package has no metadata element.", EntryName);

        // Compare against the document as it currently stands and touch only
        // what actually differs.
        //
        // This is what reconciles two invariants that would otherwise conflict:
        // "always write both EPUB 2 and EPUB 3 conventions" and "saving without
        // editing yields identical bytes". A file carrying only the EPUB 3 form
        // would otherwise gain EPUB 2 attributes merely by being opened and
        // saved, which is a change the user did not ask for. So dual-convention
        // output applies to fields the user changed; a field left alone is left
        // alone. Reporting single-convention files is EPUB-W032 and EPUB-W061's
        // job, with the user opting in to the fix.
        BookMetadata current = ReadMetadata();

        ApplyTitle(metadataElement, current, metadata);
        ApplySimple(metadataElement, "language", current.Language, metadata.Language);
        ApplySimple(metadataElement, "publisher", current.Publisher, metadata.Publisher);
        ApplySimple(metadataElement, "description", current.Description, metadata.Description);
        ApplySimple(metadataElement, "rights", current.Rights, metadata.Rights);
        ApplyDate(metadataElement, current, metadata);
        ApplySubjects(metadataElement, current, metadata);
        ApplyCreators(metadataElement, current, metadata);
        ApplySeries(metadataElement, current, metadata);

        InvalidateCaches();
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    private void ApplyTitle(XElement metadataElement, BookMetadata current, BookMetadata metadata)
    {
        XElement? title = DcElements("title").FirstOrDefault();

        if (metadata.Title is null)
        {
            if (title is null)
            {
                return;
            }

            // Removed rather than ignored. A cleared field that quietly keeps its
            // old value is the worst of the three options: the user is told nothing
            // and the editor then disagrees with the file. The publication is now
            // missing a required element, which is what rule EPUB-E012 is for.
            Log.Warning(
                $"The title was cleared, so '{EntryName}' no longer has a dc:title. "
                + "EPUB requires one, and readers will show the file name instead.");

            RemoveRefinement(title, null);
            RemoveWithWhitespace(title);
            return;
        }

        if (title is null)
        {
            title = AddDcElement(metadataElement, "title", position: 0);
            SetValue(title, metadata.Title);
        }
        else if (!Same(current.Title, metadata.Title))
        {
            SetValue(title, metadata.Title);
        }

        if (!Same(current.SortTitle, metadata.SortTitle))
        {
            ApplyFileAs(metadataElement, title, metadata.SortTitle, "title");
        }
    }

    /// <summary>
    /// Writes a sort form in both conventions: an <c>opf:file-as</c> attribute
    /// for EPUB 2 readers and a <c>meta refines</c> element for EPUB 3 ones.
    /// </summary>
    private void ApplyFileAs(XElement metadataElement, XElement target, string? sortForm, string idHint)
    {
        if (sortForm is null)
        {
            target.Attribute(OpfNs + "file-as")?.Remove();
            RemoveRefinement(target, "file-as");
            return;
        }

        target.SetAttributeValue(OpfNs + "file-as", sortForm);
        SetRefinement(metadataElement, target, "file-as", sortForm, idHint, scheme: null);
    }

    /// <summary>
    /// Whether either cover convention already names the given manifest item.
    /// </summary>
    /// <param name="manifestItemId">The manifest <c>id</c> to check for.</param>
    /// <returns>
    /// <see langword="true"/> when writing the declaration would change nothing.
    /// </returns>
    /// <remarks>
    /// Lets a save skip touching the cover declarations entirely when they are
    /// already correct, which is what keeps an unedited save byte-identical.
    /// </remarks>
    public bool CoverIsAlreadyDeclaredAs(string manifestItemId)
    {
        Throw.IfNullOrEmpty(manifestItemId);

        bool epub3 = Manifest.Any(i =>
            i.IsCoverImage && string.Equals(i.Id, manifestItemId, StringComparison.Ordinal));

        bool epub2 = Metadata?
            .Elements()
            .Any(e => e.Name.LocalName == "meta" &&
                      (string?)e.Attribute("name") == "cover" &&
                      (string?)e.Attribute("content") == manifestItemId) == true;

        // Either form on its own counts as "already declared" for the purpose of
        // not rewriting an untouched file. A file carrying only one is reported
        // by EPUB-W032 rather than silently corrected on save.
        return epub3 || epub2;
    }

    /// <summary>
    /// Declares a manifest item as the cover in both conventions.
    /// </summary>
    /// <param name="manifestItemId">The manifest <c>id</c> of the cover image.</param>
    public void ApplyCoverDeclaration(string manifestItemId)
    {
        Throw.IfNullOrEmpty(manifestItemId);

        if (Metadata is null)
        {
            return;
        }

        SetLegacyMeta(Metadata, "cover", manifestItemId);

        foreach (ManifestItem item in Manifest)
        {
            bool isCover = string.Equals(item.Id, manifestItemId, StringComparison.Ordinal);
            string[] properties = (item.Properties ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !string.Equals(p, "cover-image", StringComparison.Ordinal))
                .ToArray();

            if (isCover)
            {
                properties = properties.Concat(new[] { "cover-image" }).ToArray();
            }

            if (properties.Length == 0)
            {
                item.Element.Attribute("properties")?.Remove();
            }
            else
            {
                item.Element.SetAttributeValue("properties", string.Join(" ", properties));
            }
        }

        InvalidateCaches();
    }

    private void ApplyCreators(XElement metadataElement, BookMetadata current, BookMetadata metadata)
    {
        if (SameCreators(current.Creators, metadata.Creators))
        {
            return;
        }

        ApplyCreatorSet(metadataElement, metadata, CreatorKind.Creator, "creator");
        ApplyCreatorSet(metadataElement, metadata, CreatorKind.Contributor, "contributor");
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
                !Same(a[i].SortName, b[i].SortName) ||
                !Same(a[i].NativeRole ?? a[i].Role, b[i].NativeRole ?? b[i].Role) ||
                a[i].Kind != b[i].Kind)
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyCreatorSet(
        XElement metadataElement, BookMetadata metadata, CreatorKind kind, string localName)
    {
        List<Creator> wanted = metadata.Creators.Where(c => c.Kind == kind).ToList();
        List<XElement> existing = DcElements(localName).ToList();

        for (int i = 0; i < wanted.Count; i++)
        {
            Creator creator = wanted[i];
            XElement element = i < existing.Count
                ? existing[i]
                : AddDcElement(metadataElement, localName, position: null);

            SetValue(element, creator.Name);
            ApplyFileAs(metadataElement, element, creator.SortName, $"{localName}{i + 1}");

            // Prefer the native role string over the mapped relator: it is what
            // the originating format's readers expect, and round-tripping
            // through the mapping would degrade it.
            string? role = creator.NativeRole ?? creator.Role;

            if (role is null)
            {
                element.Attribute(OpfNs + "role")?.Remove();
                RemoveRefinement(element, "role");
            }
            else
            {
                element.SetAttributeValue(OpfNs + "role", role);
                SetRefinement(
                    metadataElement, element, "role", role, $"{localName}{i + 1}", "marc:relators");
            }
        }

        // Anything left over was deleted by the user.
        for (int i = wanted.Count; i < existing.Count; i++)
        {
            RemoveRefinement(existing[i], null);
            RemoveWithWhitespace(existing[i]);
        }
    }

    private void ApplySeries(XElement metadataElement, BookMetadata current, BookMetadata metadata)
    {
        SeriesInfo? series = metadata.Series;

        if (Same(current.Series?.Name, series?.Name) &&
            current.Series?.Index == series?.Index &&
            Same(current.Series?.RawIndex, series?.RawIndex))
        {
            return;
        }

        string? index = series?.Index is { } value
            // Invariant culture: a French locale would write "2,5", which no
            // reader parses.
            ? value.ToString("0.############", CultureInfo.InvariantCulture)
            : series?.RawIndex;

        // EPUB 2 form — calibre's, and what most files in the wild actually use.
        SetLegacyMeta(metadataElement, "calibre:series", series?.Name);
        SetLegacyMeta(metadataElement, "calibre:series_index", index);

        // EPUB 3 form.
        XElement? collection = metadataElement
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta" &&
                                 (string?)e.Attribute("property") == "belongs-to-collection");

        if (series is null)
        {
            if (collection is not null)
            {
                RemoveRefinement(collection, null);
                RemoveWithWhitespace(collection);
            }

            return;
        }

        if (collection is null)
        {
            collection = new XElement(metadataElement.Name.Namespace + "meta",
                new XAttribute("property", "belongs-to-collection"));
            Append(metadataElement, collection);
        }

        SetValue(collection, series.Name);
        SetRefinement(metadataElement, collection, "collection-type", "series", "collection", null);

        if (index is null)
        {
            RemoveRefinement(collection, "group-position");
        }
        else
        {
            SetRefinement(metadataElement, collection, "group-position", index, "collection", null);
        }
    }

    private void ApplySubjects(XElement metadataElement, BookMetadata current, BookMetadata metadata)
    {
        if (current.Subjects.SequenceEqual(metadata.Subjects, StringComparer.Ordinal))
        {
            return;
        }

        List<XElement> existing = DcElements("subject").ToList();

        for (int i = 0; i < metadata.Subjects.Count; i++)
        {
            XElement element = i < existing.Count
                ? existing[i]
                : AddDcElement(metadataElement, "subject", position: null);

            SetValue(element, metadata.Subjects[i]);
        }

        for (int i = metadata.Subjects.Count; i < existing.Count; i++)
        {
            RemoveWithWhitespace(existing[i]);
        }
    }

    private void ApplyDate(XElement metadataElement, BookMetadata current, BookMetadata metadata)
    {
        if (Same(current.PublicationDate?.Raw, metadata.PublicationDate?.Raw))
        {
            return;
        }

        XElement? date = DcElements("date")
            .FirstOrDefault(e => (string?)e.Attribute(OpfNs + "event") is null or "publication");

        if (metadata.PublicationDate is null)
        {
            // Cleared, so the element goes. Unlike the title, nothing requires a
            // publication date, so this needs no warning.
            if (date is not null)
            {
                RemoveWithWhitespace(date);
            }

            return;
        }

        date ??= AddDcElement(metadataElement, "date", position: null);

        // The raw text is authoritative: a source that said "2013" must not be
        // rewritten as "2013-01-01", which would assert a day it never claimed.
        SetValue(date, metadata.PublicationDate.Raw);
    }

    private void ApplySimple(XElement metadataElement, string localName, string? currentValue, string? value)
    {
        if (Same(currentValue, value))
        {
            return;
        }

        XElement? element = DcElements(localName).FirstOrDefault();

        if (string.IsNullOrEmpty(value))
        {
            if (element is not null)
            {
                RemoveWithWhitespace(element);
            }

            return;
        }

        element ??= AddDcElement(metadataElement, localName, position: null);
        SetValue(element, value!);
    }

    /// <summary>
    /// Sets an element's text without disturbing it when nothing changed, so an
    /// untouched field contributes nothing to the diff.
    /// </summary>
    private static void SetValue(XElement element, string value)
    {
        if (!string.Equals(element.Value, value, StringComparison.Ordinal))
        {
            element.SetValue(value);
        }
    }

    private void SetLegacyMeta(XElement metadataElement, string name, string? content)
    {
        XElement? meta = metadataElement
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == name);

        if (content is null)
        {
            if (meta is not null)
            {
                RemoveWithWhitespace(meta);
            }

            return;
        }

        if (meta is null)
        {
            meta = new XElement(metadataElement.Name.Namespace + "meta", new XAttribute("name", name));
            Append(metadataElement, meta);
        }

        meta.SetAttributeValue("content", content);
    }

    /// <summary>
    /// Creates or updates an EPUB 3 <c>&lt;meta refines&gt;</c> pointing at
    /// <paramref name="target"/>, giving the target an id if it lacks one.
    /// </summary>
    private void SetRefinement(
        XElement metadataElement, XElement target, string property, string value,
        string idHint, string? scheme)
    {
        string id = (string?)target.Attribute("id") ?? MakeUniqueId(idHint);
        target.SetAttributeValue("id", id);

        XElement? refinement = metadataElement
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta" &&
                                 (string?)e.Attribute("property") == property &&
                                 ((string?)e.Attribute("refines"))?.TrimStart('#') == id);

        if (refinement is null)
        {
            refinement = new XElement(metadataElement.Name.Namespace + "meta",
                new XAttribute("refines", "#" + id),
                new XAttribute("property", property));

            if (scheme is not null)
            {
                refinement.SetAttributeValue("scheme", scheme);
            }

            // Placed immediately after what it refines, which is where a reader
            // expects to find it and keeps the diff local.
            target.AddAfterSelf(refinement);
            InsertSeparatorBefore(refinement, target);
        }

        SetValue(refinement, value);
    }

    private void RemoveRefinement(XElement target, string? property)
    {
        string? id = (string?)target.Attribute("id");
        if (id is null || Metadata is null)
        {
            return;
        }

        List<XElement> doomed = Metadata
            .Elements()
            .Where(e => e.Name.LocalName == "meta" &&
                        ((string?)e.Attribute("refines"))?.TrimStart('#') == id &&
                        (property is null || (string?)e.Attribute("property") == property))
            .ToList();

        foreach (XElement element in doomed)
        {
            RemoveWithWhitespace(element);
        }
    }

    private string MakeUniqueId(string hint)
    {
        var taken = new HashSet<string>(
            Document.Descendants()
                .Select(e => (string?)e.Attribute("id"))
                .Where(id => id is not null)
                .Select(id => id!),
            StringComparer.Ordinal);

        if (!taken.Contains(hint))
        {
            return hint;
        }

        for (int i = 2; ; i++)
        {
            string candidate = hint + i.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private XElement AddDcElement(XElement metadataElement, string localName, int? position)
    {
        // DcNs rather than a literal prefix: XDocument emits whichever prefix is
        // already bound in the source, so this never invents one.
        var element = new XElement(DcNs + localName);

        if (position == 0 && metadataElement.Elements().Any())
        {
            XElement first = metadataElement.Elements().First();
            first.AddBeforeSelf(element);
            InsertSeparatorBefore(first, element);
        }
        else
        {
            Append(metadataElement, element);
        }

        return element;
    }

    /// <summary>
    /// Appends a child, copying the indentation already used inside the parent
    /// so a generated element lines up with its hand-written neighbours.
    /// </summary>
    private static void Append(XElement parent, XElement child)
    {
        string indent = DetectIndent(parent);
        parent.Add(new XText(indent), child);
    }

    private static void InsertSeparatorBefore(XElement element, XElement reference)
    {
        string indent = reference.Parent is null ? "\n" : DetectIndent(reference.Parent);
        element.AddBeforeSelf(new XText(indent));
    }

    private static string DetectIndent(XElement parent)
    {
        // The whitespace before the first child is the parent's indentation
        // style, whatever it happens to be. Guessing two spaces instead would
        // make generated elements visibly foreign in a file that uses tabs.
        if (parent.FirstNode is XText text && text.Value.Contains('\n'))
        {
            return text.Value;
        }

        return "\n    ";
    }

    private static void RemoveWithWhitespace(XElement element)
    {
        // Take the whitespace that preceded the element too, otherwise deleting
        // a field leaves a blank line behind and the diff shows two changes.
        if (element.PreviousNode is XText text && text.Value.Trim().Length == 0)
        {
            text.Remove();
        }

        element.Remove();
    }

    private void InvalidateCaches()
    {
        _manifest = null;
        _spine = null;
        _refinements = null;
    }
}

/// <summary>
/// <c>META-INF/container.xml</c> — the file that says where an EPUB's package
/// document lives.
/// </summary>
public sealed class ContainerXml
{
    /// <summary>The entry name, which the EPUB specification fixes.</summary>
    public const string EntryName = "META-INF/container.xml";

    private ContainerXml(IReadOnlyList<string> rootfilePaths)
    {
        RootfilePaths = rootfilePaths;
    }

    /// <summary>
    /// The <c>full-path</c> of every declared rootfile, in document order.
    /// </summary>
    public IReadOnlyList<string> RootfilePaths { get; }

    /// <summary>
    /// The package document path to edit, or <see langword="null"/> if none was
    /// declared.
    /// </summary>
    public string? PrimaryRootfilePath => RootfilePaths.Count > 0 ? RootfilePaths[0] : null;

    /// <summary>Parses <c>META-INF/container.xml</c> from its bytes.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>The parsed container description.</returns>
    /// <exception cref="BookFormatException">
    /// The document is not well-formed. Surfaced as EPUB-F002.
    /// </exception>
    public static ContainerXml Parse(ReadOnlySpan<byte> bytes)
    {
        XmlEncodingInfo encoding = XmlEncodingDetector.Detect(bytes);
        string text = XmlEncodingDetector.Decode(bytes, encoding);

        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new BookFormatException(
                $"'{EntryName}' is not well-formed XML: {ex.Message}", EntryName, ex);
        }

        // Match the rootfile elements namespace-agnostically. The container
        // namespace is fixed by spec, but files that omit or misspell it are
        // still readable and refusing them would help nobody.
        List<string> paths = [.. document
            .Descendants()
            .Where(e => e.Name.LocalName == "rootfile")
            .Select(e => (string?)e.Attribute("full-path"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)];

        return new ContainerXml(paths);
    }

    /// <summary>Reads and parses <c>META-INF/container.xml</c> from a container.</summary>
    /// <param name="container">The open EPUB container.</param>
    /// <returns>The parsed container description.</returns>
    /// <exception cref="BookFormatException">
    /// The entry is missing or not well-formed. Surfaced as EPUB-F002.
    /// </exception>
    public static ContainerXml Read(IContainer container)
    {
        Throw.IfNull(container);

        ContainerEntry? entry = container.Entries.FirstOrDefault(
            e => e.Name.Equals(EntryName, StringComparison.Ordinal));

        // Some producers get the casing wrong. Accept it on read and report it,
        // rather than declaring the book unopenable over a capital letter.
        entry ??= container.Entries.FirstOrDefault(
            e => e.Name.Equals(EntryName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new BookFormatException($"'{EntryName}' is missing.", EntryName);
        }

        return Parse(container.ReadAllBytes(entry));
    }

}
