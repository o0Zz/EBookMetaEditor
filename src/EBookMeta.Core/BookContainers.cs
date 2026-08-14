using EBookMeta.Containers;
using EBookMeta.Formats;

namespace EBookMeta;

/// <summary>
/// Opens the container behind a file — the physical half of what
/// <see cref="BookFormats"/> does for the metadata half.
/// </summary>
/// <remarks>
/// One place decides which <see cref="IContainer"/> implementation a file gets,
/// so <see cref="Book"/> depends on the interface rather than naming a concrete
/// container. ZIP carries EPUB and CBZ; TAR carries CBT and shares the comic
/// metadata document with CBZ, so it cost a container and nothing else. A kind
/// arriving here that has no implementation is a programming error rather than a
/// bad file — <c>FormatDetector</c> has already named the format and
/// <see cref="BookFormats.Resolve"/> has already refused the ones no format can
/// edit.
/// <para>
/// RAR and 7z are deliberately absent. Both can be read, and neither can be
/// written: RAR compression is proprietary and no writer for either ships in this
/// build's dependencies, so a CBR or CB7 would open into an editor that cannot
/// save. See <see cref="IContainer.IsWritable"/>.
/// </para>
/// </remarks>
/// <seealso cref="BookFormats" />
public static class BookContainers
{
    /// <summary>Opens the container a file turned out to be.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="kind">
    /// The container the content indicates, from
    /// <see cref="DetectedFormat.Container"/>.
    /// </param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="NotSupportedException">
    /// No implementation exists for <paramref name="kind"/>.
    /// </exception>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    /// <exception cref="BookFormatException">The file is not a readable container.</exception>
    public static IContainer Open(string path, ContainerKind kind)
    {
        Throw.IfNullOrEmpty(path);

        return kind switch
        {
            ContainerKind.Zip => ZipContainer.Open(path),
            ContainerKind.Tar => TarContainer.Open(path),
            ContainerKind.Raw => RawContainer.Open(path),
            ContainerKind.PalmDb => PalmDbContainer.Open(path),
            _ => throw new NotSupportedException(
                $"{kind} containers cannot be opened by this build."),
        };
    }

    /// <summary>Whether this build has an implementation for a container.</summary>
    /// <param name="kind">The container to test.</param>
    /// <returns><see langword="true"/> when the container can be opened.</returns>
    public static bool IsSupported(ContainerKind kind) =>
        kind is ContainerKind.Zip or ContainerKind.Tar or ContainerKind.Raw
            or ContainerKind.PalmDb;

    /// <summary>
    /// Opens a container file and hands the stream to the implementation, closing
    /// it again if the implementation rejects the file.
    /// </summary>
    /// <typeparam name="T">The container type being constructed.</typeparam>
    /// <param name="path">The file to open.</param>
    /// <param name="open">Builds the container over the open stream.</param>
    /// <returns>Whatever <paramref name="open"/> returned.</returns>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    /// <remarks>
    /// <see cref="FileShare.ReadWrite"/> plus <see cref="FileShare.Delete"/> is
    /// what lets a user open a book that another program is holding, and
    /// <see cref="FileOptions.RandomAccess"/> is right for all three: a ZIP's
    /// central directory is at the end, a TAR's headers are interleaved with its
    /// data, and a PalmDB's record table is at the front. The dispose-on-throw is
    /// the part that is easy to omit and leaks a handle onto the user's file when
    /// it is.
    /// </remarks>
    internal static T OpenFile<T>(string path, Func<FileStream, T> open)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new BookIoException($"Could not open '{path}' for reading.", path, ex);
        }

        try
        {
            return open(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a second handle on a container file so one entry can be read while
    /// another is open.
    /// </summary>
    /// <param name="path">The container file.</param>
    /// <param name="entryName">The entry being read, for the error message.</param>
    /// <param name="what">How to name the entry in that message — "Entry 'x'".</param>
    /// <returns>A stream positioned at the start of the file; the caller owns it.</returns>
    /// <exception cref="BookFormatException">The file could not be reopened.</exception>
    /// <remarks>
    /// Nothing in <see cref="IContainer.OpenRead"/> promises entries are read one
    /// at a time, so the containers that slice a single file cannot hand out views
    /// over one shared handle.
    /// </remarks>
    internal static FileStream ReopenForEntry(string path, string entryName, string what)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BookFormatException($"{what} could not be read.", entryName, ex);
        }
    }
}
