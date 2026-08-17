namespace EBookMeta.Model;

/// <summary>
/// A scheme-qualified identifier for the work — an ISBN, a UUID, a DOI, a
/// publisher's internal code.
/// </summary>
public sealed record Identifier
{
    /// <summary>
    /// The identifier value, verbatim. Not normalised: an ISBN written with
    /// hyphens keeps them, because rewriting it is a change the user did not
    /// ask for.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// The scheme, such as <c>ISBN</c>, <c>UUID</c>, <c>DOI</c> or <c>MOBI-ASIN</c>.
    /// <see langword="null"/> when the source declared none.
    /// </summary>
    public string? Scheme { get; init; }

    /// <summary>
    /// Whether this is the work's unique identifier — the one
    /// <c>package/@unique-identifier</c> points at, for a format that has the
    /// concept.
    /// </summary>
    public bool IsUnique { get; init; }

    /// <summary>Returns a scheme-qualified form, for diagnostics.</summary>
    public override string ToString() =>
        Scheme is null ? Value : $"{Scheme}:{Value}";
}
