using System.IO.Compression;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace EBookMeta.Containers;

/// <summary>
/// A ZIP container — the storage behind EPUB, CBZ and FB2.ZIP.
/// </summary>
public sealed class ZipContainer : IContainer
{
    private readonly ZipArchive _archive;
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly ContainerEntry[] _entries;
    private bool _disposed;

    private ZipContainer(
        ZipArchive archive,
        Stream stream,
        bool ownsStream,
        ContainerEntry[] entries,
        string? path,
        string? archiveComment,
        bool allEntriesReproducible)
    {
        _archive = archive;
        _stream = stream;
        _ownsStream = ownsStream;
        _entries = entries;
        Path = path;
        ArchiveComment = archiveComment;
        AllEntriesUseReproducibleCompression = allEntriesReproducible;
    }

    /// <inheritdoc />
    public bool IsWritable => true;

    /// <inheritdoc />
    public IReadOnlyList<ContainerEntry> Entries => _entries;

    /// <summary>The file this container was opened from, when it came from one.</summary>
    public string? Path { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Comic archives sometimes store a ComicBookLover JSON blob here.
    /// <c>System.IO.Compression</c> cannot write archive comments, so
    /// <see cref="Rebuild"/> does not reproduce this — which is why
    /// <c>CbzHandler</c> refuses to write a file that has one rather than
    /// dropping it.
    /// </remarks>
    public string? ArchiveComment { get; }

    /// <summary>
    /// Whether every entry uses a compression method this build can reproduce
    /// exactly, meaning stored or deflate.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> means a rebuild cannot be byte-faithful: an
    /// entry compressed with bzip2, LZMA or Zstandard would be re-emitted as
    /// deflate, because <c>System.IO.Compression</c> writes nothing else.
    /// Callers warn rather than silently re-encoding.
    /// </remarks>
    public bool AllEntriesUseReproducibleCompression { get; }

    /// <summary>Opens a ZIP container from a file path.</summary>
    /// <param name="path">The archive to open.</param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    /// <exception cref="BookFormatException">The file is not a readable ZIP.</exception>
    public static ZipContainer Open(string path)
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
                FileOptions.RandomAccess);
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

    /// <summary>Opens a ZIP container over an existing seekable stream.</summary>
    /// <param name="stream">A readable, seekable stream over the archive.</param>
    /// <param name="path">The originating path, for diagnostics. May be null.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave <paramref name="stream"/> open when the
    /// container is disposed.
    /// </param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookFormatException">The stream is not a readable ZIP.</exception>
    public static ZipContainer Open(Stream stream, string? path = null, bool leaveOpen = false)
    {
        Throw.IfNull(stream);

        ZipCentralDirectory directory = ZipCentralDirectory.Read(stream, path);

        stream.Position = 0;
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new BookFormatException($"'{path}' is not a readable ZIP archive.", path, ex);
        }

        try
        {
            // A disagreement between our structural read and ZipArchive's means
            // we do not understand this file's layout. Rebuilding it would risk
            // pairing the wrong compression method onto the wrong entry, so
            // refuse rather than guess — reported to the user as GEN-F001.
            if (directory.Records.Count != archive.Entries.Count)
            {
                throw new BookFormatException(
                    $"Central directory lists {directory.Records.Count} entries but the archive " +
                    $"reads {archive.Entries.Count}. The file is inconsistent and will not be edited.",
                    path);
            }

            var entries = new ContainerEntry[archive.Entries.Count];
            bool allReproducible = true;

            for (int i = 0; i < entries.Length; i++)
            {
                ZipArchiveEntry zipEntry = archive.Entries[i];
                ZipCentralDirectoryRecord record = directory.Records[i];

                var entry = new ContainerEntry
                {
                    // Name comes from ZipArchive, not from our parser, so the
                    // two views cannot diverge on entry-name encoding.
                    Name = zipEntry.FullName,
                    Index = i,
                    Length = zipEntry.Length,
                    CompressedLength = zipEntry.CompressedLength,
                    CompressionMethod = record.CompressionMethod,
                    LastModified = zipEntry.LastWriteTime,
                    IsDirectory = zipEntry.FullName.EndsWith('/') && zipEntry.Length == 0,
                };

                entries[i] = entry;
                allReproducible &= entry.IsReproducibleCompression || entry.IsDirectory;
            }

            return new ZipContainer(
                archive, stream, ownsStream: !leaveOpen, entries, path,
                directory.ArchiveComment, allReproducible);
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
            return _archive.Entries[entry.Index].Open();
        }
        catch (InvalidDataException ex)
        {
            throw new BookFormatException(
                $"Entry '{entry.Name}' is corrupt or uses an unsupported compression method " +
                $"({ZipCompressionMethods.ToName(entry.CompressionMethod)}).",
                entry.Name,
                ex);
        }
    }

    /// <inheritdoc />
    public void Rebuild(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);
        Throw.IfDisposed(_disposed, this);

        Create(entries, targetPath);
    }

    /// <summary>
    /// Writes a ZIP containing the given entries, in the order given.
    /// </summary>
    /// <param name="entries">The entries to write, in order.</param>
    /// <param name="targetPath">The file to create.</param>
    /// <exception cref="BookIoException">The target could not be written.</exception>
    /// <remarks>
    /// SharpCompress rather than <c>System.IO.Compression</c> for one specific
    /// reason: on .NET Framework, <c>CompressionLevel.NoCompression</c> produces
    /// deflate at level 0 (method 8), not a <em>stored</em> entry. An EPUB whose
    /// <c>mimetype</c> is method 8 is rejected by readers, and there is no way to
    /// express the difference through the framework's ZIP writer. Reading stays on
    /// <c>ZipArchive</c>, which has no equivalent gap.
    /// </remarks>
    public static void Create(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);

        try
        {
            using var output = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            // Deflate is only the default; every entry overrides it below with
            // the method it was read with.
            using var writer = new ZipWriter(output, new ZipWriterOptions(CompressionType.Deflate)
            {
                LeaveStreamOpen = true,
            });

            foreach (PendingEntry pending in entries)
            {
                var options = new ZipWriterEntryOptions
                {
                    CompressionType = pending.CompressionMethod == ZipCompressionMethods.Stored
                        ? CompressionType.None
                        : CompressionType.Deflate,
                };

                if (pending.LastModified != default)
                {
                    // ZIP timestamps are DOS format and cannot predate 1980.
                    // Clamp rather than throw: an odd timestamp on a source file
                    // is not a reason to refuse the user's metadata edit.
                    DateTimeOffset stamp = pending.LastModified < ZipMinimumTimestamp
                        ? ZipMinimumTimestamp
                        : pending.LastModified;

                    options.ModificationDateTime = stamp.UtcDateTime;
                }

                // Entries are written in the order supplied, which is what puts
                // mimetype first in an EPUB.
                using Stream source = pending.OpenContent();
                writer.Write(pending.Name, source, options);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BookIoException($"Could not write '{targetPath}'.", targetPath, ex);
        }
    }

    private static readonly DateTimeOffset ZipMinimumTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
