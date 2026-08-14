namespace EBookMeta.Containers;

/// <summary>
/// A file that holds named entries — a ZIP, a TAR, a 7z, a PalmDB, or a single
/// raw file presented as one entry.
/// </summary>
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
