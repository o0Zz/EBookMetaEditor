using EBookMeta.Containers;
using EBookMeta.Model;

namespace EBookMeta.Formats;

/// <summary>
/// Reads and writes the metadata of one format.
/// </summary>
/// <remarks>
/// <para>
/// A handler pairs a metadata document convention with whatever container the
/// format happens to use. It receives an open <see cref="IContainer"/> and does
/// not know or care how that container stores bytes, which is what lets CBT be
/// TAR + <c>ComicInfo.xml</c> without duplicating the comic logic.
/// </para>
/// <para>
/// <b>There is no Validate method, and that is the design.</b> Checking a file is
/// not a separate operation a user asks for — it is what reading already does, so
/// <see cref="Read"/> reports what it noticed. Fixing is what writing does, so
/// <see cref="Write"/> reports what it corrected. Neither ever touches the user's
/// file on its own: a correction found on read is held in memory and reaches the
/// disk only if the user saves.
/// </para>
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
    /// <remarks>
    /// <para>
    /// Reads only the metadata document, plus the cover when asked for one. It does
    /// not hash or decompress the rest of the archive — a 300-page comic must not
    /// be walked just to show its title, because cold launch to a populated window
    /// has a 400 ms budget.
    /// </para>
    /// <para>
    /// Every check a read performs works from the parsed metadata document and from
    /// container entry <em>names</em>, which the central directory has already
    /// supplied. That is why cross-checks like "does the page count match the
    /// images" are affordable here: comparing names costs nothing, and nothing is
    /// decompressed to do it.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Every entry other than the metadata document is copied byte for byte, in
    /// its original order, at its original compression method. Content files,
    /// stylesheets and images are never round-tripped through a parser.
    /// </para>
    /// <para>
    /// This is where a file gets fixed. A write corrects what is provable from the
    /// file itself — a missing namespace declaration, a page count that disagrees
    /// with the images present — and reports each correction so the user can find
    /// out what changed. It never guesses: anything that would need an assumption
    /// is reported by <see cref="Read"/> and left alone.
    /// </para>
    /// </remarks>
    void Write(
        IContainer container,
        BookMetadata metadata,
        string targetPath,
        ICollection<Finding>? findings = null);
}
