using EBookMeta.Containers;
using EBookMeta.Documents;
using EBookMeta.Model;

namespace EBookMeta.Formats;

/// <summary>
/// The MOBI validation rules — the half of <see cref="MobiFormat"/> that reports
/// rather than reads.
/// </summary>
/// <remarks>
/// Every rule works from the header record the read already parsed, or from the
/// PalmDB record table the container already walked, so running all of them costs
/// a read nothing. Nothing here decompresses a text record: this build has no
/// reason to look at a book's contents and does not.
/// </remarks>
public sealed partial class MobiFormat
{
    /// <summary>
    /// Checks the fields a book needs before a library can file it.
    /// </summary>
    private static void CheckRequiredMetadata(
        MobiDocument header, BookMetadata metadata, ICollection<Finding> findings)
    {
        string location = header.Location;

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            findings.Add(new Finding
            {
                RuleId = "MOBI-E010",
                Severity = Severity.Error,
                Message = "The book has no title, in either the header's name field or "
                    + "EXTH record 503.",
                Location = location,
            });
        }

        if (!metadata.PrimaryCreators.Any())
        {
            findings.Add(new Finding
            {
                RuleId = "MOBI-W011",
                Severity = Severity.Warning,
                Message = "There is no author (EXTH record 100), so a library will file "
                    + "this book under no author at all.",
                Location = location,
            });
        }

        if (metadata.Language is null)
        {
            findings.Add(new Finding
            {
                RuleId = "MOBI-W012",
                Severity = Severity.Warning,
                Message = "There is no language (EXTH record 524). The MOBI header's own "
                    + "locale field is not read as a substitute, because mapping its "
                    + "numeric code back to a language tag would be a guess.",
                Location = location,
            });
        }
    }

    /// <summary>
    /// Reports what the database's header records say about each other.
    /// </summary>
    private static void CheckHeaders(
        IContainer container, List<MobiDocument> headers, ICollection<Finding> findings)
    {
        if (headers.Count < 2)
        {
            return;
        }

        // Both halves are read, so a disagreement can be stated rather than
        // guessed at. It is reported and left alone: neither half is provably the
        // right one, and copying the KF8 half over the MOBI 6 one would delete
        // whatever fields only the older half carries. A save propagates what the
        // user edits and nothing else.
        BookMetadata first = headers[0].ReadMetadata();
        BookMetadata second = headers[1].ReadMetadata();

        if (!string.Equals(first.Title, second.Title, StringComparison.Ordinal))
        {
            findings.Add(new Finding
            {
                RuleId = "MOBI-W020",
                Severity = Severity.Warning,
                Message = "The MOBI and KF8 halves of this file carry different titles "
                    + $"('{first.Title}' and '{second.Title}'). The KF8 one is shown, "
                    + "because that is the half readers use. Editing the title writes both; "
                    + "saving without editing changes neither.",
                Detail = $"{first.Title} / {second.Title}",
            });
        }
    }

    /// <summary>
    /// Reports a cover reference that points outside the database.
    /// </summary>
    private static void CheckCover(
        IContainer container, MobiDocument header, ICollection<Finding> findings)
    {
        if (header.CoverImageOffset is not { } offset)
        {
            findings.Add(new Finding
            {
                RuleId = "MOBI-W022",
                Severity = Severity.Warning,
                Message = "No cover is declared (EXTH record 201), so readers will show "
                    + "this book with a generated one.",
                Location = header.Location,
            });

            return;
        }

        if (header.FirstImageIndex < 0)
        {
            findings.Add(new Finding
            {
                RuleId = "MOBI-E023",
                Severity = Severity.Error,
                Message = $"A cover is declared at image {offset}, but the header does not "
                    + "say which record the images start at, so it cannot be found.",
                Location = header.Location,
            });

            return;
        }

        long index = (long)header.FirstImageIndex + offset;

        if (index <= 0 || index >= container.Entries.Count)
        {
            findings.Add(new Finding
            {
                RuleId = "MOBI-E023",
                Severity = Severity.Error,
                Message = $"The cover points at record {index}, which is outside this "
                    + $"database's {container.Entries.Count} records.",
                Location = header.Location,
                Detail = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }
    }
}
