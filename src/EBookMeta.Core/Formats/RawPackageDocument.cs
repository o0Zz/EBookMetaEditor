namespace EBookMeta.Formats;

/// <summary>
/// A package document as bytes, before any attempt to parse it.
/// </summary>
public sealed record RawPackageDocument
{
    /// <summary>The container entry the document came from — <c>OEBPS/content.opf</c>.</summary>
    public required string EntryName { get; init; }

    /// <summary>The document's bytes, exactly as stored.</summary>
    public required byte[] Bytes { get; init; }
}
