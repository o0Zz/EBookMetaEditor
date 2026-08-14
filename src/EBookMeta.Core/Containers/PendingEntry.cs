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
    public PendingEntry(
        string name,
        Func<Stream> openContent,
        ushort compressionMethod = ZipCompressionMethods.Deflate,
        DateTimeOffset lastModified = default)
    {
        Throw.IfNullOrEmpty(name);
        Throw.IfNull(openContent);

        Name = name;
        _openContent = openContent;
        CompressionMethod = compressionMethod;
        LastModified = lastModified;
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
            entry.LastModified);
    }

    /// <summary>
    /// Creates an entry from bytes already in memory — a rewritten OPF or
    /// <c>ComicInfo.xml</c>.
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
