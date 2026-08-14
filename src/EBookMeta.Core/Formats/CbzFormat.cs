using EBookMeta.Containers;
using EBookMeta.Documents;
using EBookMeta.Model;

namespace EBookMeta.Formats;

/// <summary>
/// Reads and writes comic archive metadata: ZIP plus <c>ComicInfo.xml</c>.
/// </summary>
/// <remarks>
/// This file is the <see cref="IBookFormat"/> implementation — reading, writing,
/// and the corrections a write can prove, such as a <c>PageCount</c> recomputed
/// from the images actually present. The validation rules live beside it in
/// <c>CbzFormat.Rules.cs</c>, which is the same class.
/// </remarks>
public sealed partial class CbzFormat : IBookFormat
{
    /// <summary>The CoMet metadata document, read for cross-checking only.</summary>
    private const string CometEntryName = "comet.xml";

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".jxl", ".tif", ".tiff"];

    /// <inheritdoc />
    public FormatId Id => FormatId.Cbz;

    /// <inheritdoc />
    public FormatCapabilities Capabilities { get; } = new()
    {
        Format = FormatId.Cbz,

        // ComicInfo has no sort forms, no identifiers and no rights statement, so
        // those fields stay off. That is the point of declaring capabilities: a
        // user must not type a sort title into a comic and have it silently
        // discarded on save.
        ReadableFields =
            MetadataField.Title | MetadataField.Creators | MetadataField.CreatorRoles |
            MetadataField.Series | MetadataField.SeriesIndex | MetadataField.Description |
            MetadataField.Publisher | MetadataField.PublicationDate | MetadataField.Language |
            MetadataField.Subjects | MetadataField.Cover,

        // Everything readable except the cover. A comic's cover is its first page
        // image, so replacing it means replacing a page — and page-image
        // processing is deliberately out of scope.
        WritableFields =
            MetadataField.Title | MetadataField.Creators | MetadataField.CreatorRoles |
            MetadataField.Series | MetadataField.SeriesIndex | MetadataField.Description |
            MetadataField.Publisher | MetadataField.PublicationDate | MetadataField.Language |
            MetadataField.Subjects,
    };

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// <c>ComicInfo.xml</c> is present but not well-formed (CBZ-F001).
    /// </exception>
    public BookMetadata Read(
        IContainer container, ReadOptions? options = null, ICollection<Finding>? findings = null)
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
            document = Parse(container, entry, findings);
            metadata = document.ReadMetadata();
        }

        if (options.IncludeCover)
        {
            ReadCover(container, metadata);
        }

        // Checked here rather than on request, because none of it costs anything:
        // every rule below reads the parsed document or entry names the central
        // directory already gave us, and nothing is decompressed to do it.
        if (findings is not null)
        {
            CheckLayout(container, entry, findings);
            CheckPages(container, entry, document, findings);
            CheckFields(entry, document, findings);
        }

        Log.Info(
            entry is null
                ? $"Read comic archive metadata: no '{ComicInfoDocument.DefaultEntryName}', "
                    + $"{CountImages(container)} images."
                : $"Read comic archive metadata from '{entry.Name}': "
                    + $"series={Describe(metadata.Series?.Name)}, title={Describe(metadata.Title)}, "
                    + $"creators={metadata.Creators.Count}.");

        return metadata;
    }

    private static string Describe(string? value) => value is null ? "(none)" : $"\"{value}\"";

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// The archive carries a comment — a ComicBookLover blob — which a rebuild
    /// cannot reproduce, or its <c>ComicInfo.xml</c> is not well-formed.
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
            entry is null ? ComicInfoDocument.CreateEmpty() : Parse(container, entry, findings);

        document.ApplyMetadata(metadata);

        int images = CountImages(container);
        int? declared = document.PageCount;

        // Corrected, not merely reported. The images are right here to be counted,
        // so a PageCount that disagrees with them is wrong rather than evidence of
        // anything, and every reader that trusts it is misled until it is fixed.
        if (document.SetPageCount(images) && entry is not null)
        {
            findings?.Add(new Finding
            {
                RuleId = "CBZ-E020",
                Severity = Severity.Warning,
                Message = declared is null
                    ? $"No PageCount was declared; set to {images} on save."
                    : $"PageCount said {declared} but the archive holds {images} "
                        + $"image{(images == 1 ? "" : "s")}; corrected on save.",
                Location = entry.Name,
                HasAutofix = true,
            });
        }

        // A nested ComicInfo.xml is one most readers never find, and the rebuild is
        // already composing a fresh entry list, so moving it costs nothing. Only
        // the metadata document moves: the images keep their order, which for a
        // comic is the reading order.
        bool relocate = entry is not null && entry.Name.IndexOf('/') >= 0;

        if (relocate)
        {
            findings?.Add(new Finding
            {
                RuleId = "CBZ-E011",
                Severity = Severity.Warning,
                Message = $"'{entry!.Name}' was not at the archive root, where readers look "
                    + $"for it; moved to '{ComicInfoDocument.DefaultEntryName}' on save.",
                Location = entry.Name,
                HasAutofix = true,
            });
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
                ? PendingEntry.FromBytes(
                    existing.Name, bytes, existing.CompressionMethod, existing.LastModified)
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
        IContainer container, ContainerEntry entry, ICollection<Finding>? findings)
    {
        try
        {
            return ComicInfoDocument.Parse(ReadAllBytes(container, entry), entry.Name);
        }
        catch (BookFormatException ex)
        {
            findings?.Add(new Finding
            {
                RuleId = "CBZ-F001",
                Severity = Severity.Fatal,
                Message = ex.Message,
                Location = entry.Name,
            });

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
            Data = ReadAllBytes(container, first),
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

    private static byte[] ReadAllBytes(IContainer container, ContainerEntry entry)
    {
        using Stream stream = container.OpenRead(entry);
        using var buffer = new MemoryStream(entry.Length > 0 ? (int)entry.Length : 4096);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
