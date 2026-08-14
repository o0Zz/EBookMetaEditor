using System.Globalization;
using EBookMeta.Documents;
using EBookMeta.Formats;
using EBookMeta.Model;

namespace EBookMeta;

/// <summary>
/// Reads and writes <see cref="BookMetadata"/> one field at a time, as the text an
/// editor shows.
/// </summary>
public static class MetadataFields
{
    /// <summary>
    /// Every field this class projects as text, in the order an editor shows them.
    /// </summary>
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

    /// <summary>Whether a language tag is shaped like one, without asserting it exists.</summary>
    /// <param name="tag">The tag to test, such as <c>en</c> or <c>pt-BR</c>.</param>
    /// <returns><see langword="true"/> when it is plausibly BCP 47.</returns>
    /// <remarks>
    /// Shape only, deliberately: EPUB-W014 and FB2-W013 are warnings, and a
    /// registry check would turn a tag this build has not heard of into a false
    /// accusation. Shared because both rules ask the identical question of the
    /// identical kind of string.
    /// </remarks>
    public static bool IsPlausibleLanguageTag(string tag)
    {
        Throw.IfNull(tag);

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
