namespace EBookMeta.Model;

/// <summary>
/// A piece of metadata read from the source that does not map onto any
/// <see cref="BookMetadata"/> field.
/// </summary>
/// <remarks>
/// <para>
/// This exists to serve the invariant "never lose a field you do not
/// understand" — unknown EXTH record types, unrecognised <c>ComicInfo</c>
/// elements, arbitrary <c>&lt;meta&gt;</c> in an OPF, unknown XMP properties.
/// It plays two distinct roles depending on the format, and the difference
/// matters:
/// </para>
/// <para>
/// <b>For XML formats, this list is informational only.</b> Preservation is
/// achieved by never touching the node, not by round-tripping it through the
/// model. <c>OpfDocument</c> retains the parsed tree and mutates only the
/// elements a field actually changed, so an unrecognised <c>&lt;meta&gt;</c>
/// survives because nothing went near it. Extracting and re-serialising it
/// would be strictly worse: it would risk reformatting, re-quoting attributes,
/// or rebinding a namespace prefix on an element the user never edited. These
/// entries exist so the UI can show the user what is in the file.
/// </para>
/// <para>
/// <b>For record-based binary formats, this list is the preservation
/// mechanism.</b> An EXTH table is rebuilt from scratch on write, so an unknown
/// record type survives only if its bytes were captured on read and re-emitted.
/// There is no tree to leave alone.
/// </para>
/// </remarks>
public sealed record UnmappedField
{
    /// <summary>
    /// The document the field came from — <c>OPF</c>, <c>ComicInfo.xml</c>,
    /// <c>EXTH</c>, <c>XMP</c>.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// What identifies the field within its source: an element name, a
    /// <c>meta/@name</c> or <c>@property</c> value, or an EXTH record type
    /// number rendered as text.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// The textual value, for fields that have one. For XML this is the
    /// element's value rather than its markup — the markup is preserved in the
    /// tree, not here.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// The verbatim payload, for record-based binary formats where these bytes
    /// are the only surviving copy and must be written back unchanged.
    /// </summary>
    public byte[]? Bytes { get; init; }

    /// <summary>
    /// One-based line in the source document, or 0 where the format has no
    /// meaningful line numbers.
    /// </summary>
    public int Line { get; init; }

    /// <summary>
    /// One-based column in the source document, or 0 where the format has no
    /// meaningful columns.
    /// </summary>
    public int Column { get; init; }

    /// <summary>Returns "Source/Key = Text", for diagnostics.</summary>
    public override string ToString() =>
        $"{Source}/{Key}{(Text is null ? "" : $" = {Text}")}";
}
