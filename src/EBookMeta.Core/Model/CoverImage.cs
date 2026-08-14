namespace EBookMeta.Model;

/// <summary>
/// The cover image, as raw bytes plus its media type.
/// </summary>
/// <remarks>
/// <para>
/// This is how cover art crosses the Core boundary, and it is deliberately not
/// a <c>Bitmap</c>. <c>EBookMeta.Core</c> has zero UI dependencies so that a
/// future port to Avalonia or Rust does not have to touch it; the WinForms
/// layer decodes these bytes itself, off the UI thread.
/// </para>
/// <para>
/// Not decoding also means EBookMetaEditor never re-encodes a cover it was not asked
/// to change. Page-image processing is an explicit non-goal, and a JPEG that
/// survives a save as the same bytes it arrived as cannot have lost quality.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Needed to write both EPUB cover conventions back consistently: the
    /// EPUB 2 form is <c>&lt;meta name="cover" content="<i>id</i>"&gt;</c> and
    /// the EPUB 3 form is <c>properties="cover-image"</c> on the manifest item
    /// with that same id. Rule EPUB-W032 exists because files in the wild
    /// routinely have one and not the other.
    /// </remarks>
    public string? SourceManifestId { get; init; }

    /// <summary>Returns a short description, for diagnostics.</summary>
    public override string ToString() =>
        $"{MediaType}, {Data.Length} bytes{(SourceEntryName is null ? "" : $" ({SourceEntryName})")}";
}
