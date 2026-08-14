using System.Globalization;
using EBookMeta.Containers;
using EBookMeta.Documents;
using EBookMeta.Model;

namespace EBookMeta.Formats;

/// <summary>
/// Reads, validates and writes comic archive metadata: ZIP plus
/// <c>ComicInfo.xml</c>.
/// </summary>
public sealed class CbzHandler : IFormatHandler
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
    /// Reports where the metadata document is, or that there is none.
    /// </summary>
    private static void CheckLayout(
        IContainer container, ContainerEntry? entry, ICollection<Finding> findings)
    {
        if (entry is null)
        {
            // Not a defect. Most comics in a collection have never been tagged,
            // and this is the message that explains what saving will do.
            findings.Add(new Finding
            {
                RuleId = "CBZ-W010",
                Severity = Severity.Warning,
                Message = $"There is no '{ComicInfoDocument.DefaultEntryName}'. "
                    + "One will be created when you save.",
            });
        }
        else if (entry.Name.IndexOf('/') >= 0)
        {
            findings.Add(new Finding
            {
                RuleId = "CBZ-E011",
                Severity = Severity.Error,
                Message = $"'{entry.Name}' is not at the archive root, so most readers "
                    + "will not find it.",
                Location = entry.Name,
            });
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
            findings.Add(new Finding
            {
                RuleId = "CBZ-W012",
                Severity = Severity.Warning,
                Message = "This archive carries more than one metadata convention "
                    + $"({string.Join(" and ", Conventions(entry, hasCoMet, hasComicBookLover))}). "
                    + $"Only '{ComicInfoDocument.DefaultEntryName}' is written; the others are "
                    + "left as they are, so applications reading them may disagree.",
                Detail = hasComicBookLover ? "the ZIP comment also blocks saving" : null,
            });
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
        ComicInfoDocument? document,
        ICollection<Finding> findings)
    {
        List<ContainerEntry> images = Images(container).ToList();

        if (document is not null)
        {
            if (document.PageCount is { } declared && declared != images.Count)
            {
                findings.Add(new Finding
                {
                    RuleId = "CBZ-E020",
                    Severity = Severity.Error,
                    Message = $"PageCount says {declared} but the archive holds "
                        + $"{images.Count} image{(images.Count == 1 ? "" : "s")}.",
                    Location = entry!.Name,
                });
            }

            IReadOnlyList<int?> pages = document.PageImageIndexes;

            if (pages.Count > 0 &&
                (pages.Count != images.Count || pages.Any(p => p is null || p < 0 || p >= images.Count)))
            {
                findings.Add(new Finding
                {
                    RuleId = "CBZ-W021",
                    Severity = Severity.Warning,
                    Message = $"The <Pages> block describes {pages.Count} page(s) and does not "
                        + $"match the {images.Count} image(s) in the archive.",
                    Location = entry!.Name,
                });
            }
        }

        if (!SortsIntoReadingOrder(images))
        {
            findings.Add(new Finding
            {
                RuleId = "CBZ-W022",
                Severity = Severity.Warning,
                Message = "The page filenames do not sort into reading order. Unpadded "
                    + "numbers are the usual cause — '10.jpg' sorts before '2.jpg' — and "
                    + "readers that sort by name will show the pages jumbled.",
            });
        }

        List<string> extras = container.Entries
            .Where(e => !e.IsDirectory && !IsImage(e) && !IsMetadata(e))
            .Select(e => e.Name)
            .ToList();

        if (extras.Count > 0)
        {
            findings.Add(new Finding
            {
                RuleId = "CBZ-W023",
                Severity = Severity.Warning,
                Message = $"The archive holds {extras.Count} entr"
                    + (extras.Count == 1 ? "y" : "ies")
                    + " that are neither images nor metadata.",
                Detail = string.Join(", ", extras.Take(5)),
            });
        }
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
    /// Checks the fields whose content can be wrong on its own terms.
    /// </summary>
    private static void CheckFields(
        ContainerEntry? entry, ComicInfoDocument? document, ICollection<Finding> findings)
    {
        if (document is null || entry is null)
        {
            return;
        }

        if (document.Value("Number") is { } number && document.Value("Series") is null)
        {
            findings.Add(new Finding
            {
                RuleId = "CBZ-W030",
                Severity = Severity.Warning,
                Message = $"Number is '{number}' but no Series is given, so a library will "
                    + "file this issue under no series at all.",
                Location = entry.Name,
                Detail = number,
            });
        }

        if (ImpossibleDate(document) is { } date)
        {
            findings.Add(new Finding
            {
                RuleId = "CBZ-W031",
                Severity = Severity.Warning,
                Message = $"Year, Month and Day do not form a real date ({date}).",
                Location = entry.Name,
                Detail = date,
            });
        }

        if (document.Value("LanguageISO") is { } language && !IsIso639Part1(language))
        {
            findings.Add(new Finding
            {
                RuleId = "CBZ-W032",
                Severity = Severity.Warning,
                Message = $"LanguageISO is '{language}', which is not an ISO 639-1 code. "
                    + "The schema wants two letters, such as 'en' or 'fr'.",
                Location = entry.Name,
                Detail = language,
            });
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

/// <summary>
/// Orders names the way a person reads them, so <c>2.jpg</c> comes before
/// <c>10.jpg</c>.
/// </summary>
internal sealed class NaturalNameComparer : IComparer<string>
{
    /// <summary>The shared instance; the comparer holds no state.</summary>
    internal static NaturalNameComparer Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.CompareOrdinal(x, y);
        }

        int i = 0;
        int j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int startX = i;
                int startY = j;

                while (i < x.Length && char.IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    j++;
                }

                // Compared as text with leading zeros stripped rather than parsed
                // as an integer: a scanner that names pages with a 20-digit
                // timestamp would overflow every numeric type there is.
                string numberX = x.Substring(startX, i - startX).TrimStart('0');
                string numberY = y.Substring(startY, j - startY).TrimStart('0');

                if (numberX.Length != numberY.Length)
                {
                    return numberX.Length - numberY.Length;
                }

                int digits = string.CompareOrdinal(numberX, numberY);
                if (digits != 0)
                {
                    return digits;
                }

                continue;
            }

            int character = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (character != 0)
            {
                return character;
            }

            i++;
            j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}
