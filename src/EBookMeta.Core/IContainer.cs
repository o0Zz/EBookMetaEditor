namespace EBookMeta;

/// <summary>
/// A file that holds named entries — a ZIP, a TAR, a 7z, a PalmDB, or a single
/// raw file presented as one entry. The physical axis of the design, and one of
/// Core's two seams.
/// </summary>
/// <seealso cref="IBookFormat" />
public interface IContainer : IDisposable
{
    /// <summary>Whether this container can be rebuilt.</summary>
    bool IsWritable { get; }

    /// <summary>The entries, in the container's own order.</summary>
    IReadOnlyList<ContainerEntry> Entries { get; }

    /// <summary>
    /// Data the container carries outside its entries, which a rebuild would not
    /// reproduce — a ZIP's archive comment. <see langword="null"/> when there is
    /// none, which is the ordinary case.
    /// </summary>
    string? ArchiveComment { get; }

    /// <summary>
    /// Opens a readable stream over an entry's decompressed content. Implementations
    /// share one handle on the archive, so <b>reads must not overlap</b> — dispose one
    /// entry stream before opening the next.
    /// </summary>
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
    /// <exception cref="BookFormatException">
    /// <see cref="IsWritable"/> is <see langword="false"/>, or the container cannot
    /// be reproduced without losing something. Thrown rather than
    /// <see cref="NotSupportedException"/> because the reason is worth showing the
    /// user, and because <c>AtomicFileWriter</c> passes it through unwrapped.
    /// </exception>
    /// <exception cref="BookIoException">The target could not be written.</exception>
    void Rebuild(IEnumerable<PendingEntry> entries, string targetPath);
}

/// <summary>
/// The physical container a file turned out to be, independent of the metadata
/// document inside it.
/// </summary>
public enum ContainerKind
{
    /// <summary>Not recognised.</summary>
    Unknown = 0,

    /// <summary>A single unarchived file.</summary>
    Raw,

    /// <summary>ZIP.</summary>
    Zip,

    /// <summary>RAR, versions 4 and 5. Rebuilt only through an archiver on the machine.</summary>
    Rar,

    /// <summary>7z. Rebuilt only through an archiver on the machine.</summary>
    SevenZip,

    /// <summary>TAR.</summary>
    Tar,

    /// <summary>PalmDB, the record container behind MOBI and AZW.</summary>
    PalmDb,
}

/// <summary>
/// One container implementation as <see cref="BookContainers"/> knows it: which
/// <see cref="ContainerKind"/> it is, how its bytes are recognised, and how to open
/// one. Registering this is the whole of adding a container.
/// </summary>
/// <seealso cref="ContainerSignature" />
public sealed record ContainerFormat
{
    /// <summary>The kind this implementation is responsible for.</summary>
    public required ContainerKind Kind { get; init; }

    /// <summary>Opens a file of this kind.</summary>
    public required Func<string, IContainer> Open { get; init; }

    /// <summary>
    /// The magic numbers that name this container. Empty for
    /// <see cref="ContainerKind.Raw"/>, which is what a file with no marker is.
    /// Signatures must not overlap: the sniff answers with the first that matches,
    /// and nothing should depend on registration order.
    /// </summary>
    public IReadOnlyList<ContainerSignature> Signatures { get; init; } = [];
}

/// <summary>A magic number that names a container, and where in the file it sits.</summary>
/// <seealso cref="ContainerFormat" />
public sealed record ContainerSignature
{
    /// <summary>The bytes to look for.</summary>
    public required byte[] Magic { get; init; }

    /// <summary>Where they sit. TAR's are 257 bytes into the first header block.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// How to describe the match in the log — "RAR 5 archive" — or
    /// <see langword="null"/> when the magic number speaks for itself.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>Describes a signature whose bytes are all writable as text.</summary>
    /// <param name="magic">
    /// The bytes as characters, read as Latin-1 — every character stands for the byte
    /// it looks like, so an escape above <c>0xFF</c> is a mistake.
    /// </param>
    /// <param name="detail">How to describe the match in the log.</param>
    /// <param name="offset">Where the bytes sit.</param>
    /// <returns>The signature.</returns>
    public static ContainerSignature Text(string magic, string? detail = null, int offset = 0)
    {
        Throw.IfNullOrEmpty(magic);

        return new ContainerSignature
        {
            Magic = [.. magic.Select(c => (byte)c)],
            Detail = detail,
            Offset = offset,
        };
    }

    /// <summary>Whether a file's leading bytes carry this signature.</summary>
    /// <param name="head">The first several kilobytes of the file.</param>
    /// <returns><see langword="true"/> when they do.</returns>
    public bool Matches(ReadOnlySpan<byte> head) =>
        head.Length >= Offset + Magic.Length &&
        head.Slice(Offset, Magic.Length).SequenceEqual(Magic);
}

/// <summary>Convenience over <see cref="IContainer"/> that every format needs.</summary>
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
    public required int Index { get; init; }

    /// <summary>The uncompressed size in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>
    /// The compression method as read, using ZIP method codes: 0 for stored, 8 for
    /// deflate. Carried per entry because reproducing it on rebuild is a hard
    /// invariant — a stored <c>mimetype</c> re-emitted as deflate breaks EPUB readers.
    /// </summary>
    public ushort CompressionMethod { get; init; }

    /// <summary>The last-modified timestamp recorded for the entry.</summary>
    public DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// Whether the entry is a directory marker rather than a file — a ZIP entry
    /// whose name ends in <c>/</c> and whose length is zero.
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>Returns the name and size, for diagnostics.</summary>
    public override string ToString() => $"{Name} ({Length} bytes, method {CompressionMethod})";

    /// <summary>
    /// Whether an entry name is absolute, or walks out of the archive with <c>..</c>.
    /// Hard invariant 4, and the one predicate for it, so the read path and
    /// <c>ExternalArchiver.Stage</c> cannot disagree about what "escapes" means.
    /// </summary>
    public static bool EscapesArchive(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name![0] is '/' or '\\' || System.IO.Path.IsPathRooted(name) || name.IndexOf(':') >= 0)
        {
            return true;
        }

        foreach (string segment in name.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>ZIP compression method codes, as they appear in the central directory.</summary>
public static class ZipCompressionMethods
{
    /// <summary>No compression. Required for an EPUB's <c>mimetype</c> entry.</summary>
    public const ushort Stored = 0;

    /// <summary>Deflate — the method essentially every other entry uses.</summary>
    public const ushort Deflate = 8;

    /// <summary>Returns a human-readable name for a method code.</summary>
    /// <param name="method">A ZIP compression method code.</param>
    /// <returns>The method's usual name, or <c>method N</c> if unrecognised.</returns>
    public static string ToName(ushort method) => method switch
    {
        Stored => "stored",
        Deflate => "deflate",
        _ => $"method {method}",
    };
}

/// <summary>An entry to be written by <see cref="IContainer.Rebuild"/>.</summary>
/// <seealso cref="ContainerEntry" />
public sealed class PendingEntry
{
    private readonly Func<Stream> _openContent;

    /// <summary>Creates an entry whose content is produced on demand.</summary>
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
    public ContainerEntry? Source { get; }

    /// <summary>Opens the content stream. Called once per rebuild.</summary>
    /// <returns>A readable stream the caller is responsible for disposing.</returns>
    public Stream OpenContent() => _openContent();

    /// <summary>
    /// Creates an entry that copies an existing one through unchanged, preserving its
    /// name, compression method and timestamp. Hard invariant 3: content files are
    /// copied byte for byte, never round-tripped through a parser.
    /// </summary>
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
    /// Creates an entry that takes an existing one's place with new content, keeping
    /// its name, compression method and timestamp. Use this rather than
    /// <see cref="FromBytes"/> whenever new content stands in for an existing entry —
    /// <see cref="FromBytes"/> has no <see cref="Source"/>, so a container that retains
    /// more about an entry than <see cref="ContainerEntry"/> models silently loses it.
    /// </summary>
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

/// <summary>A read-only window onto part of another stream.</summary>
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
