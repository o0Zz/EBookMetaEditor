namespace EBookMeta.Formats;

/// <summary>
/// A package document as bytes, before any attempt to parse it.
/// </summary>
/// <remarks>
/// The input to diagnosis and repair. A document that will not parse still has
/// an entry name and content, and both are needed: the name to write the repair
/// back to the right entry, the bytes to work out what is wrong with it.
/// </remarks>
public sealed record RawPackageDocument
{
    /// <summary>The container entry the document came from — <c>OEBPS/content.opf</c>.</summary>
    public required string EntryName { get; init; }

    /// <summary>The document's bytes, exactly as stored.</summary>
    public required byte[] Bytes { get; init; }
}
