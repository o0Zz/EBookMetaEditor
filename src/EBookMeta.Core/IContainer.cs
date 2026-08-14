namespace EBookMeta;

/// <summary>
/// A file that holds named entries — a ZIP, a TAR, a 7z, a PalmDB, or a single
/// raw file presented as one entry. The physical axis of the design, and one of
/// Core's two seams.
/// </summary>
/// <remarks>
/// It knows nothing about books: entries in the container's own order, byte
/// access to one of them, and an atomic rebuild. Everything that understands
/// what those entries mean lives behind <see cref="IBookFormat"/>.
/// <para>
/// Implementations are opened through <see cref="BookContainers.Open"/> rather
/// than being named by callers, so the choice of container lives in one place.
/// </para>
/// </remarks>
/// <seealso cref="IBookFormat" />
public interface IContainer : IDisposable
{
    /// <summary>
    /// Whether this container can be rebuilt.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> for RAR, and callers must handle that rather
    /// than assume. RAR compression is proprietary: SharpCompress reads but
    /// cannot write, and the UnRAR binary's licence forbids using it to build a
    /// compatible compressor. This is not a bug to work around — the correct
    /// response to a CBR is to offer conversion to CBZ as an explicit user
    /// choice, surfaced by rule GEN-W004.
    /// </remarks>
    bool IsWritable { get; }

    /// <summary>
    /// The entries, in the container's own order.
    /// </summary>
    IReadOnlyList<ContainerEntry> Entries { get; }

    /// <summary>
    /// Data the container carries outside its entries, which a rebuild would not
    /// reproduce — a ZIP's archive comment. <see langword="null"/> when there is
    /// none, which is the ordinary case.
    /// </summary>
    string? ArchiveComment { get; }

    /// <summary>Opens a readable stream over an entry's decompressed content.</summary>
    /// <param name="entry">An entry from <see cref="Entries"/>.</param>
    /// <returns>A readable stream; the caller disposes it.</returns>
    /// <exception cref="BookFormatException">The entry's content is unreadable.</exception>
    Stream OpenRead(ContainerEntry entry);

    /// <summary>
    /// Writes a new container containing the supplied entries, in the order
    /// given, to <paramref name="targetPath"/>.
    /// </summary>
    /// <param name="entries">The entries to write, in the order to write them.</param>
    /// <param name="targetPath">
    /// The path to write to. Supplied by <c>AtomicFileWriter</c> and normally a
    /// temporary sibling of the real target, never the user's file.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// <see cref="IsWritable"/> is <see langword="false"/>.
    /// </exception>
    /// <exception cref="BookIoException">The target could not be written.</exception>
    void Rebuild(IEnumerable<PendingEntry> entries, string targetPath);
}

/// <summary>
/// Convenience over <see cref="IContainer"/> that every format needs.
/// </summary>
/// <remarks>
/// Reading one entry whole is what a metadata document always requires, and each
/// format having its own copy of the three-line stream drain was three chances
/// for them to disagree about buffer sizing. It belongs on the physical seam
/// rather than in the formats, because it is a fact about entries, not books.
/// </remarks>
public static class ContainerExtensions
{
    /// <summary>Reads an entry's whole decompressed content.</summary>
    /// <param name="container">The container holding the entry.</param>
    /// <param name="entry">An entry from <see cref="IContainer.Entries"/>.</param>
    /// <returns>The entry's bytes.</returns>
    /// <exception cref="BookFormatException">The entry's content is unreadable.</exception>
    public static byte[] ReadAllBytes(this IContainer container, ContainerEntry entry)
    {
        Throw.IfNull(container);
        Throw.IfNull(entry);

        using Stream stream = container.OpenRead(entry);

        // Sized from the entry's declared length so the common case never grows
        // the buffer. A bogus length only costs a resize, so it is not validated.
        using var buffer = new MemoryStream(
            entry.Length > 0 && entry.Length < int.MaxValue ? (int)entry.Length : 4096);

        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

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

/// <summary>
/// An entry to be written by <see cref="IContainer.Rebuild"/>.
/// </summary>
/// <remarks>
/// The write half of a pair: <see cref="ContainerEntry"/> describes an entry as
/// found, this one instructs a rebuild to produce one.
/// <para>
/// Content is supplied as a stream factory rather than a byte array so that a
/// rebuild copies entries through without holding the whole archive in memory.
/// A 300-page comic is a few hundred megabytes; the startup budget and the
/// memory footprint both depend on streaming it.
/// </para>
/// </remarks>
/// <seealso cref="ContainerEntry" />
public sealed class PendingEntry
{
    private readonly Func<Stream> _openContent;

    /// <summary>
    /// Creates an entry whose content is produced on demand.
    /// </summary>
    /// <param name="name">The entry name, with forward slashes.</param>
    /// <param name="openContent">
    /// Opens a readable stream over the content. Called once, during the
    /// rebuild; the rebuild disposes the stream it returns.
    /// </param>
    /// <param name="compressionMethod">
    /// The ZIP method code to store the entry with — 0 stored, 8 deflate.
    /// </param>
    /// <param name="lastModified">The timestamp to record for the entry.</param>
    /// <param name="source">
    /// The entry this one reproduces, when it is a copy of one already in a
    /// container. <see langword="null"/> for content that has no original.
    /// </param>
    public PendingEntry(
        string name,
        Func<Stream> openContent,
        ushort compressionMethod = ZipCompressionMethods.Deflate,
        DateTimeOffset lastModified = default,
        ContainerEntry? source = null)
    {
        Throw.IfNullOrEmpty(name);
        Throw.IfNull(openContent);

        Name = name;
        _openContent = openContent;
        CompressionMethod = compressionMethod;
        LastModified = lastModified;
        Source = source;
    }

    /// <summary>The entry name, with forward slashes.</summary>
    public string Name { get; }

    /// <summary>
    /// The ZIP method code to store this entry with. Defaults to deflate;
    /// an EPUB's <c>mimetype</c> must be written with
    /// <see cref="ZipCompressionMethods.Stored"/>.
    /// </summary>
    public ushort CompressionMethod { get; }

    /// <summary>
    /// The timestamp to record. <see langword="default"/> lets the container
    /// choose, which for ZIP means the current time.
    /// </summary>
    public DateTimeOffset LastModified { get; }

    /// <summary>
    /// The entry this one reproduces, or <see langword="null"/> when the content
    /// is new — a rewritten <c>ComicInfo.xml</c>, or one being added.
    /// </summary>
    /// <remarks>
    /// A rebuild needs a way back to the original to reproduce what
    /// <see cref="ContainerEntry"/> does not model. <c>TarContainer</c> uses it to
    /// re-emit an entry's 512-byte header byte for byte, preserving the mode, uid,
    /// gid, uname and gname that tar records and this build has no other reason to
    /// understand. Only the container that produced the entry may interpret this;
    /// to anyone else it is opaque.
    /// </remarks>
    public ContainerEntry? Source { get; }

    /// <summary>Opens the content stream. Called once per rebuild.</summary>
    /// <returns>A readable stream the caller is responsible for disposing.</returns>
    public Stream OpenContent() => _openContent();

    /// <summary>
    /// Creates an entry that copies an existing one through unchanged,
    /// preserving its name, compression method and timestamp.
    /// </summary>
    /// <param name="source">The container the entry is read from.</param>
    /// <param name="entry">The entry to copy.</param>
    /// <returns>A pending entry that reproduces <paramref name="entry"/>.</returns>
    /// <remarks>
    /// This is the path every non-metadata entry takes. Content files,
    /// stylesheets and images are copied byte for byte and never round-tripped
    /// through a parser — an XHTML file that goes through an XML writer comes
    /// out reformatted, and the user did not ask for that.
    /// </remarks>
    public static PendingEntry CopyOf(IContainer source, ContainerEntry entry)
    {
        Throw.IfNull(source);
        Throw.IfNull(entry);

        return new PendingEntry(
            entry.Name,
            () => source.OpenRead(entry),
            entry.CompressionMethod,
            entry.LastModified,
            entry);
    }

    /// <summary>
    /// Creates an entry that takes an existing one's place with new content,
    /// keeping its name, compression method and timestamp.
    /// </summary>
    /// <param name="source">The entry being replaced.</param>
    /// <param name="content">The new content bytes.</param>
    /// <returns>A pending entry that stands in for <paramref name="source"/>.</returns>
    /// <remarks>
    /// The path a rewritten OPF or <c>ComicInfo.xml</c> takes. Distinct from
    /// <see cref="FromBytes"/> because the entry is not new: a container that
    /// retains more about an entry than <see cref="ContainerEntry"/> models can
    /// reach it through <see cref="Source"/> and keep it. <c>TarContainer</c>
    /// preserves the original header this way, changing only the length field and
    /// the checksum over it.
    /// </remarks>
    public static PendingEntry Replacing(ContainerEntry source, byte[] content)
    {
        Throw.IfNull(source);
        Throw.IfNull(content);

        return new PendingEntry(
            source.Name,
            () => new MemoryStream(content, writable: false),
            source.CompressionMethod,
            source.LastModified,
            source);
    }

    /// <summary>
    /// Creates an entry from bytes already in memory, for content that has no
    /// original — a metadata document being added to a file that had none.
    /// </summary>
    /// <param name="name">The entry name, with forward slashes.</param>
    /// <param name="content">The content bytes.</param>
    /// <param name="compressionMethod">The ZIP method code to store it with.</param>
    /// <param name="lastModified">The timestamp to record.</param>
    /// <returns>A pending entry over the supplied bytes.</returns>
    public static PendingEntry FromBytes(
        string name,
        byte[] content,
        ushort compressionMethod = ZipCompressionMethods.Deflate,
        DateTimeOffset lastModified = default)
    {
        Throw.IfNull(content);

        return new PendingEntry(
            name,
            () => new MemoryStream(content, writable: false),
            compressionMethod,
            lastModified);
    }

    /// <summary>Returns the name and method, for diagnostics.</summary>
    public override string ToString() =>
        $"{Name} ({ZipCompressionMethods.ToName(CompressionMethod)})";
}

/// <summary>
/// A read-only window onto part of another stream.
/// </summary>
/// <remarks>
/// What a container hands back for an entry stored uncompressed at a known
/// offset: TAR and PalmDB records, and the whole of a raw file. Reading one is a
/// bounded read rather than a decompression, so there is nothing to wrap it in
/// but bounds.
/// <para>
/// Seeks to its own position on every read, so a container that hands out several
/// of these over one shared stream does not have them lose each other's place.
/// <see cref="Dispose"/> closes the underlying stream only when this window was
/// given ownership of it — a caller's <c>using</c> must never close the
/// container's handle.
/// </para>
/// </remarks>
internal sealed class SectionStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _ownsStream;
    private long _position;

    /// <summary>Creates a window over part of a stream.</summary>
    /// <param name="inner">The stream to read from; must be seekable.</param>
    /// <param name="start">Where the window begins in <paramref name="inner"/>.</param>
    /// <param name="length">How many bytes the window covers.</param>
    /// <param name="ownsStream">
    /// <see langword="true"/> to dispose <paramref name="inner"/> with this window.
    /// </param>
    internal SectionStream(Stream inner, long start, long length, bool ownsStream)
    {
        _inner = inner;
        _start = start;
        _length = length;
        _ownsStream = ownsStream;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? 0 : Math.Min(value, _length);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        _inner.Position = _start + _position;

        int read = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
        _position += read;

        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };

        return _position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsStream)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
