namespace EBookMeta.Containers;

/// <summary>
/// A file that is not an archive, presented as a container of exactly one entry.
/// </summary>
public sealed class RawContainer : IContainer
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly ContainerEntry _entry;
    private bool _disposed;

    private RawContainer(Stream stream, bool ownsStream, string? path, ContainerEntry entry)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _entry = entry;
        Path = path;
    }

    /// <inheritdoc />
    public bool IsWritable => true;

    /// <inheritdoc />
    public IReadOnlyList<ContainerEntry> Entries => [_entry];

    /// <summary>The file this container was opened from, when it came from one.</summary>
    public string? Path { get; }

    /// <inheritdoc />
    public string? ArchiveComment => null;

    /// <summary>Opens a file as a one-entry container.</summary>
    /// <param name="path">The file to open.</param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    public static RawContainer Open(string path)
    {
        Throw.IfNullOrEmpty(path);

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new BookIoException($"Could not open '{path}' for reading.", path, ex);
        }

        try
        {
            return Open(stream, path, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Wraps an open stream as a one-entry container.</summary>
    /// <param name="stream">A readable, seekable stream over the file.</param>
    /// <param name="path">The originating path, for diagnostics. May be null.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave <paramref name="stream"/> open when the
    /// container is disposed.
    /// </param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookFormatException">The stream cannot be measured.</exception>
    public static RawContainer Open(Stream stream, string? path = null, bool leaveOpen = false)
    {
        Throw.IfNull(stream);

        if (!stream.CanSeek)
        {
            throw new BookFormatException("A raw file must be read from a seekable stream.", path);
        }

        var entry = new ContainerEntry
        {
            Name = path is null ? "(document)" : System.IO.Path.GetFileName(path),
            Index = 0,
            Length = stream.Length,
            CompressionMethod = ZipCompressionMethods.Stored,
            LastModified = LastWriteTimeOf(path),
        };

        return new RawContainer(stream, ownsStream: !leaveOpen, path, entry);
    }

    private static DateTimeOffset LastWriteTimeOf(string? path)
    {
        if (path is null)
        {
            return default;
        }

        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <inheritdoc />
    public Stream OpenRead(ContainerEntry entry)
    {
        Throw.IfNull(entry);
        Throw.IfDisposed(_disposed, this);

        if (entry.Index != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entry), entry.Index, "A raw file has only one entry.");
        }

        // A window rather than the stream itself: the caller disposes what
        // OpenRead returns, and that must not close the container's handle.
        return new SectionStream(_stream, 0, _entry.Length, ownsStream: false);
    }

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// More than one entry was supplied. A file that is not an archive cannot hold
    /// a second one, and quietly dropping it would be worse than refusing.
    /// </exception>
    public void Rebuild(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);
        Throw.IfDisposed(_disposed, this);

        List<PendingEntry> pending = [.. entries];

        if (pending.Count != 1)
        {
            throw new BookFormatException(
                $"A raw file holds exactly one document, but {pending.Count} were supplied.",
                targetPath);
        }

        try
        {
            using var output = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            using Stream content = pending[0].OpenContent();
            content.CopyTo(output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BookIoException($"Could not write '{targetPath}'.", targetPath, ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
