namespace EBookMeta.Containers;

/// <summary>
/// A TAR container — the storage behind CBT.
/// </summary>
/// <remarks>
/// Read and written here rather than through SharpCompress, which reads a TAR
/// entry's mode, uid and gid but gives a writer no way to put them back, and
/// finalises with two zero blocks where <c>tar</c> pads to ten kilobytes. Saving
/// through it would rewrite every header in the archive to edit one title, which
/// hard invariant 6 exists to prevent.
/// <para>
/// So this is the container-level form of "a repair is an edit, not a
/// reserialisation": each entry's header blocks are retained exactly as read and
/// re-emitted byte for byte, and only the metadata document's header is touched —
/// its size field and the checksum over it. TAR has no offset table and no central
/// directory, so a resized entry shifts the bytes after it and nothing else needs
/// fixing up. That is what makes exact rebuilding practical here and impractical
/// for ZIP.
/// </para>
/// </remarks>
public sealed class TarContainer : IContainer
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly ContainerEntry[] _entries;
    private readonly EntryLayout[] _layout;
    private readonly byte[] _trailer;
    private bool _disposed;

    /// <summary>
    /// Where an entry's bytes are, and the header blocks that introduce them.
    /// </summary>
    /// <param name="Header">
    /// Every header block for the entry, in order and verbatim: the entry's own
    /// 512-byte header, preceded by any GNU long-name or PAX blocks and their data.
    /// The entry's own header is always the last block.
    /// </param>
    /// <param name="DataOffset">Where the content starts in the source.</param>
    /// <param name="DeclaresPaxSize">
    /// Whether a PAX block states the size, which a patched header would then
    /// contradict.
    /// </param>
    private sealed record EntryLayout(byte[] Header, long DataOffset, bool DeclaresPaxSize);

    private TarContainer(
        Stream stream,
        bool ownsStream,
        string? path,
        ContainerEntry[] entries,
        EntryLayout[] layout,
        byte[] trailer)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _entries = entries;
        _layout = layout;
        _trailer = trailer;
        Path = path;
    }

    /// <inheritdoc />
    public bool IsWritable => true;

    /// <inheritdoc />
    public IReadOnlyList<ContainerEntry> Entries => _entries;

    /// <summary>The file this container was opened from, when it came from one.</summary>
    public string? Path { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Always <see langword="null"/>: TAR has no archive-level metadata, so there
    /// is nothing a rebuild could fail to reproduce. A comic in a TAR therefore
    /// cannot carry the ComicBookLover blob that blocks saving a CBZ.
    /// </remarks>
    public string? ArchiveComment => null;

    /// <summary>Opens a TAR container from a file path.</summary>
    /// <param name="path">The archive to open.</param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    /// <exception cref="BookFormatException">The file is not a readable TAR.</exception>
    public static TarContainer Open(string path)
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

    /// <summary>Opens a TAR container over an existing seekable stream.</summary>
    /// <param name="stream">A readable, seekable stream over the archive.</param>
    /// <param name="path">The originating path, for diagnostics. May be null.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave <paramref name="stream"/> open when the
    /// container is disposed.
    /// </param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookFormatException">The stream is not a readable TAR.</exception>
    /// <remarks>
    /// Entries opened from a container built this way share
    /// <paramref name="stream"/> and seek within it, so reads must not overlap.
    /// <see cref="Open(string)"/> has no such restriction: it gives every read its
    /// own handle.
    /// </remarks>
    public static TarContainer Open(Stream stream, string? path = null, bool leaveOpen = false)
    {
        Throw.IfNull(stream);

        if (!stream.CanSeek)
        {
            throw new BookFormatException(
                "A TAR archive must be read from a seekable stream.", path);
        }

        var entries = new List<ContainerEntry>();
        var layout = new List<EntryLayout>();
        byte[] block = new byte[TarHeader.BlockSize];
        long position = 0;

        while (true)
        {
            stream.Position = position;

            if (!ReadExactly(stream, block, TarHeader.BlockSize))
            {
                // A truncated final block is how an archive that was cut short
                // ends. There is nothing further to read and nothing to repair;
                // whatever entries were found are still good.
                break;
            }

            // The archive ends at the first all-zero block, which is also how tar
            // itself decides. Everything from here is the trailer, reproduced
            // verbatim on write.
            if (TarHeader.IsZeroBlock(block))
            {
                break;
            }

            if (!TarHeader.ChecksumMatches(block))
            {
                throw new BookFormatException(
                    entries.Count == 0
                        ? "This file is not a readable TAR archive: the first header's "
                            + "checksum does not match."
                        : $"The TAR header after entry {entries.Count} is corrupt: its "
                            + "checksum does not match.",
                    path);
            }

            long headerStart = position;
            string? nameOverride = null;
            bool declaresPaxSize = false;
            char type = TarHeader.ReadTypeFlag(block);

            // GNU long-name and PAX blocks describe the entry that follows rather
            // than being entries themselves. Their blocks stay in the retained
            // header so a rebuild reproduces them, and their content is read for
            // the name it may override.
            while (TarHeader.IsPrefixBlock(type))
            {
                long prefixSize = TarHeader.ReadSize(block);
                if (prefixSize < 0)
                {
                    throw new BookFormatException(
                        $"A TAR extended header at offset {position} has an unreadable size.",
                        path);
                }

                byte[] data = new byte[prefixSize];
                stream.Position = position + TarHeader.BlockSize;

                if (!ReadExactly(stream, data, data.Length))
                {
                    throw new BookFormatException(
                        $"A TAR extended header at offset {position} is truncated.", path);
                }

                nameOverride ??= TarHeader.ReadNameOverride(type, data);
                declaresPaxSize |= TarHeader.DeclaresPaxSize(type, data);

                position += TarHeader.BlockSize + TarHeader.Padded(prefixSize);
                stream.Position = position;

                if (!ReadExactly(stream, block, TarHeader.BlockSize) ||
                    TarHeader.IsZeroBlock(block) ||
                    !TarHeader.ChecksumMatches(block))
                {
                    throw new BookFormatException(
                        $"A TAR extended header at offset {headerStart} is not followed by "
                        + "the entry it describes.",
                        path);
                }

                type = TarHeader.ReadTypeFlag(block);
            }

            long size = TarHeader.ReadSize(block);
            if (size < 0)
            {
                throw new BookFormatException(
                    $"The TAR header at offset {position} has an unreadable size.", path);
            }

            // Directories and links carry no content of their own, whatever their
            // size field says.
            if (!TarHeader.IsRegularFile(type))
            {
                size = 0;
            }

            long dataOffset = position + TarHeader.BlockSize;
            byte[] header = new byte[dataOffset - headerStart];

            stream.Position = headerStart;
            if (!ReadExactly(stream, header, header.Length))
            {
                throw new BookFormatException(
                    $"The TAR header at offset {headerStart} is truncated.", path);
            }

            string name = nameOverride ?? TarHeader.ReadName(block);

            entries.Add(new ContainerEntry
            {
                Name = name,
                Index = entries.Count,
                Length = size,
                CompressedLength = size,

                // TAR does not compress. Reported as stored so the entry counts as
                // reproducible and the format layer, which speaks ZIP method codes,
                // needs no special case.
                CompressionMethod = ZipCompressionMethods.Stored,
                LastModified = TarHeader.ReadLastModified(block),
                IsDirectory = type == TarHeader.TypeDirectory || name.EndsWith('/'),
            });

            layout.Add(new EntryLayout(header, dataOffset, declaresPaxSize));

            position = dataOffset + TarHeader.Padded(size);
        }

        return new TarContainer(
            stream,
            ownsStream: !leaveOpen,
            path,
            [.. entries],
            [.. layout],
            ReadTrailer(stream, position));
    }

    /// <summary>
    /// Reads everything past the last entry, so a rebuild can put it back.
    /// </summary>
    /// <remarks>
    /// The end of a TAR is two zero blocks followed by padding to the blocking
    /// factor, which is ten kilobytes by default — so reproducing it is most of
    /// what byte-identity means for a file <c>tar</c> produced. Retained verbatim
    /// rather than regenerated, because the blocking factor is a choice the
    /// producer made and this build has no way to infer it.
    /// </remarks>
    private static byte[] ReadTrailer(Stream stream, long position)
    {
        long available = stream.Length - position;

        // Beyond any plausible blocking factor this is no longer padding but
        // appended data, and holding it in memory to reproduce it would be a cost
        // out of proportion to the fidelity it buys.
        if (available <= 0 || available > MaximumTrailerLength)
        {
            return [];
        }

        byte[] trailer = new byte[available];
        stream.Position = position;

        return ReadExactly(stream, trailer, trailer.Length) ? trailer : [];
    }

    /// <summary>One mebibyte, far above the largest blocking factor tar offers.</summary>
    private const long MaximumTrailerLength = 1024 * 1024;

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

        EntryLayout layout = _layout[entry.Index];
        long length = _entries[entry.Index].Length;

        if (Path is null)
        {
            return new SectionStream(_stream, layout.DataOffset, length, ownsStream: false);
        }

        // Its own handle, so two entries can be read at once. The rebuild reads
        // entries one at a time, but nothing in the interface promises that.
        FileStream own;
        try
        {
            own = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BookFormatException(
                $"Entry '{entry.Name}' could not be read.", entry.Name, ex);
        }

        return new SectionStream(own, layout.DataOffset, length, ownsStream: true);
    }

    /// <inheritdoc />
    public void Rebuild(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);
        Throw.IfDisposed(_disposed, this);

        Write(entries, targetPath, this);
    }

    /// <summary>
    /// Writes a TAR containing the given entries, in the order given.
    /// </summary>
    /// <param name="entries">The entries to write, in order.</param>
    /// <param name="targetPath">The file to create.</param>
    /// <exception cref="BookIoException">The target could not be written.</exception>
    /// <exception cref="BookFormatException">An entry name cannot be expressed.</exception>
    /// <remarks>
    /// Every header is synthesized, because there is no source archive to take one
    /// from. <see cref="Rebuild"/> is the path that preserves them.
    /// </remarks>
    public static void Create(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);

        Write(entries, targetPath, source: null);
    }

    private static void Write(
        IEnumerable<PendingEntry> entries, string targetPath, TarContainer? source)
    {
        try
        {
            using var output = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] padding = new byte[TarHeader.BlockSize];

            foreach (PendingEntry pending in entries)
            {
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

                byte[] header = HeaderFor(pending, size, source);
                output.Write(header, 0, header.Length);
                body.CopyTo(output);

                if (body != content)
                {
                    body.Dispose();
                }

                int overhang = (int)(size % TarHeader.BlockSize);
                if (overhang != 0)
                {
                    output.Write(padding, 0, TarHeader.BlockSize - overhang);
                }
            }

            byte[] trailer = source?._trailer ?? [];

            if (trailer.Length > 0)
            {
                output.Write(trailer, 0, trailer.Length);
            }
            else
            {
                // The minimum a reader will accept as an end of archive.
                output.Write(padding, 0, TarHeader.BlockSize);
                output.Write(padding, 0, TarHeader.BlockSize);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BookIoException($"Could not write '{targetPath}'.", targetPath, ex);
        }
    }

    /// <summary>
    /// Produces the header blocks for an entry: the original where there is one,
    /// patched only if the content changed size.
    /// </summary>
    private static byte[] HeaderFor(PendingEntry pending, long size, TarContainer? source)
    {
        EntryLayout? layout = source?.LayoutOf(pending.Source);

        if (layout is null)
        {
            return TarHeader.Synthesize(pending.Name, size, pending.LastModified);
        }

        if (size == pending.Source!.Length)
        {
            // The common case by far: an entry copied through untouched, header
            // included. This is what makes saving an unedited archive produce the
            // same bytes.
            return layout.Header;
        }

        // A PAX block that states the size would contradict a patched header, and
        // rewriting PAX records is not something this build does. Starting over
        // with a clean header loses the original's mode and owner for this one
        // entry, which is the lesser harm.
        if (layout.DeclaresPaxSize)
        {
            return TarHeader.Synthesize(pending.Name, size, pending.LastModified);
        }

        byte[] header = (byte[])layout.Header.Clone();
        int last = header.Length - TarHeader.BlockSize;

        TarHeader.WithSize(header.AsSpan(last, TarHeader.BlockSize), size)
            .CopyTo(header.AsSpan(last));

        return header;
    }

    /// <summary>
    /// Returns the retained layout for an entry, if it is one of ours.
    /// </summary>
    /// <remarks>
    /// Reference equality, not the record's value equality: this asks whether the
    /// entry came out of <see cref="Entries"/> on this instance, and two distinct
    /// archives can hold entries that compare equal.
    /// </remarks>
    private EntryLayout? LayoutOf(ContainerEntry? entry)
    {
        if (entry is null || (uint)entry.Index >= (uint)_entries.Length)
        {
            return null;
        }

        return ReferenceEquals(_entries[entry.Index], entry) ? _layout[entry.Index] : null;
    }

    /// <summary>
    /// Fills a buffer, returning false if the stream ended first.
    /// </summary>
    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int total = 0;

        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return true;
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

    /// <summary>
    /// A read-only window onto part of another stream.
    /// </summary>
    /// <remarks>
    /// TAR stores entries as plain bytes at a known offset, so reading one is a
    /// bounded read rather than a decompression. Seeks to its own position on
    /// every read, which is what lets a container opened over a caller's stream
    /// hand out several of these without them losing each other's place.
    /// </remarks>
    private sealed class SectionStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private readonly bool _ownsStream;
        private long _position;

        internal SectionStream(Stream inner, long start, long length, bool ownsStream)
        {
            _inner = inner;
            _start = start;
            _length = length;
            _ownsStream = ownsStream;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value < 0 ? 0 : Math.Min(value, _length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            _inner.Position = _start + _position;

            int read = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            _position += read;

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset,
            };

            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsStream)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
