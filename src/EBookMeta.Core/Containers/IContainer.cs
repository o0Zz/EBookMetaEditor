namespace EBookMeta.Containers;

/// <summary>
/// A file that holds named entries — a ZIP, a TAR, a 7z, a PalmDB, or a single
/// raw file presented as one entry.
/// </summary>
/// <remarks>
/// <para>
/// Container and metadata document are independent axes, and keeping them
/// separate is the main design constraint in this codebase. EPUB is ZIP + OPF;
/// CBZ is ZIP + <c>ComicInfo.xml</c>; CBT is TAR + <c>ComicInfo.xml</c>. A new
/// format is usually a new document handler over an existing container.
/// </para>
/// <para>
/// So this interface knows nothing about books: no titles, no covers, no
/// manifests. It exposes an ordered list of entries, byte access, and an atomic
/// rebuild.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Order is preserved and must be reproduced on rebuild. For EPUB it is
    /// load-bearing — <c>mimetype</c> has to be first — and for comics the
    /// entry order is the reading order.
    /// </remarks>
    IReadOnlyList<ContainerEntry> Entries { get; }

    /// <summary>
    /// Data the container carries outside its entries, which a rebuild would not
    /// reproduce — a ZIP's archive comment. <see langword="null"/> when there is
    /// none, which is the ordinary case.
    /// </summary>
    /// <remarks>
    /// On the interface rather than on <c>ZipContainer</c> because a handler has
    /// to be able to refuse a write that would lose it, and refusing is a
    /// decision about the user's file, not about ZIP. Comic archives sometimes
    /// store a ComicBookLover JSON blob here, and
    /// <c>System.IO.Compression</c> cannot write one back.
    /// </remarks>
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
    /// <remarks>
    /// Rebuild always produces a whole new file. Nothing is ever modified in
    /// place, and no archive is opened in update mode — a crash midway through
    /// must leave the user's original file untouched rather than truncated.
    /// </remarks>
    void Rebuild(IEnumerable<PendingEntry> entries, string targetPath);
}
