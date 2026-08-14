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
    /// <remarks>
    /// Case is preserved as found rather than upper-cased. EPUB sources are
    /// inconsistent here — <c>ISBN</c>, <c>isbn</c> and <c>Isbn</c> all occur —
    /// and normalising would produce a diff on save for a file the user only
    /// opened to fix a typo in the title.
    /// </remarks>
    public string? Scheme { get; init; }

    /// <summary>
    /// The <c>id</c> attribute this identifier carried in the source document.
    /// </summary>
    /// <remarks>
    /// Load-bearing for EPUB: <c>package/@unique-identifier</c> names one
    /// <c>dc:identifier</c> by id, and rule EPUB-E011 checks that the reference
    /// resolves. Both the check and a correct write need this preserved.
    /// </remarks>
    public string? SourceId { get; init; }

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
