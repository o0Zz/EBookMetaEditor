namespace EBookMeta.Formats;

/// <summary>
/// How much of a file a read should bother with.
/// </summary>
public sealed record ReadOptions
{
    /// <summary>Read everything — the default a single-file editor wants.</summary>
    public static ReadOptions Default { get; } = new();

    /// <summary>Read the metadata only, leaving the cover image alone.</summary>
    public static ReadOptions WithoutCover { get; } = new() { IncludeCover = false };

    /// <summary>
    /// Whether to load the cover image bytes.
    /// </summary>
    public bool IncludeCover { get; init; } = true;
}
