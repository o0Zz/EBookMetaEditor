using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Common.Tar.Headers;
using SharpCompress.Readers;
using SharpCompress.Writers.Tar;
using System.Text;

namespace EBookMeta.Containers;

/// <summary>
/// A TAR container — the storage behind CBT. Reads and writes through SharpCompress,
/// which models an entry as a name, a size and a timestamp: mode, uid and gid are read
/// but cannot be written back, and uname and gname are not read at all.
/// </summary>
public sealed class TarContainer : IContainer
{
    private readonly TarArchive _archive;
    private readonly TarArchiveEntry[] _source;
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly ContainerEntry[] _entries;
    private bool _disposed;

    private TarContainer(
        TarArchive archive,
        TarArchiveEntry[] source,
        Stream stream,
        bool ownsStream,
        ContainerEntry[] entries,
        string? path)
    {
        _archive = archive;
        _source = source;
        _stream = stream;
        _ownsStream = ownsStream;
        _entries = entries;
        Path = path;
    }

    /// <inheritdoc />
    public bool IsWritable => true;

    /// <inheritdoc />
    public IReadOnlyList<ContainerEntry> Entries => _entries;

    /// <summary>The file this container was opened from, when it came from one.</summary>
    public string? Path { get; }

    /// <inheritdoc />
    public string? ArchiveComment => null;

    /// <summary>Opens a TAR container from a file path.</summary>
    /// <param name="path">The archive to open.</param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    /// <exception cref="BookFormatException">The file is not a readable TAR.</exception>
    public static TarContainer Open(string path)
    {
        Throw.IfNullOrEmpty(path);

        return BookContainers.OpenFile(path, stream => Open(stream, path, leaveOpen: false));
    }

    /// <summary>Opens a TAR container over an existing seekable stream.</summary>
    /// <param name="stream">A readable, seekable stream over the archive.</param>
    /// <param name="path">The originating path, for diagnostics. May be null.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave <paramref name="stream"/> open when the
    /// container is disposed.
    /// </param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookFormatException">The stream is not a readable TAR.</exception>
    public static TarContainer Open(Stream stream, string? path = null, bool leaveOpen = false)
    {
        Throw.IfNull(stream);

        if (!stream.CanSeek)
        {
            throw new BookFormatException(
                "A TAR archive must be read from a seekable stream.");
        }

        TarArchive archive;

        try
        {
            archive = TarArchive.Open(stream, new ReaderOptions { LeaveStreamOpen = true });
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            throw new BookFormatException($"'{path}' is not a readable TAR archive.", ex);
        }

        try
        {
            // Materialised once so an entry's Index keeps addressing the same archive
            // entry. TAR names are no more unique than ZIP's, so nothing looks an entry
            // up by name.
            TarArchiveEntry[] source = [.. archive.Entries];
            var entries = new ContainerEntry[source.Length];

            for (int i = 0; i < source.Length; i++)
            {
                TarArchiveEntry entry = source[i];

                entries[i] = new ContainerEntry
                {
                    Name = entry.Key ?? $"entry{i}",
                    Index = i,
                    Length = entry.IsDirectory ? 0 : entry.Size,

                    // TAR does not compress. Reported as stored so the entry counts as
                    // reproducible and the format layer, which speaks ZIP method codes,
                    // needs no special case.
                    CompressionMethod = ZipCompressionMethods.Stored,

                    // SharpCompress hands back a local DateTime, so the kind has to be
                    // converted rather than reinterpreted or the instant shifts.
                    LastModified = entry.LastModifiedTime is { } modified
                        ? new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero)
                        : default,
                    IsDirectory = entry.IsDirectory,
                };
            }

            Log.Debug($"Opened TAR archive '{path}' with {entries.Length} entries.");

            return new TarContainer(
                archive, source, stream, ownsStream: !leaveOpen, entries, path);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            archive.Dispose();
            throw new BookFormatException($"'{path}' could not be read as a TAR archive.", ex);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Stream OpenRead(ContainerEntry entry)
    {
        Throw.IfNull(entry);
        Throw.IfDisposed(_disposed, this);

        if ((uint)entry.Index >= (uint)_entries.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entry), entry.Index, "Entry index is outside this container.");
        }

        try
        {
            return _source[entry.Index].OpenEntryStream();
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            throw new BookFormatException($"Entry '{entry.Name}' could not be read.", ex);
        }
    }

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// An entry name is too long for the header this build writes (CBT-F001).
    /// </exception>
    public void Rebuild(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);
        Throw.IfDisposed(_disposed, this);

        Create(entries, targetPath);
    }

    /// <summary>Writes a TAR containing the given entries, in the order given.</summary>
    /// <param name="entries">The entries to write, in order.</param>
    /// <param name="targetPath">The file to create.</param>
    /// <exception cref="BookIoException">The target could not be written.</exception>
    /// <exception cref="BookFormatException">
    /// An entry name is too long for the header this build writes (CBT-F001).
    /// </exception>
    public static void Create(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);

        try
        {
            using var output = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            // USTAR rather than GNU: SharpCompress's GNU writer leaves the magic field
            // zeroed, and BookContainers.Sniff would not recognise this build's output.
            using var writer = new TarWriter(
                output,
                new TarWriterOptions(
                    CompressionType.None,
                    finalizeArchiveOnClose: true,
                    TarHeaderWriteFormat.USTAR));

            foreach (PendingEntry pending in entries)
            {
                // WriteDirectory emits a header with no magic at all, which would make a
                // comic whose first entry is its page folder unrecognisable to Sniff.
                // The paths on the pages carry the structure, so the marker is dropped.
                if (pending.Source?.IsDirectory == true || pending.Name.EndsWith('/'))
                {
                    Log.Debug($"Dropping the folder marker '{pending.Name}'.");
                    continue;
                }

                RefuseIfNameTooLong(pending.Name, targetPath);

                DateTime? modified = pending.LastModified == default
                    ? null
                    : pending.LastModified.UtcDateTime;

                using Stream content = pending.OpenContent();
                Stream body = content;
                long size;

                if (content.CanSeek)
                {
                    size = content.Length - content.Position;
                }
                else
                {
                    // The header states the length, so it has to be known before a
                    // byte of content is written. Everything this build produces is
                    // seekable; this is the fallback for a caller that is not.
                    var buffered = new MemoryStream();
                    content.CopyTo(buffered);
                    buffered.Position = 0;
                    body = buffered;
                    size = buffered.Length;
                }

                writer.Write(pending.Name, body, modified, size);

                if (body != content)
                {
                    body.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BookIoException($"Could not write '{targetPath}'.", ex);
        }
    }

    /// <summary>The name field of a USTAR header, which this build does not split.</summary>
    private const int MaximumNameLength = 100;

    /// <summary>
    /// Refuses a name the writer cannot express. SharpCompress never fills the ustar
    /// prefix field and throws a bare <see cref="Exception"/> instead, so the refusal
    /// is made here where it can name the entry.
    /// </summary>
    private static void RefuseIfNameTooLong(string name, string targetPath)
    {
        int length = Encoding.UTF8.GetByteCount(name);

        if (length <= MaximumNameLength)
        {
            return;
        }

        string reason =
            $"This archive cannot be saved: the entry '{name}' has a name of {length} bytes, "
            + $"and this build writes TAR headers that hold at most {MaximumNameLength}. "
            + "The file was not changed.";

        Log.Rule(LogLevel.Error, "CBT-F001", reason, targetPath);
        throw new BookFormatException(reason);
    }

    /// <summary>Whether an exception means "these bytes did not read", not a bug.</summary>
    private static bool IsUnreadable(Exception ex) =>
        ex is SharpCompressException or IOException or InvalidDataException
            or IndexOutOfRangeException or ArgumentOutOfRangeException or NotSupportedException;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archive.Dispose();

        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
