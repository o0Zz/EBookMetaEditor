using System.Text;
using EBookMeta.Containers;
using EBookMeta.Documents;
using EBookMeta.Model;

namespace EBookMeta.Formats;

/// <summary>
/// Reads and writes EPUB 2 and EPUB 3 metadata.
/// </summary>
public sealed class EpubHandler : IFormatHandler
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

    /// <summary>
    /// Opens the package document referenced by <c>META-INF/container.xml</c>.
    /// </summary>
    /// <param name="container">The open EPUB container.</param>
    /// <returns>The parsed package document and the entry it came from.</returns>
    /// <exception cref="BookFormatException">
    /// The container file or the package document is missing or malformed.
    /// </exception>
    /// <param name="findings">
    /// Collects a namespace repair as rule EPUB-W070, so the correction is on the
    /// record. <see langword="null"/> discards it.
    /// </param>
    public static OpfDocument OpenPackageDocument(
        IContainer container, ICollection<Finding>? findings = null)
    {
        Throw.IfNull(container);

        ContainerXml containerXml = ContainerXml.Read(container);
        string? opfPath = containerXml.PrimaryRootfilePath;

        if (opfPath is null)
        {
            throw new BookFormatException(
                $"'{ContainerXml.EntryName}' declares no rootfile.", ContainerXml.EntryName);
        }

        ContainerEntry? entry = FindEntry(container, opfPath);
        if (entry is null)
        {
            throw new BookFormatException(
                $"'{ContainerXml.EntryName}' points at '{opfPath}', which is not in the archive.",
                ContainerXml.EntryName);
        }

        byte[] bytes = ReadAllBytes(container, entry);

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
            NamespaceRepairResult? repair = NamespaceRepair.Repair(bytes, entry.Name);

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

            // Reported as a finding rather than logged here, so that a repair
            // reaches the log by the same route as every other rule and cannot
            // appear twice. Severity.Warning is what puts it in the log as a
            // warning, which invariant 14 requires of every repair.
            findings?.Add(new Finding
            {
                RuleId = NamespaceRepair.RuleId,
                Severity = Severity.Warning,
                Message =
                    $"'{entry.Name}' was missing namespace declarations and has been repaired "
                    + $"in memory: added xmlns for {string.Join(", ", repair.Added)}. "
                    + "Save to keep the correction.",
                Location = entry.Name,
                HasAutofix = true,
            });

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

        ContainerXml containerXml = ContainerXml.Read(container);
        string? opfPath = containerXml.PrimaryRootfilePath;

        if (opfPath is null)
        {
            throw new BookFormatException(
                $"'{ContainerXml.EntryName}' declares no rootfile.", ContainerXml.EntryName);
        }

        ContainerEntry? entry = FindEntry(container, opfPath);
        if (entry is null)
        {
            throw new BookFormatException(
                $"'{ContainerXml.EntryName}' points at '{opfPath}', which is not in the archive.",
                ContainerXml.EntryName);
        }

        return new RawPackageDocument
        {
            EntryName = entry.Name,
            Bytes = ReadAllBytes(container, entry),
        };
    }

    /// <inheritdoc />
    public BookMetadata Read(
        IContainer container, ReadOptions? options = null, ICollection<Finding>? findings = null)
    {
        Throw.IfNull(container);

        options ??= ReadOptions.Default;

        OpfDocument opf = OpenPackageDocument(container, findings);
        BookMetadata metadata = opf.ReadMetadata();

        if (options.IncludeCover)
        {
            ReadCover(container, opf, metadata);
        }

        // Checked on every read, because it is all but free: the package document
        // is already parsed and its manifest, spine and refinements are already
        // cached, and the cross-checks against the archive compare entry names the
        // central directory has already supplied.
        if (findings is not null)
        {
            CheckPackage(container, opf, findings);
        }

        Log.Info(
            $"Read EPUB {opf.Version ?? "(unversioned)"} metadata from '{opf.EntryName}': "
            + $"title={Describe(metadata.Title)}, creators={metadata.Creators.Count}, "
            + $"cover={(metadata.Cover is null ? "none" : metadata.Cover.MediaType)}.");

        return metadata;
    }

    private static string Describe(string? value) =>
        value is null ? "(none)" : $"\"{value}\"";

    /// <inheritdoc />
    public void Write(
        IContainer container,
        BookMetadata metadata,
        string targetPath,
        ICollection<Finding>? findings = null)
    {
        Throw.IfNull(container);
        Throw.IfNull(metadata);
        Throw.IfNullOrEmpty(targetPath);

        OpfDocument opf = OpenPackageDocument(container, findings);
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
                entries.Add(PendingEntry.FromBytes(
                    entry.Name, opfBytes, entry.CompressionMethod, entry.LastModified));
            }
            else if (coverBytes is not null &&
                     entry.Name.Equals(coverEntryName, StringComparison.Ordinal))
            {
                entries.Add(PendingEntry.FromBytes(
                    entry.Name, coverBytes, entry.CompressionMethod, entry.LastModified));
            }
            else
            {
                entries.Add(PendingEntry.CopyOf(container, entry));
            }
        }

        RepairMimetype(entries, findings);

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
    private static void RepairMimetype(List<PendingEntry> entries, ICollection<Finding>? findings)
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

        findings?.Add(new Finding
        {
            RuleId = "EPUB-E040",
            Severity = Severity.Warning,
            Message = existing is null
                ? $"'{MimetypeEntryName}' was missing; written as the first entry, stored."
                : wrongPlace
                    ? $"'{MimetypeEntryName}' was not the first entry; moved to the front on save."
                    : $"'{MimetypeEntryName}' was compressed; stored on save.",
            Location = MimetypeEntryName,
            HasAutofix = true,
        });
    }

    /// <summary>
    /// Checks the package document against itself and against the archive.
    /// </summary>
    private static void CheckPackage(
        IContainer container, OpfDocument opf, ICollection<Finding> findings)
    {
        CheckRequiredMetadata(opf, findings);
        CheckReferences(opf, findings);
        CheckArchive(container, opf, findings);
    }

    /// <summary>
    /// Checks the metadata every EPUB is required to carry.
    /// </summary>
    private static void CheckRequiredMetadata(OpfDocument opf, ICollection<Finding> findings)
    {
        string? location = opf.EntryName;

        if (opf.Encoding is { DeclarationMatchesBytes: false, Mismatch: { } mismatch })
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-E050",
                Severity = Severity.Error,
                Message = $"The declared encoding does not match the bytes: {mismatch}",
                Location = location,
            });
        }

        if (opf.UniqueIdentifierRef is null)
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-E010",
                Severity = Severity.Error,
                Message = "package/@unique-identifier is absent, so nothing says which "
                    + "identifier identifies this book.",
                Location = location,
            });
        }
        else if (!Identifiers(opf).Contains(opf.UniqueIdentifierRef, StringComparer.Ordinal))
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-E011",
                Severity = Severity.Error,
                Message = $"package/@unique-identifier is '{opf.UniqueIdentifierRef}' but no "
                    + "dc:identifier carries that id.",
                Location = location,
                Detail = opf.UniqueIdentifierRef,
            });
        }

        if (string.IsNullOrWhiteSpace(DcValue(opf, "title")))
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-E012",
                Severity = Severity.Error,
                Message = "dc:title is missing or empty.",
                Location = location,
            });
        }

        string? language = DcValue(opf, "language");

        if (string.IsNullOrWhiteSpace(language))
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-E013",
                Severity = Severity.Error,
                Message = "dc:language is missing.",
                Location = location,
            });
        }
        else if (!IsPlausibleLanguageTag(language!))
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-W014",
                Severity = Severity.Warning,
                Message = $"dc:language is '{language}', which is not a plausible BCP 47 tag. "
                    + "Two or three letters, optionally followed by a subtag, is what readers "
                    + "expect — 'en', 'fr', 'pt-BR'.",
                Location = location,
                Detail = language,
            });
        }
    }

    /// <summary>
    /// Checks that the ids the package document points at actually exist in it.
    /// </summary>
    private static void CheckReferences(OpfDocument opf, ICollection<Finding> findings)
    {
        string? location = opf.EntryName;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (ManifestItem item in opf.Manifest)
        {
            if (item.Id is { Length: > 0 } id && !ids.Add(id))
            {
                duplicates.Add(id);
            }
        }

        foreach (string duplicate in duplicates.Distinct(StringComparer.Ordinal))
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-E022",
                Severity = Severity.Error,
                Message = $"Two manifest items share the id '{duplicate}', so any reference "
                    + "to it is ambiguous.",
                Location = location,
                Detail = duplicate,
            });
        }

        foreach (SpineItemRef itemRef in opf.Spine)
        {
            if (itemRef.IdRef is { Length: > 0 } idRef && !ids.Contains(idRef))
            {
                findings.Add(new Finding
                {
                    RuleId = "EPUB-E020",
                    Severity = Severity.Error,
                    Message = $"The spine refers to '{idRef}', which is not in the manifest, "
                        + "so that part of the reading order does not exist.",
                    Location = location,
                    Detail = idRef,
                });
            }
        }

        foreach (MetaRefinement refinement in opf.Refinements)
        {
            string target = (refinement.Refines ?? string.Empty).TrimStart('#');

            // Refinements point at metadata elements as well as manifest items, so
            // an id that is not in the manifest is only dangling if it is nowhere
            // in the document at all.
            if (target.Length > 0 && !ids.Contains(target) && !ElementIdExists(opf, target))
            {
                findings.Add(new Finding
                {
                    RuleId = "EPUB-W060",
                    Severity = Severity.Warning,
                    Message = $"A meta/@refines points at '{target}', which nothing in the "
                        + "package document declares, so the refinement is ignored.",
                    Location = location,
                    Detail = refinement.Property,
                });
            }
        }

        CheckCoverDeclarations(opf, ids, findings);
        CheckSeriesDeclarations(opf, findings);
    }

    /// <summary>
    /// Checks the two cover conventions against each other and against the manifest.
    /// </summary>
    private static void CheckCoverDeclarations(
        OpfDocument opf, HashSet<string> manifestIds, ICollection<Finding> findings)
    {
        string? location = opf.EntryName;
        string? legacy = CoverMetaContent(opf);
        bool epub3 = opf.Manifest.Any(i => i.IsCoverImage);

        if (legacy is { Length: > 0 } && !manifestIds.Contains(legacy))
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-E030",
                Severity = Severity.Error,
                Message = $"The cover metadata names manifest item '{legacy}', which does "
                    + "not exist.",
                Location = location,
                Detail = legacy,
            });

            return;
        }

        if (legacy is null && !epub3)
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-W031",
                Severity = Severity.Warning,
                Message = "No cover is declared. Saving will not invent one, so shelves will "
                    + "show this book without a thumbnail.",
                Location = location,
            });
        }
        else if (legacy is null || !epub3)
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-W032",
                Severity = Severity.Warning,
                Message = epub3
                    ? "The cover is declared the EPUB 3 way only, so EPUB 2 readers will not "
                        + "find it. Editing the cover writes both conventions."
                    : "The cover is declared the EPUB 2 way only, so EPUB 3 readers may not "
                        + "find it. Editing the cover writes both conventions.",
                Location = location,
            });
        }
    }

    /// <summary>
    /// Checks the two series conventions against each other.
    /// </summary>
    private static void CheckSeriesDeclarations(OpfDocument opf, ICollection<Finding> findings)
    {
        if (opf.Metadata is not { } metadata)
        {
            return;
        }

        bool calibre = metadata
            .Elements(OpfDocument.OpfNs + "meta")
            .Any(m => (string?)m.Attribute("name") == "calibre:series");

        bool epub3 = metadata
            .Elements(OpfDocument.OpfNs + "meta")
            .Any(m => (string?)m.Attribute("property") == "belongs-to-collection");

        if (calibre != epub3)
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-W061",
                Severity = Severity.Warning,
                Message = calibre
                    ? "The series is recorded the calibre way only, so EPUB 3 readers will "
                        + "not see it. Editing the series writes both conventions."
                    : "The series is recorded the EPUB 3 way only, so calibre and EPUB 2 "
                        + "readers will not see it. Editing the series writes both conventions.",
                Location = opf.EntryName,
            });
        }
    }

    /// <summary>
    /// Cross-checks the manifest against what the archive actually holds.
    /// </summary>
    /// <remarks>
    /// Entry names only, compared against resolved hrefs. The single exception is
    /// <c>mimetype</c>, whose twenty bytes have to be read to know whether they say
    /// the right thing — readers reject the file when they do not.
    /// </remarks>
    private static void CheckArchive(
        IContainer container, OpfDocument opf, ICollection<Finding> findings)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ContainerEntry entry in container.Entries)
        {
            if (!entry.IsDirectory)
            {
                present.Add(entry.Name);
            }
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MimetypeEntryName,
            ContainerXml.EntryName,
            opf.EntryName,
        };

        foreach (ManifestItem item in opf.Manifest)
        {
            if (item.Href is not { Length: > 0 } href)
            {
                continue;
            }

            string resolved = ResolveHref(opf.EntryName, href);
            referenced.Add(resolved);

            if (!present.Contains(resolved))
            {
                findings.Add(new Finding
                {
                    RuleId = "EPUB-E021",
                    Severity = Severity.Error,
                    Message = $"The manifest lists '{href}', which is not in the archive.",
                    Location = opf.EntryName,
                    Detail = resolved,
                });
            }
        }

        List<string> orphans = present
            .Where(name => !referenced.Contains(name)
                && !name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (orphans.Count > 0)
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-W023",
                Severity = Severity.Warning,
                Message = $"The archive holds {orphans.Count} file"
                    + (orphans.Count == 1 ? "" : "s")
                    + " the manifest does not list, so readers will ignore "
                    + (orphans.Count == 1 ? "it" : "them") + ".",
                Detail = string.Join(", ", orphans.Take(5)),
            });
        }

        CheckMimetype(container, findings);
    }

    /// <summary>
    /// Checks the one entry whose position and storage are dictated by the spec.
    /// </summary>
    private static void CheckMimetype(IContainer container, ICollection<Finding> findings)
    {
        ContainerEntry? first = container.Entries.Count > 0 ? container.Entries[0] : null;

        if (first is null || !first.Name.Equals(MimetypeEntryName, StringComparison.Ordinal))
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-E040",
                Severity = Severity.Error,
                Message = $"'{MimetypeEntryName}' must be the archive's first entry. "
                    + "Saving puts it back.",
                Location = MimetypeEntryName,
                HasAutofix = true,
            });

            return;
        }

        if (first.CompressionMethod != ZipCompressionMethods.Stored)
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-E040",
                Severity = Severity.Error,
                Message = $"'{MimetypeEntryName}' is compressed and must be stored. "
                    + "Saving stores it.",
                Location = MimetypeEntryName,
                HasAutofix = true,
            });
        }

        string content = Encoding.ASCII.GetString(ReadAllBytes(container, first));

        if (!content.Equals(EpubMediaType, StringComparison.Ordinal))
        {
            findings.Add(new Finding
            {
                RuleId = "EPUB-E040",
                Severity = Severity.Error,
                Message = $"'{MimetypeEntryName}' must contain exactly '{EpubMediaType}', "
                    + $"with no BOM and no trailing newline, but contains '{content.Trim()}'.",
                Location = MimetypeEntryName,
            });
        }
    }

    private static IEnumerable<string> Identifiers(OpfDocument opf) =>
        opf.Metadata is null
            ? []
            : opf.Metadata
                .Elements(OpfDocument.DcNs + "identifier")
                .Select(e => (string?)e.Attribute("id"))
                .Where(id => id is { Length: > 0 })
                .Select(id => id!);

    private static string? DcValue(OpfDocument opf, string localName) =>
        opf.Metadata?.Elements(OpfDocument.DcNs + localName).FirstOrDefault()?.Value;

    private static string? CoverMetaContent(OpfDocument opf) =>
        opf.Metadata
            ?.Elements(OpfDocument.OpfNs + "meta")
            .Where(m => (string?)m.Attribute("name") == "cover")
            .Select(m => (string?)m.Attribute("content"))
            .FirstOrDefault();

    private static bool ElementIdExists(OpfDocument opf, string id) =>
        opf.Package?.Descendants().Any(e => (string?)e.Attribute("id") == id) == true;

    /// <summary>
    /// Whether a language tag is shaped like BCP 47.
    /// </summary>
    private static bool IsPlausibleLanguageTag(string tag)
    {
        string[] parts = tag.Split('-');

        if (parts.Length == 0 || parts[0].Length is < 2 or > 3)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0 || !part.All(char.IsLetterOrDigit))
            {
                return false;
            }
        }

        return true;
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
            Data = ReadAllBytes(container, entry),
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

    private static byte[] ReadAllBytes(IContainer container, ContainerEntry entry)
    {
        using Stream stream = container.OpenRead(entry);
        using var buffer = new MemoryStream(entry.Length > 0 && entry.Length < int.MaxValue
            ? (int)entry.Length
            : 0);

        stream.CopyTo(buffer);
        return buffer.ToArray();
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
