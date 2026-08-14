using System.Text;
using EBookMeta.Containers;
using EBookMeta.Documents;

namespace EBookMeta.Formats;

/// <summary>
/// The EPUB validation rules, by stable rule ID.
/// </summary>
/// <remarks>
/// Every rule here runs on every read, which is affordable because of what they
/// read: the package document is already parsed, and the cross-checks against the
/// archive compare entry names the ZIP central directory has already supplied.
/// Nothing is decompressed, so a 500-entry EPUB costs no more to check than a
/// three-entry one.
/// <para>
/// A rule goes where its evidence is. These are the ones answerable from the
/// parsed document and from entry names, so they belong to the read. A defect a
/// write can prove and fix — <c>mimetype</c> in the wrong place — is corrected in
/// <c>EpubFormat.cs</c> instead, and reports what it changed.
/// </para>
/// </remarks>
public sealed partial class EpubFormat
{
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
}
