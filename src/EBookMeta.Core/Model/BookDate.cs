namespace EBookMeta.Model;

/// <summary>
/// How much of a date the source actually stated.
/// </summary>
public enum DatePrecision
{
    /// <summary>The text could not be parsed as a date at all.</summary>
    Unknown = 0,

    /// <summary>Year only — <c>2011</c>.</summary>
    Year = 1,

    /// <summary>Year and month — <c>2011-05</c>.</summary>
    Month = 2,

    /// <summary>A full calendar date — <c>2011-05-03</c>.</summary>
    Day = 3,

    /// <summary>A date and time — <c>2011-05-03T14:22:00Z</c>.</summary>
    Time = 4,
}

/// <summary>
/// A date from a book's metadata, keeping the source text alongside the parsed
/// value.
/// </summary>
public sealed record BookDate
{
    /// <summary>The date text exactly as the source stored it.</summary>
    public required string Raw { get; init; }

    /// <summary>
    /// The parsed value, or <see langword="null"/> when <see cref="Raw"/> is not
    /// a recognisable date. A value here is not a promise of precision — check
    /// <see cref="Precision"/> before showing or writing components the source
    /// never stated.
    /// </summary>
    public DateTimeOffset? Value { get; init; }

    /// <summary>How much of the date the source actually stated.</summary>
    public DatePrecision Precision { get; init; } = DatePrecision.Unknown;

    /// <summary>Returns the source text, which is the authoritative form.</summary>
    public override string ToString() => Raw;
}
