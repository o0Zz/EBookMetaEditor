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
}
