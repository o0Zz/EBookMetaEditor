using EBookMeta.Containers;

namespace EBookMeta;

/// <summary>
/// Opens the container behind a file — the physical half of what
/// <see cref="BookFormats"/> does for the metadata half.
/// </summary>
/// <seealso cref="BookFormats" />
public static class BookContainers
{
    private static readonly Dictionary<ContainerKind, ContainerFormat> Registered = [];

    static BookContainers()
    {
        // The whole inventory. A new container is one file under Containers/ and one
        // line here; nothing above this reaches for an implementation by name.
        Register(ContainerKind.Zip, ZipContainer.Open,
            Magic("PK\x03\x04"), Magic("PK\x05\x06"), Magic("PK\x07\x08"));

        Register(ContainerKind.Rar, RarContainer.Open,
            Magic("Rar!\x1a\x07\x01\x00", "RAR 5 archive"),
            Magic("Rar!\x1a\x07\x00", "RAR 4 archive"));

        Register(ContainerKind.SevenZip, SevenZipContainer.Open,
            new ContainerSignature
            {
                Magic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C],
                Detail = "7z archive",
            });

        // TAR's magic sits inside the first header block, not at offset zero, and
        // PalmDB's type-and-creator pair sits at offset 60.
        Register(ContainerKind.Tar, TarContainer.Open, Magic("ustar", "TAR archive", 257));

        Register(ContainerKind.PalmDb, PalmDbContainer.Open,
            Magic("BOOKMOBI", "PalmDB BOOKMOBI", 60),
            Magic("TEXtREAd", "PalmDB TEXtREAd", 60));

        // No signature: a file carrying no marker at all is a raw one.
        Register(ContainerKind.Raw, RawContainer.Open);
    }

    /// <summary>Every registered container.</summary>
    public static IReadOnlyCollection<ContainerFormat> All => Registered.Values;

    /// <summary>Registers a container, replacing any previous one for its kind.</summary>
    /// <param name="container">The container to register.</param>
    public static void Register(ContainerFormat container)
    {
        Throw.IfNull(container);
        Registered[container.Kind] = container;
    }

    /// <summary>The implementation for a kind, if this build has one.</summary>
    /// <param name="kind">The kind to look up.</param>
    /// <returns>
    /// The implementation, or <see langword="null"/> when nothing handles it.
    /// </returns>
    public static ContainerFormat? For(ContainerKind kind) =>
        Registered.TryGetValue(kind, out ContainerFormat? registered) ? registered : null;

    private static void Register(
        ContainerKind kind, Func<string, IContainer> open, params ContainerSignature[] signatures) =>
        Register(new ContainerFormat { Kind = kind, Open = open, Signatures = signatures });

    private static ContainerSignature Magic(string magic, string? detail = null, int offset = 0) =>
        new()
        {
            // Latin-1, so each character is the byte it looks like — every magic
            // number here is ASCII or an escape below 0x100.
            Magic = [.. magic.Select(c => (byte)c)],
            Detail = detail,
            Offset = offset,
        };

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

        return For(kind) is { } container
            ? container.Open(path)
            : throw new NotSupportedException(
                $"{kind} containers cannot be opened by this build.");
    }

    /// <summary>Names the physical container a file's leading bytes indicate.</summary>
    /// <param name="head">The first several kilobytes of the file.</param>
    /// <returns>
    /// The container kind and a short note on how it was recognised, or
    /// <see cref="ContainerKind.Raw"/> when nothing archive-shaped was found.
    /// </returns>
    public static (ContainerKind Kind, string? Detail) Sniff(ReadOnlySpan<byte> head)
    {
        foreach (ContainerFormat container in Registered.Values)
        {
            foreach (ContainerSignature signature in container.Signatures)
            {
                if (signature.Matches(head))
                {
                    return (container.Kind, signature.Detail);
                }
            }
        }

        return (ContainerKind.Raw, null);
    }

    /// <summary>
    /// Opens a container file and hands the stream to the implementation, closing
    /// it again if the implementation rejects the file.
    /// </summary>
    /// <typeparam name="T">The container type being constructed.</typeparam>
    /// <param name="path">The file to open.</param>
    /// <param name="open">Builds the container over the open stream.</param>
    /// <returns>Whatever <paramref name="open"/> returned.</returns>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
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
            throw new BookIoException($"Could not open '{path}' for reading.", ex);
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
    /// <param name="what">How to name the entry in the error message — "Entry 'x'".</param>
    /// <returns>A stream positioned at the start of the file; the caller owns it.</returns>
    /// <exception cref="BookFormatException">The file could not be reopened.</exception>
    internal static FileStream ReopenForEntry(string path, string what)
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
            throw new BookFormatException($"{what} could not be read.", ex);
        }
    }
}
