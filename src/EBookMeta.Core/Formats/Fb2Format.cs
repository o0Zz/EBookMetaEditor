using System.Xml;
using EBookMeta.Containers;
using EBookMeta.Documents;
using EBookMeta.Model;

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
/// <para>
/// The rules live beside this in <c>Fb2Format.Rules.cs</c>, which is the same
/// class.
/// </para>
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
        IContainer container, ReadOptions? options = null, ICollection<Finding>? findings = null)
    {
        Throw.IfNull(container);

        options ??= ReadOptions.Default;

        ContainerEntry entry = FindDocument(container, findings);
        Fb2Document document = Parse(container, entry, findings);
        BookMetadata metadata = document.ReadMetadata();

        if (options.IncludeCover)
        {
            ReadCover(container, entry, document, metadata, findings);
        }

        if (findings is not null)
        {
            CheckRequiredMetadata(document, metadata, findings);
            CheckEncoding(document, findings);
            CheckCover(document, metadata, options, findings);
        }

        Log.Info(
            $"Read FictionBook metadata from '{entry.Name}': "
            + $"title={Describe(metadata.Title)}, creators={metadata.Creators.Count}, "
            + $"series={Describe(metadata.Series?.Name)}.");

        return metadata;
    }

    private static string Describe(string? value) => value is null ? "(none)" : $"\"{value}\"";

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

        ContainerEntry entry = FindDocument(container, findings: null);
        Fb2Document document = Parse(container, entry, findings);

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
        IContainer container, ICollection<Finding>? findings)
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

        if (candidates.Count > 1)
        {
            findings?.Add(new Finding
            {
                RuleId = "FB2-W020",
                Severity = Severity.Warning,
                Message = $"The archive holds {candidates.Count} FictionBook documents; "
                    + $"'{candidates[0].Name}' is the one being edited.",
                Detail = string.Join(", ", candidates.Select(c => c.Name)),
            });
        }

        return candidates[0];
    }

    /// <summary>
    /// Parses the document, reporting FB2-F001 or FB2-F002 before giving up.
    /// </summary>
    private static Fb2Document Parse(
        IContainer container, ContainerEntry entry, ICollection<Finding>? findings)
    {
        try
        {
            return Fb2Document.Parse(ReadAllBytes(container, entry), entry.Name);
        }
        catch (BookFormatException ex)
        {
            findings?.Add(new Finding
            {
                RuleId = ex.Message.Contains("<description>", StringComparison.Ordinal)
                    ? "FB2-F002"
                    : "FB2-F001",
                Severity = Severity.Fatal,
                Message = ex.Message,
                Location = entry.Name,
            });

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
        BookMetadata metadata,
        ICollection<Finding>? findings)
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
            // A cover that will not decode is not a reason to refuse the file. The
            // metadata is all still readable, and the rule below says what is wrong.
            findings?.Add(new Finding
            {
                RuleId = "FB2-W031",
                Severity = Severity.Warning,
                Message = $"The cover image '{id}' could not be decoded: {ex.Message}",
                Location = entry.Name,
            });
        }
    }

    private static byte[] ReadAllBytes(IContainer container, ContainerEntry entry)
    {
        using Stream stream = container.OpenRead(entry);
        using var buffer = new MemoryStream(entry.Length > 0 ? (int)entry.Length : 4096);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
