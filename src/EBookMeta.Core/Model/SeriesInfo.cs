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
    public decimal? Index { get; init; }

    /// <summary>
    /// The index exactly as the source wrote it, when it was present but not
    /// parseable as a number — <c>"one"</c>, <c>"II"</c>, <c>"3 of 7"</c>.
    /// </summary>
    public string? RawIndex { get; init; }

    /// <summary>Returns "Name #Index", for diagnostics.</summary>
    public override string ToString() =>
        Index is { } i
            ? $"{Name} #{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : RawIndex is not null ? $"{Name} #{RawIndex}" : Name;
}
