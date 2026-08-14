using System.Globalization;
using EBookMeta.Documents;
using EBookMeta.Formats;
using EBookMeta.Model;

namespace EBookMeta;

/// <summary>
/// Reads and writes <see cref="BookMetadata"/> one field at a time, as the text an
/// editor shows.
/// </summary>
/// <remarks>
/// <para>
/// The single definition of what a field looks like in a box and what typing in
/// that box does to the model: authors separated by semicolons, subjects by
/// commas, a date kept as the characters the file used, an index parsed as an
/// invariant decimal.
/// </para>
/// <para>
/// In Core rather than in the window because there is more than one editor now. A
/// grid over three hundred files and a form over one must agree exactly, and the
/// agreement has to include the parts that are easy to get subtly wrong: rebuilding
/// the primary creators without deleting the contributors the editor never showed,
/// carrying a sort name forward only when the name it belongs to did not change,
/// and reparsing a date only when its text actually differs. Those are what keep
/// "open a file and save it without editing" byte-identical, and a second
/// implementation of them would keep it for one editor and quietly lose it for the
/// other.
/// </para>
/// <para>
/// Every <see cref="Apply"/> returns whether it changed anything, so a caller can
/// tell an edited file from an untouched one without comparing whole documents.
/// </para>
/// </remarks>
public static class MetadataFields
{
    /// <summary>
    /// Every field this class projects as text, in the order an editor shows them.
    /// </summary>
    /// <remarks>
    /// The order is not cosmetic: <see cref="MetadataField.Series"/> precedes
    /// <see cref="MetadataField.SeriesIndex"/> because the name carries the index,
    /// so a caller applying the whole set in sequence gets the right answer without
    /// having to know that.
    /// </remarks>
    public static IReadOnlyList<MetadataField> All { get; } =
    [
        MetadataField.Title,
        MetadataField.SortTitle,
        MetadataField.Creators,
        MetadataField.Series,
        MetadataField.SeriesIndex,
        MetadataField.Publisher,
        MetadataField.PublicationDate,
        MetadataField.Language,
        MetadataField.Subjects,
        MetadataField.Description,
    ];

    /// <summary>How authors are separated in a single-line editor.</summary>
    public const string CreatorSeparator = "; ";

    /// <summary>How subjects are separated in a single-line editor.</summary>
    public const string SubjectSeparator = ", ";

    /// <summary>Returns a field as the text an editor should show.</summary>
    /// <param name="metadata">The metadata to read.</param>
    /// <param name="field">The field to read. Exactly one flag.</param>
    /// <returns>The field's text, empty when it is absent.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="field"/> is not a field this class projects as text.
    /// </exception>
    public static string Read(BookMetadata metadata, MetadataField field)
    {
        Throw.IfNull(metadata);

        return field switch
        {
            MetadataField.Title => metadata.Title ?? string.Empty,
            MetadataField.SortTitle => metadata.SortTitle ?? string.Empty,
            MetadataField.Creators => string.Join(
                CreatorSeparator, metadata.PrimaryCreators.Select(c => c.Name)),
            MetadataField.Series => metadata.Series?.Name ?? string.Empty,
            MetadataField.SeriesIndex => ReadSeriesIndex(metadata),
            MetadataField.Description => metadata.Description ?? string.Empty,
            MetadataField.Publisher => metadata.Publisher ?? string.Empty,
            MetadataField.PublicationDate => metadata.PublicationDate?.Raw ?? string.Empty,
            MetadataField.Language => metadata.Language ?? string.Empty,
            MetadataField.Subjects => string.Join(SubjectSeparator, metadata.Subjects),
            _ => throw new ArgumentOutOfRangeException(
                nameof(field), field, "There is no text projection for this field."),
        };
    }

    /// <summary>Applies text to a field, leaving the model alone if nothing changed.</summary>
    /// <param name="metadata">The metadata to update.</param>
    /// <param name="field">The field to write. Exactly one flag.</param>
    /// <param name="value">The text the user typed. Trimmed; blank means absent.</param>
    /// <returns><see langword="true"/> if the model changed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="field"/> is not a field this class projects as text.
    /// </exception>
    /// <remarks>
    /// <see cref="MetadataField.Series"/> and <see cref="MetadataField.SeriesIndex"/>
    /// have an order between them: the name carries the index, so apply the name
    /// first. An index applied while there is no series name is dropped, because
    /// <see cref="SeriesInfo"/> has nowhere to put it.
    /// </remarks>
    public static bool Apply(BookMetadata metadata, MetadataField field, string value)
    {
        Throw.IfNull(metadata);
        Throw.IfNull(value);

        string? text = Blank(value) ? null : value.Trim();

        return field switch
        {
            MetadataField.Title => Set(text, metadata.Title, v => metadata.Title = v),
            MetadataField.SortTitle => Set(text, metadata.SortTitle, v => metadata.SortTitle = v),
            MetadataField.Creators => ApplyCreators(metadata, value),
            MetadataField.Series => ApplySeries(metadata, text),
            MetadataField.SeriesIndex => ApplySeriesIndex(metadata, text),
            MetadataField.Description => Set(text, metadata.Description, v => metadata.Description = v),
            MetadataField.Publisher => Set(text, metadata.Publisher, v => metadata.Publisher = v),
            MetadataField.PublicationDate => ApplyDate(metadata, text),
            MetadataField.Language => Set(text, metadata.Language, v => metadata.Language = v),
            MetadataField.Subjects => ApplySubjects(metadata, value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(field), field, "There is no text projection for this field."),
        };
    }

    private static string ReadSeriesIndex(BookMetadata metadata)
    {
        if (metadata.Series is not { } series)
        {
            return string.Empty;
        }

        // Invariant culture: a French locale would render 2.5 as "2,5", which is
        // then what a save would write, and no reader parses that.
        return series.Index?.ToString("0.############", CultureInfo.InvariantCulture)
            ?? series.RawIndex
            ?? string.Empty;
    }

    private static bool Set(string? value, string? current, Action<string?> assign)
    {
        if (string.Equals(value, current, StringComparison.Ordinal))
        {
            return false;
        }

        assign(value);
        return true;
    }

    /// <summary>
    /// Rebuilds the primary creators from a separated list of names.
    /// </summary>
    /// <remarks>
    /// Contributors are left untouched: an editor that shows only authors must not
    /// delete the illustrator it never showed. Sort name, role and source id are
    /// carried forward only where the name at that position is unchanged — moving
    /// "Gaiman, Neil" onto a different author would be worse than leaving it empty.
    /// </remarks>
    private static bool ApplyCreators(BookMetadata metadata, string value)
    {
        string[] names = Split(value, ';');
        List<Creator> primaries = metadata.PrimaryCreators.ToList();

        if (names.Length == primaries.Count &&
            names.SequenceEqual(primaries.Select(c => c.Name), StringComparer.Ordinal))
        {
            return false;
        }

        List<Creator> contributors = metadata.Creators
            .Where(c => c.Kind == CreatorKind.Contributor)
            .ToList();

        metadata.Creators.Clear();

        for (int i = 0; i < names.Length; i++)
        {
            Creator? previous = i < primaries.Count && primaries[i].Name == names[i]
                ? primaries[i]
                : null;

            metadata.Creators.Add(new Creator
            {
                Name = names[i],
                SortName = previous?.SortName,
                NativeRole = previous?.NativeRole ?? "aut",
                Role = previous?.Role ?? "aut",
                SourceId = previous?.SourceId,
                Kind = CreatorKind.Creator,
            });
        }

        foreach (Creator contributor in contributors)
        {
            metadata.Creators.Add(contributor);
        }

        return true;
    }

    private static bool ApplySeries(BookMetadata metadata, string? name)
    {
        if (name is null)
        {
            if (metadata.Series is null)
            {
                return false;
            }

            // Clearing the name clears the index with it. An index belongs to a
            // series, and one on its own is not something the model can hold.
            metadata.Series = null;
            return true;
        }

        if (string.Equals(metadata.Series?.Name, name, StringComparison.Ordinal))
        {
            return false;
        }

        metadata.Series = metadata.Series is { } existing
            ? existing with { Name = name }
            : new SeriesInfo { Name = name };

        return true;
    }

    private static bool ApplySeriesIndex(BookMetadata metadata, string? raw)
    {
        if (metadata.Series is not { } series)
        {
            return false;
        }

        if (raw is null)
        {
            if (series.Index is null && series.RawIndex is null)
            {
                return false;
            }

            metadata.Series = series with { Index = null, RawIndex = null };
            return true;
        }

        if (string.Equals(ReadSeriesIndex(metadata), raw, StringComparison.Ordinal))
        {
            return false;
        }

        // An index that will not parse is kept verbatim rather than discarded:
        // "3 of 7" and "Annual" are real, and the format that supplied one can
        // usually store it back.
        metadata.Series = decimal.TryParse(
                raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal index)
            ? series with { Index = index, RawIndex = null }
            : series with { Index = null, RawIndex = raw };

        return true;
    }

    /// <summary>
    /// Reparses a date only when its text changed.
    /// </summary>
    /// <remarks>
    /// The raw text is authoritative. A file that said "2013" must not come back
    /// as "2013-01-01", which would assert a day it never claimed, so an untouched
    /// date keeps the exact characters it arrived as.
    /// </remarks>
    private static bool ApplyDate(BookMetadata metadata, string? raw)
    {
        if (string.Equals(metadata.PublicationDate?.Raw, raw, StringComparison.Ordinal))
        {
            return false;
        }

        metadata.PublicationDate = raw is null ? null : OpfDocument.ParseDate(raw);
        return true;
    }

    private static bool ApplySubjects(BookMetadata metadata, string value)
    {
        string[] subjects = Split(value, ',');

        if (subjects.SequenceEqual(metadata.Subjects, StringComparer.Ordinal))
        {
            return false;
        }

        metadata.Subjects.Clear();

        foreach (string subject in subjects)
        {
            metadata.Subjects.Add(subject);
        }

        return true;
    }

    private static string[] Split(string value, char separator) =>
        [.. value
            .Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)];

    private static bool Blank(string value) => string.IsNullOrWhiteSpace(value);
}
