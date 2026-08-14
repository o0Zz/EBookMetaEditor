namespace EBookMeta.Model;

/// <summary>
/// The series a book belongs to and its position within it.
/// </summary>
public sealed record SeriesInfo
{
    /// <summary>The series name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Position within the series. <see langword="null"/> when the source named
    /// a series without one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="decimal"/> rather than <see cref="double"/>, because half
    /// numbers are real and common — a novella published as book 2.5 of a
    /// series is normal, and <c>calibre:series_index</c> stores it as text.
    /// Binary floating point would turn 2.5 into something that serialises as
    /// <c>2.4999999999999996</c> on an unlucky round trip, producing a spurious
    /// diff on a file the user never edited.
    /// </para>
    /// <para>
    /// Formatted for output with the invariant culture. A French user's locale
    /// would otherwise write <c>2,5</c>, which no reader parses.
    /// </para>
    /// </remarks>
    public decimal? Index { get; init; }

    /// <summary>
    /// The index exactly as the source wrote it, when it was present but not
    /// parseable as a number — <c>"one"</c>, <c>"II"</c>, <c>"3 of 7"</c>.
    /// </summary>
    /// <remarks>
    /// Preserved rather than discarded so that opening and saving a file with
    /// an odd index does not silently delete it. When this is set and
    /// <see cref="Index"/> is <see langword="null"/>, write this back verbatim.
    /// </remarks>
    public string? RawIndex { get; init; }

    /// <summary>Returns "Name #Index", for diagnostics.</summary>
    public override string ToString() =>
        Index is { } i
            ? $"{Name} #{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : RawIndex is not null ? $"{Name} #{RawIndex}" : Name;
}
