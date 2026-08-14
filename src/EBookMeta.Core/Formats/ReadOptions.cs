namespace EBookMeta.Formats;

/// <summary>
/// How much of a file a read should bother with.
/// </summary>
/// <remarks>
/// Exists for one case that matters: a grid showing hundreds of files needs every
/// title and no cover at all. Reading a cover means decompressing a full-size
/// image per file, so a five-hundred-book folder would hold half a gigabyte of
/// JPEGs to populate a text grid.
/// </remarks>
public sealed record ReadOptions
{
    /// <summary>Read everything — the default a single-file editor wants.</summary>
    public static ReadOptions Default { get; } = new();

    /// <summary>Read the metadata only, leaving the cover image alone.</summary>
    public static ReadOptions WithoutCover { get; } = new() { IncludeCover = false };

    /// <summary>
    /// Whether to load the cover image bytes.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> leaves <see cref="Model.BookMetadata.Cover"/> null,
    /// which is indistinguishable from a file that has no cover. A caller that
    /// needs to tell those apart has to read again with the cover included.
    /// </remarks>
    public bool IncludeCover { get; init; } = true;
}
