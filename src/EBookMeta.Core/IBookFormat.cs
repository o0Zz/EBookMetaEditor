using EBookMeta.Model;

namespace EBookMeta;

/// <summary>
/// Reads and writes the metadata of one format — the metadata-document axis of
/// the design, and one of Core's two seams.
/// </summary>
/// <seealso cref="IContainer" />
public interface IBookFormat
{
    /// <summary>The format this implementation is responsible for.</summary>
    FormatId Id { get; }

    /// <summary>What this format can read and write.</summary>
    FormatCapabilities Capabilities { get; }

    /// <summary>
    /// The file extensions that claim this format, lowercase and with the dot.
    /// </summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// Says whether a file is one of this format's, having looked inside it.
    /// <b>Claim; do not parse, and never throw</b> — returning <see langword="null"/> is
    /// how a format declines, and an exception abandons the loop before the remaining
    /// formats are asked. A damaged file is still its own format's.
    /// </summary>
    FormatClaim? TryOpen(BookSource source);

    /// <summary>Reads metadata from an open container.</summary>
    /// <param name="container">The open container.</param>
    /// <returns>The metadata found.</returns>
    /// <exception cref="BookFormatException">
    /// The metadata document is missing or too malformed to parse. Repair is
    /// offered separately; this means the read could not produce a result.
    /// </exception>
    /// <param name="options">
    /// How much to read. <see langword="null"/> means
    /// <see cref="ReadOptions.Default"/>, which reads everything — the optional
    /// parameter exists so a single-file editor never has to say so, while a batch
    /// read can ask for metadata without covers.
    /// </param>
    BookMetadata Read(
        IContainer container, ReadOptions? options = null);

    /// <summary>
    /// Writes metadata, producing a complete new file at
    /// <paramref name="targetPath"/>.
    /// </summary>
    /// <param name="container">The open source container.</param>
    /// <param name="metadata">The metadata to write.</param>
    /// <param name="targetPath">
    /// Where to write. Supplied by <c>AtomicFileWriter</c>, and normally a
    /// temporary sibling of the user's file rather than the file itself.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// <see cref="FormatCapabilities.CanWrite"/> is <see langword="false"/>.
    /// </exception>
    void Write(
        IContainer container,
        BookMetadata metadata,
        string targetPath);
}

/// <summary>A file format EBookMetaEditor recognises.</summary>
public enum FormatId
{
    /// <summary>Not recognised.</summary>
    Unknown = 0,

    /// <summary>EPUB 2 or 3. ZIP + OPF package document.</summary>
    Epub,

    /// <summary>Comic archive, ZIP. <c>ComicInfo.xml</c>.</summary>
    Cbz,

    /// <summary>
    /// Comic archive, RAR. <c>ComicInfo.xml</c>, read but never written — see
    /// <c>RarContainer</c>.
    /// </summary>
    Cbr,

    /// <summary>Comic archive, 7z.</summary>
    Cb7,

    /// <summary>Comic archive, TAR.</summary>
    Cbt,

    /// <summary>MOBI or PRC. PalmDB + EXTH records.</summary>
    Mobi,

    /// <summary>AZW or AZW3 (KF8). PalmDB + EXTH records, possibly two sets.</summary>
    Azw3,

    /// <summary>FictionBook 2, uncompressed XML.</summary>
    Fb2,

    /// <summary>FictionBook 2 inside a ZIP.</summary>
    Fb2Zip,

    /// <summary>PDF. Info dictionary + XMP.</summary>
    Pdf,

    /// <summary>
    /// A ZIP that is none of the above — recognised as an archive, but not as
    /// anything EBookMetaEditor edits.
    /// </summary>
    UnknownZip,
}

/// <summary>Describes a <see cref="FormatId"/>.</summary>
public static class FormatIdExtensions
{
    /// <summary>The conventional display name for a format.</summary>
    /// <param name="id">The format.</param>
    /// <returns>A short human-readable name.</returns>
    public static string DisplayName(this FormatId id) => id switch
    {
        FormatId.Epub => "EPUB",
        FormatId.Cbz => "CBZ",
        FormatId.Cbr => "CBR",
        FormatId.Cb7 => "CB7",
        FormatId.Cbt => "CBT",
        FormatId.Mobi => "MOBI",
        FormatId.Azw3 => "AZW3",
        FormatId.Fb2 => "FB2",
        FormatId.Fb2Zip => "FB2.ZIP",
        FormatId.Pdf => "PDF",
        FormatId.UnknownZip => "ZIP",
        _ => "unknown",
    };
}

/// <summary>
/// The fields of <see cref="BookMetadata"/>, as a flag set, so a format can
/// declare which of them it is able to store.
/// </summary>
[Flags]
public enum MetadataField
{
    /// <summary>No fields.</summary>
    None = 0,

    /// <summary>Title.</summary>
    Title = 1 << 0,

    /// <summary>Sort title.</summary>
    SortTitle = 1 << 1,

    /// <summary>Creator names.</summary>
    Creators = 1 << 2,

    /// <summary>Per-creator roles.</summary>
    CreatorRoles = 1 << 3,

    /// <summary>Series name.</summary>
    Series = 1 << 4,

    /// <summary>Position within the series.</summary>
    SeriesIndex = 1 << 5,

    /// <summary>Description or synopsis.</summary>
    Description = 1 << 6,

    /// <summary>Publisher.</summary>
    Publisher = 1 << 7,

    /// <summary>Publication date.</summary>
    PublicationDate = 1 << 8,

    /// <summary>Language.</summary>
    Language = 1 << 9,

    /// <summary>Subjects, genres or tags.</summary>
    Subjects = 1 << 10,

    /// <summary>Scheme-qualified identifiers.</summary>
    Identifiers = 1 << 11,

    /// <summary>Rights statement.</summary>
    Rights = 1 << 12,

    /// <summary>Cover image.</summary>
    Cover = 1 << 13,

    /// <summary>Everything.</summary>
    All = (1 << 14) - 1,
}

/// <summary>
/// What a format can store when it writes, and whether it can write at all. Both
/// editors read this to disable fields, so a user never types into a box whose
/// content would be discarded.
/// </summary>
public sealed record FormatCapabilities
{
    /// <summary>
    /// Fields this format can store on write. <see cref="MetadataField.None"/>
    /// when <see cref="CanWrite"/> is <see langword="false"/>.
    /// </summary>
    public MetadataField WritableFields { get; init; } = MetadataField.None;

    /// <summary>Whether this format can be written at all.</summary>
    public bool CanWrite => WritableFields != MetadataField.None;

    /// <summary>Returns whether every field in <paramref name="fields"/> can be written.</summary>
    /// <param name="fields">The fields an edit would touch.</param>
    /// <returns><see langword="true"/> if all of them are writable.</returns>
    public bool CanWriteAll(MetadataField fields) => (WritableFields & fields) == fields;
}

/// <summary>How much of a file a read should bother with.</summary>
public sealed record ReadOptions
{
    /// <summary>Read everything — the default a single-file editor wants.</summary>
    public static ReadOptions Default { get; } = new();

    /// <summary>Read the metadata only, leaving the cover image alone.</summary>
    public static ReadOptions WithoutCover { get; } = new() { IncludeCover = false };

    /// <summary>Whether to load the cover image bytes.</summary>
    public bool IncludeCover { get; init; } = true;
}

/// <summary>How sure a format is that a file is one of its own.</summary>
public enum MatchConfidence
{
    /// <summary>A convention rather than a marker — an archive of only images.</summary>
    Weak = 1,

    /// <summary>A document this format owns is present, by name.</summary>
    Strong = 2,

    /// <summary>A marker the specification requires, checked by content.</summary>
    Certain = 3,
}

/// <summary>One format's claim on a file.</summary>
public sealed record FormatClaim
{
    /// <summary>The format the content turned out to be.</summary>
    public required FormatId Format { get; init; }

    /// <summary>How the decision was reached — "first entry is ComicInfo.xml".</summary>
    public required string Detail { get; init; }

    /// <summary>How sure the format is.</summary>
    public MatchConfidence Confidence { get; init; } = MatchConfidence.Strong;
}

/// <summary>
/// A candidate file, offered to each format in turn so it can say whether the file
/// is one of its own.
/// </summary>
public sealed class BookSource : IDisposable
{
    /// <summary>How many bytes the magic-number pass reads.</summary>
    private const int HeaderLength = 8192;

    private readonly byte[] _head;
    private readonly int _headLength;
    private IContainer? _container;
    private bool _disposed;

    private BookSource(string path, byte[] head, int headLength, ContainerKind kind, string? detail)
    {
        Path = path;
        _head = head;
        _headLength = headLength;
        ContainerKind = kind;
        ContainerDetail = detail;
    }

    /// <summary>The file this describes.</summary>
    public string Path { get; }

    /// <summary>The container its leading bytes indicate.</summary>
    public ContainerKind ContainerKind { get; }

    /// <summary>
    /// How the container was recognised — "RAR 5 archive" — or <see langword="null"/>
    /// when the magic number speaks for itself.
    /// </summary>
    public string? ContainerDetail { get; }

    /// <summary>The start of the file, for formats that have no magic number.</summary>
    public ReadOnlySpan<byte> Head => _head.AsSpan(0, _headLength);

    /// <summary>The open container, opened on first use and shared from then on.</summary>
    /// <exception cref="NotSupportedException">
    /// This build has no container implementation for these bytes.
    /// </exception>
    /// <exception cref="BookFormatException">The file is not a readable container.</exception>
    public IContainer Container
    {
        get
        {
            Throw.IfDisposed(_disposed, this);
            return _container ??= BookContainers.Open(Path, ContainerKind);
        }
    }

    /// <summary>Opens a file for identification.</summary>
    /// <param name="path">The file to inspect.</param>
    /// <returns>The source; the caller disposes it.</returns>
    /// <exception cref="BookIoException">The file could not be read.</exception>
    public static BookSource Open(string path)
    {
        byte[] head = new byte[HeaderLength];
        int read;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: HeaderLength,
                FileOptions.SequentialScan);

            read = stream.ReadAtLeast(head, HeaderLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new BookIoException($"Could not read '{path}'.", ex);
        }

        (ContainerKind kind, string? detail) = BookContainers.Sniff(head.AsSpan(0, read));

        return new BookSource(path, head, read, kind, detail);
    }

    /// <summary>Whether the leading bytes start with a given magic number.</summary>
    /// <param name="magic">The bytes to look for at offset zero.</param>
    /// <returns><see langword="true"/> when they are there.</returns>
    public bool HeadStartsWith(ReadOnlySpan<byte> magic) => Head.StartsWith(magic);

    /// <summary>Decodes the start of the file as text, for formats with no magic number.</summary>
    /// <param name="maxBytes">How far in to look.</param>
    /// <returns>
    /// The decoded text, or an empty string when the file does not begin like XML.
    /// </returns>
    public string LeadingText(int maxBytes = 2048)
    {
        ReadOnlySpan<byte> head = Head;
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        ReadOnlySpan<byte> body = head.StartsWith(bom) ? head.Slice(bom.Length) : head;

        return body.IsEmpty || body[0] != (byte)'<'
            ? string.Empty
            : System.Text.Encoding.UTF8.GetString(body.Slice(0, Math.Min(body.Length, maxBytes)));
    }

    /// <summary>Closes the container, if one was opened.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _container?.Dispose();
        _container = null;
    }
}
