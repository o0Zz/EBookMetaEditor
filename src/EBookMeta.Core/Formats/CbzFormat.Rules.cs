using System.Globalization;
using EBookMeta.Containers;
using EBookMeta.Documents;

namespace EBookMeta.Formats;

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
}
