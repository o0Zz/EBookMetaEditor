using EBookMeta.Containers;
using EBookMeta.Model;

namespace EBookMeta;

/// <summary>
/// One book or comic, open for editing: what it is, what its metadata says, and
/// what was noticed about it.
/// </summary>
public sealed class Book
{
    private readonly IBookFormat _format;

    private Book(
        string path,
        DetectedFormat detected,
        IBookFormat format,
        BookMetadata metadata,
        int entryCount)
    {
        Path = path;
        Detected = detected;
        _format = format;
        Metadata = metadata;
        EntryCount = entryCount;
    }

    /// <summary>The file this was loaded from, and the file <see cref="Save"/> replaces.</summary>
    public string Path { get; }

    /// <summary>What the content turned out to be, and whether the extension agreed.</summary>
    public DetectedFormat Detected { get; }

    /// <summary>Which fields this file's format can store, and whether it can be written.</summary>
    public FormatCapabilities Capabilities => _format.Capabilities;

    /// <summary>
    /// The metadata, mutable in place. Edits reach the file only through
    /// <see cref="Save"/>.
    /// </summary>
    public BookMetadata Metadata { get; }

    /// <summary>How many entries the container held when it was read.</summary>
    public int EntryCount { get; }

    /// <summary>Whether this format can be written at all.</summary>
    public bool CanSave => Capabilities.CanWrite;

    /// <summary>Opens a file for editing.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="options">
    /// How much to read. <see langword="null"/> means <see cref="ReadOptions.Default"/>;
    /// a grid of hundreds of rows wants <see cref="ReadOptions.WithoutCover"/>.
    /// </param>
    /// <returns>The open book.</returns>
    /// <exception cref="UnsupportedFormatException">
    /// The file was recognised but no registered <see cref="IBookFormat"/> can
    /// edit it — a 7z archive with a <c>.cbz</c> extension, or a PDF.
    /// </exception>
    /// <exception cref="BookFormatException">
    /// The file is damaged beyond what can be recovered on the way in.
    /// </exception>
    /// <exception cref="BookIoException">The file could not be read.</exception>
    public static Book Load(string path, ReadOptions? options = null)
    {
        Throw.IfNullOrEmpty(path);

        // Offered to every registered format; the strongest claim wins and the file
        // comes back still open, so the read below does not reopen it.
        using BookSource? source = BookFormats.TryOpen(path, out DetectedFormat detected);

        string name = System.IO.Path.GetFileName(path);
        string detail = detected.Detail is null ? "." : $" ({detected.Detail}).";

        if (!detected.ExtensionAgrees)
        {
            Log.Rule(
                LogLevel.Warning,
                "GEN-W002",
                $"The extension says {detected.ClaimedByExtension.DisplayName()} "
                    + $"but the content is {detected.Format.DisplayName()}{detail}",
                name);
        }

        if (source is null)
        {
            Log.Rule(
                LogLevel.Error,
                "GEN-W004",
                $"{detected.Format.DisplayName()} is recognised but cannot be edited "
                    + $"by this build{detail}",
                name);

            throw new UnsupportedFormatException(detected, path);
        }

        IBookFormat format = BookFormats.For(detected.Format)
            ?? throw new UnsupportedFormatException(detected, path);

        IContainer container = source.Container;

        CheckEntryNames(container);

        BookMetadata metadata = format.Read(container, options);

        return new Book(path, detected, format, metadata, container.Entries.Count);
    }

    /// <summary>
    /// Writes the metadata, correcting whatever can be corrected, and replaces the
    /// original atomically.
    /// </summary>
    /// <param name="keepBackup">
    /// Whether to leave the previous version beside the file as <c>.bak</c>.
    /// </param>
    /// <returns>The backup's path, or <see langword="null"/> if none was kept.</returns>
    /// <exception cref="NotSupportedException">This format cannot be written.</exception>
    /// <exception cref="BookFormatException">
    /// The file cannot be rebuilt without losing something — a comic archive
    /// carrying a ZIP comment, for instance.
    /// </exception>
    /// <exception cref="BookIoException">
    /// The write or the atomic swap failed. The original is unchanged.
    /// </exception>
    public string? Save(bool keepBackup = true)
    {
        if (!CanSave)
        {
            throw new NotSupportedException(
                $"{Detected.Format.DisplayName()} cannot be written by this build.");
        }

        return AtomicFileWriter.Write(
            Path,
            temp =>
            {
                // Reopened inside the callback so the source handle is closed
                // before File.Replace swaps the file underneath it.
                using IContainer container = BookContainers.Open(Path, Detected.Container);
                _format.Write(container, Metadata, temp);
            },
            keepBackup);
    }

    /// <summary>Returns the file name and what it turned out to be, for diagnostics.</summary>
    /// <returns>A short description.</returns>
    public override string ToString() =>
        $"{System.IO.Path.GetFileName(Path)} ({Detected.Format.DisplayName()})";

    /// <summary>
    /// Reports entry names that point outside the archive.
    /// </summary>
    private static void CheckEntryNames(IContainer container)
    {
        foreach (ContainerEntry entry in container.Entries)
        {
            if (!ContainerEntry.EscapesArchive(entry.Name))
            {
                continue;
            }

            Log.Rule(
                LogLevel.Error,
                "GEN-E003",
                "Entry name is absolute or escapes the archive.",
                entry.Name);
        }
    }
}
