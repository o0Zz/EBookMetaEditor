using System.Xml.Linq;
using EBookMeta.Documents;
using EBookMeta.Model;

namespace EBookMeta.Formats;

/// <summary>
/// The FictionBook validation rules — the half of <see cref="Fb2Format"/> that
/// reports rather than reads.
/// </summary>
/// <remarks>
/// Every rule here works from the parsed <c>&lt;description&gt;</c>, which the read
/// already has in hand, so running all of them costs a read essentially nothing.
/// The one exception is the cover, which needs the binary the cover page points at
/// and is therefore only checked when a cover was asked for.
/// </remarks>
public sealed partial class Fb2Format
{
    /// <summary>
    /// Checks the fields a FictionBook is required to carry.
    /// </summary>
    private static void CheckRequiredMetadata(
        Fb2Document document, BookMetadata metadata, ICollection<Finding> findings)
    {
        string location = document.EntryName;
        XElement? titleInfo = document.Description.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "title-info");

        if (titleInfo is null)
        {
            findings.Add(new Finding
            {
                RuleId = "FB2-E010",
                Severity = Severity.Error,
                Message = "There is no <title-info>, which is where a FictionBook keeps "
                    + "everything about the book itself.",
                Location = location,
            });

            return;
        }

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            findings.Add(new Finding
            {
                RuleId = "FB2-E011",
                Severity = Severity.Error,
                Message = "<book-title> is missing or empty, so readers have no title to "
                    + "show but the file name.",
                Location = location,
            });
        }

        if (metadata.Language is not { } language || string.IsNullOrWhiteSpace(language))
        {
            findings.Add(new Finding
            {
                RuleId = "FB2-E012",
                Severity = Severity.Error,
                Message = "<lang> is missing. The schema requires it, and readers use it "
                    + "for hyphenation and sorting.",
                Location = location,
            });
        }
        else if (!IsPlausibleLanguageTag(language))
        {
            findings.Add(new Finding
            {
                RuleId = "FB2-W013",
                Severity = Severity.Warning,
                Message = $"<lang> is '{language}', which is not a plausible language code. "
                    + "Two or three letters is what readers expect — 'en', 'ru', 'fr'.",
                Location = location,
                Detail = language,
            });
        }

        if (!metadata.PrimaryCreators.Any())
        {
            findings.Add(new Finding
            {
                RuleId = "FB2-W014",
                Severity = Severity.Warning,
                Message = "There is no <author>. The schema requires at least one, and a "
                    + "library will file this book under no author at all.",
                Location = location,
            });
        }

        CheckSequence(titleInfo, location, findings);
    }

    /// <summary>
    /// Reports a series position that is not a number.
    /// </summary>
    private static void CheckSequence(
        XElement titleInfo, string location, ICollection<Finding> findings)
    {
        foreach (XElement sequence in titleInfo.Elements()
            .Where(e => e.Name.LocalName == "sequence"))
        {
            string? number = (string?)sequence.Attribute("number");

            if (string.IsNullOrWhiteSpace(number) ||
                decimal.TryParse(
                    number!.Trim(),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                continue;
            }

            findings.Add(new Finding
            {
                RuleId = "FB2-W060",
                Severity = Severity.Warning,
                Message = $"The series position is '{number}', which is not a number, so "
                    + "readers that sort a series numerically will not place this book.",
                Location = location,
                Detail = number,
            });
        }
    }

    /// <summary>
    /// Reports bytes that do not match what the declaration claims.
    /// </summary>
    private static void CheckEncoding(Fb2Document document, ICollection<Finding> findings)
    {
        if (document.Encoding is { DeclarationMatchesBytes: false, Mismatch: { } mismatch })
        {
            findings.Add(new Finding
            {
                RuleId = "FB2-E050",
                Severity = Severity.Error,
                Message = $"The declared encoding does not match the bytes: {mismatch}",
                Location = document.EntryName,
            });
        }
    }

    /// <summary>
    /// Reports a cover page that points nowhere, or the absence of one.
    /// </summary>
    /// <remarks>
    /// FB2-E030 needs the binary itself, which only a full pass over the document
    /// can find, so it is reported only when the read was asked for a cover. A
    /// batch grid reads three hundred books without covers and must not pay for
    /// three hundred document walks to check a reference it is not displaying.
    /// </remarks>
    private static void CheckCover(
        Fb2Document document,
        BookMetadata metadata,
        ReadOptions options,
        ICollection<Finding> findings)
    {
        string? id = document.CoverImageId();

        if (id is null)
        {
            findings.Add(new Finding
            {
                RuleId = "FB2-W032",
                Severity = Severity.Warning,
                Message = "No cover is declared, so readers will show this book with a "
                    + "blank or generated one.",
                Location = document.EntryName,
            });

            return;
        }

        if (options.IncludeCover && metadata.Cover is null &&
            !findings.Any(f => f.RuleId == "FB2-W031"))
        {
            findings.Add(new Finding
            {
                RuleId = "FB2-E030",
                Severity = Severity.Error,
                Message = $"The cover page points at '{id}', but the document has no "
                    + $"<binary> with that id.",
                Location = document.EntryName,
                Detail = id,
            });
        }
    }

    /// <summary>
    /// Whether a language code is shaped like one, without asserting it exists.
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
