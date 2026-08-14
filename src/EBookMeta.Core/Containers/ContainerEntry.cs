namespace EBookMeta.Containers;

/// <summary>
/// One entry inside a container, described well enough that
/// <see cref="IContainer.Rebuild"/> can reproduce it.
/// </summary>
/// <remarks>
/// The read half of a pair: this describes an entry <em>as found</em>, while
/// <see cref="PendingEntry"/> is an instruction to write one. They carry nearly
/// the same fields and flow in opposite directions —
/// <see cref="PendingEntry.CopyOf"/> is what turns one into the other.
/// </remarks>
/// <seealso cref="PendingEntry" />
public sealed record ContainerEntry
{
    /// <summary>
    /// The entry name, with forward slashes, exactly as stored — never
    /// normalised, rooted or resolved.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Position in the container's own ordering, zero-based. Stable for the
    /// lifetime of the container and used to address the entry.
    /// </summary>
    /// <remarks>
    /// Entries are addressed by index rather than name because names are not
    /// reliably unique. A ZIP may legally contain two entries with the same
    /// name, and malformed archives in the wild do.
    /// </remarks>
    public required int Index { get; init; }

    /// <summary>The uncompressed size in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>
    /// The compression method as read, using ZIP method codes: 0 for stored,
    /// 8 for deflate.
    /// </summary>
    public ushort CompressionMethod { get; init; }

    /// <summary>The last-modified timestamp recorded for the entry.</summary>
    public DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// Whether the entry is a directory marker rather than a file — a ZIP entry
    /// whose name ends in <c>/</c> and whose length is zero.
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="CompressionMethod"/> is one this
    /// build can reproduce on write: stored or deflate.
    /// </summary>
    /// <remarks>
    /// ZIP permits bzip2, LZMA, Zstandard and others.
    /// <c>System.IO.Compression</c> can read some and write none of them, so an
    /// archive containing one cannot be rebuilt byte-faithfully. Callers check
    /// this and warn rather than silently re-encoding.
    /// </remarks>
    public bool IsReproducibleCompression =>
        CompressionMethod is ZipCompressionMethods.Stored or ZipCompressionMethods.Deflate;

    /// <summary>Returns the name and size, for diagnostics.</summary>
    public override string ToString() => $"{Name} ({Length} bytes, method {CompressionMethod})";
}

/// <summary>
/// ZIP compression method codes, as they appear in the central directory.
/// </summary>
public static class ZipCompressionMethods
{
    /// <summary>No compression. Required for an EPUB's <c>mimetype</c> entry.</summary>
    public const ushort Stored = 0;

    /// <summary>Deflate — the method essentially every other entry uses.</summary>
    public const ushort Deflate = 8;

    /// <summary>Deflate64. Readable by some tools, not writable here.</summary>
    public const ushort Deflate64 = 9;

    /// <summary>bzip2.</summary>
    public const ushort BZip2 = 12;

    /// <summary>LZMA.</summary>
    public const ushort Lzma = 14;

    /// <summary>Zstandard.</summary>
    public const ushort Zstd = 93;

    /// <summary>XZ.</summary>
    public const ushort Xz = 95;

    /// <summary>Returns a human-readable name for a method code.</summary>
    /// <param name="method">A ZIP compression method code.</param>
    /// <returns>The method's usual name, or <c>method N</c> if unrecognised.</returns>
    public static string ToName(ushort method) => method switch
    {
        Stored => "stored",
        Deflate => "deflate",
        Deflate64 => "deflate64",
        BZip2 => "bzip2",
        Lzma => "lzma",
        Zstd => "zstd",
        Xz => "xz",
        _ => $"method {method}",
    };
}
