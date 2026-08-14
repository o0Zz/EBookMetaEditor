using EBookMeta.Containers;
using EBookMeta.Model;

namespace EBookMeta.Formats;

/// <summary>
/// Reads and writes the metadata of one format.
/// </summary>
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
    /// <param name="options">
    /// How much to read. <see langword="null"/> means
    /// <see cref="ReadOptions.Default"/>, which reads everything — the optional
    /// parameter exists so a single-file editor never has to say so, while a batch
    /// read can ask for metadata without covers.
    /// </param>
    /// <param name="findings">
    /// Collects what the read noticed, by stable rule ID. <see langword="null"/>
    /// discards them — a caller that only wants the metadata need not care.
    /// </param>
    BookMetadata Read(
        IContainer container, ReadOptions? options = null, ICollection<Finding>? findings = null);

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
    /// <param name="findings">
    /// Collects what the write corrected, by stable rule ID. <see langword="null"/>
    /// discards them.
    /// </param>
    void Write(
        IContainer container,
        BookMetadata metadata,
        string targetPath,
        ICollection<Finding>? findings = null);
}
