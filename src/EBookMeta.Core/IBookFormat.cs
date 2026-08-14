using EBookMeta.Formats;
using EBookMeta.Model;

namespace EBookMeta;

/// <summary>
/// Reads and writes the metadata of one format — the metadata-document axis of
/// the design, and one of Core's two seams.
/// </summary>
/// <remarks>
/// Two methods, not three. Reading reports what it noticed and writing reports
/// what it corrected, so there is nowhere for a <c>Validate</c> to live.
/// <para>
/// Implementations are stateless singletons, held by <see cref="BookFormats"/>,
/// which hands the same instance to every caller including parallel batch
/// threads. Adding a format is one implementation plus one
/// <see cref="BookFormats.Register"/> call; nothing in the UI or the open path
/// changes, because both ask the registry rather than naming a format.
/// </para>
/// <para>
/// No implementation touches the user's file. Both methods work against an open
/// <see cref="IContainer"/>, and writing produces a complete new file at a path
/// <c>AtomicFileWriter</c> supplies — which is what keeps a single sanctioned
/// path for replacing a user's file.
/// </para>
/// </remarks>
/// <seealso cref="IContainer" />
public interface IBookFormat
{
    /// <summary>The format this implementation is responsible for.</summary>
    FormatId Id { get; }

    /// <summary>What this format can read and write.</summary>
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
