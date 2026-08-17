using System.Globalization;

namespace EBookMeta.Model;

/// <summary>How much of a date the source actually stated.</summary>
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

    /// <summary>
    /// Parses a book date, keeping the source text and recording how much of it
    /// was actually stated.
    /// </summary>
    /// <param name="raw">The date text as the source wrote it.</param>
    /// <returns>
    /// The parsed date. <see cref="Raw"/> is always the input, and
    /// <see cref="Precision"/> says how much of it was real, so a bare year is
    /// never silently promoted to a full calendar date.
    /// </returns>
    public static BookDate Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new BookDate { Raw = raw, Precision = DatePrecision.Unknown };
        }

        string trimmed = raw.Trim();

        // Try most-specific first so precision is not overstated. A bare "2011"
        // must not come back as 1 January.
        (string Format, DatePrecision Precision)[] formats =
        [
            ("yyyy-MM-ddTHH:mm:ssK", DatePrecision.Time),
            ("yyyy-MM-ddTHH:mm:ss", DatePrecision.Time),
            ("yyyy-MM-dd", DatePrecision.Day),
            ("yyyy-MM", DatePrecision.Month),
            ("yyyy", DatePrecision.Year),
        ];

        foreach ((string format, DatePrecision precision) in formats)
        {
            if (DateTimeOffset.TryParseExact(
                    trimmed, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset value))
            {
                return new BookDate { Raw = trimmed, Value = value, Precision = precision };
            }
        }

        return DateTimeOffset.TryParse(
                trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset loose)
            ? new BookDate { Raw = trimmed, Value = loose, Precision = DatePrecision.Day }
            : new BookDate { Raw = trimmed, Precision = DatePrecision.Unknown };
    }
}
