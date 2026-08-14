using EBookMeta.Containers;
using EBookMeta.Model;

namespace EBookMeta.Formats;

/// <summary>
/// Reads, validates and writes the metadata of one format.
/// </summary>
/// <remarks>
/// A handler pairs a metadata document convention with whatever container the
/// format happens to use. It receives an open <see cref="IContainer"/> and does
/// not know or care how that container stores bytes, which is what lets CBT be
/// TAR + <c>ComicInfo.xml</c> without duplicating the comic logic.
/// </remarks>
public interface IFormatHandler
{
    /// <summary>The format this handler is responsible for.</summary>
    FormatId Id { get; }

    /// <summary>What this handler can read and write.</summary>
    FormatCapabilities Capabilities { get; }

    /// <summary>Reads metadata from an open container.</summary>
    /// <param name="container">The open container.</param>
    /// <returns>The metadata found.</returns>
    /// <exception cref="BookFormatException">
    /// The metadata document is missing or too malformed to parse. Repair is
    /// offered separately; this means the read could not produce a result.
    /// </exception>
    /// <remarks>
    /// Reads only the metadata document. It does not enumerate, hash or
    /// decompress the rest of the archive — a 300-page comic must not be walked
    /// just to show its title, because cold launch to a populated window has a
    /// 400 ms budget.
    /// </remarks>
    BookMetadata Read(IContainer container);

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
    /// <remarks>
    /// Every entry other than the metadata document is copied byte for byte, in
    /// its original order, at its original compression method. Content files,
    /// stylesheets and images are never round-tripped through a parser.
    /// </remarks>
    void Write(IContainer container, BookMetadata metadata, string targetPath);

    /// <summary>Validates a file, returning findings by stable rule ID.</summary>
    /// <param name="container">The open container.</param>
    /// <param name="metadata">The metadata already read from it.</param>
    /// <returns>The findings, in no guaranteed order.</returns>
    IEnumerable<Finding> Validate(IContainer container, BookMetadata metadata);
}
