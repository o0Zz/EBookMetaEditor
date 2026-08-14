namespace EBookMeta.Model;

/// <summary>
/// The cover image, as raw bytes plus its media type.
/// </summary>
public sealed record CoverImage
{
    /// <summary>The image bytes, exactly as stored in the source.</summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// The IANA media type, such as <c>image/jpeg</c> or <c>image/png</c>.
    /// </summary>
    public required string MediaType { get; init; }

    /// <summary>
    /// The container entry the image was read from, when it came from one —
    /// <c>OEBPS/images/cover.jpg</c>. <see langword="null"/> for a cover
    /// supplied by the user or embedded without a named entry, as in MOBI.
    /// </summary>
    public string? SourceEntryName { get; init; }

    /// <summary>
    /// The manifest <c>id</c> of the cover item in the source document, when
    /// the format has a manifest.
    /// </summary>
    public string? SourceManifestId { get; init; }

    /// <summary>Returns a short description, for diagnostics.</summary>
    public override string ToString() =>
        $"{MediaType}, {Data.Length} bytes{(SourceEntryName is null ? "" : $" ({SourceEntryName})")}";
}
