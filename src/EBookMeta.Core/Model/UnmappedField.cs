namespace EBookMeta.Model;

/// <summary>
/// A piece of metadata read from the source that does not map onto any
/// <see cref="BookMetadata"/> field.
/// </summary>
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

    /// <summary>Returns "Source/Key = Text", for diagnostics.</summary>
    public override string ToString() =>
        $"{Source}/{Key}{(Text is null ? "" : $" = {Text}")}";
}
