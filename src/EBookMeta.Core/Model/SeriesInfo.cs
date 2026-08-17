using System.Globalization;

namespace EBookMeta.Model;

/// <summary>The series a book belongs to and its position within it.</summary>
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

    /// <summary>
    /// The index as text: the number, or the raw form when it was not a number, or
    /// <see langword="null"/> when there is none. Invariant culture, because a French
    /// locale would write "2,5" and no reader parses that.
    /// </summary>
    public string? IndexText =>
        Index?.ToString("0.############", CultureInfo.InvariantCulture) ?? RawIndex;

    /// <summary>
    /// Builds a series from a name and the index as the source wrote it, keeping an
    /// index that will not parse verbatim rather than discarding it — "3 of 7" and
    /// "Annual" are real, and the format that supplied one can usually store it back.
    /// </summary>
    /// <param name="name">The series name.</param>
    /// <param name="indexText">The index as text, which may be absent or unparseable.</param>
    /// <returns>The series.</returns>
    public static SeriesInfo Create(string name, string? indexText) =>
        string.IsNullOrWhiteSpace(indexText)
            ? new SeriesInfo { Name = name }
            : decimal.TryParse(
                indexText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal index)
                ? new SeriesInfo { Name = name, Index = index }
                : new SeriesInfo { Name = name, RawIndex = indexText };

    /// <summary>Returns "Name #Index", for diagnostics.</summary>
    public override string ToString() =>
        IndexText is { } text ? $"{Name} #{text}" : Name;
}
