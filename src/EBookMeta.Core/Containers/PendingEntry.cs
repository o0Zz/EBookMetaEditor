namespace EBookMeta.Containers;

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
