using EBookMeta.Containers;

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
