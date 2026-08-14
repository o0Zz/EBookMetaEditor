using System.Text;

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
        FileStream own = BookContainers.ReopenForEntry(
            Path, entry.Name, $"Entry '{entry.Name}'");

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
}

/// <summary>
/// The 512-byte TAR header block: reading the fields this build understands, and
/// producing one for an entry it has to write.
/// </summary>
/// <remarks>
/// Separated from <see cref="TarContainer"/> for the same reason
/// <see cref="ZipCentralDirectory"/> is separated from <see cref="ZipContainer"/>:
/// the byte-level format is a subject of its own, and the fiddly parts are worth
/// keeping in one place.
/// <para>
/// A header is fixed-width ASCII. Numbers are octal, terminated by a NUL or a
/// space, and the layout has not changed since V7 UNIX — later formats (ustar,
/// GNU, PAX) only add meaning to bytes the original left as padding. That is why
/// this build can be faithful to headers it does not fully understand: the fields
/// it cares about sit at fixed offsets, and everything else can be copied
/// through.
/// </para>
/// </remarks>
internal static class TarHeader
{
    /// <summary>The size of every block in a TAR archive, header or data.</summary>
    internal const int BlockSize = 512;

    private const int NameOffset = 0;
    private const int NameLength = 100;
    private const int ModeOffset = 100;
    private const int UidOffset = 108;
    private const int GidOffset = 116;
    private const int SizeOffset = 124;
    private const int SizeLength = 12;
    private const int ModifiedOffset = 136;
    private const int ModifiedLength = 12;
    private const int ChecksumOffset = 148;
    private const int ChecksumLength = 8;
    private const int TypeFlagOffset = 156;
    private const int MagicOffset = 257;
    private const int PrefixOffset = 345;
    private const int PrefixLength = 155;

    /// <summary>A regular file. V7 wrote a NUL here; ustar writes '0'.</summary>
    internal const char TypeRegular = '0';

    /// <summary>A regular file, as the oldest archives spell it.</summary>
    internal const char TypeRegularLegacy = '\0';

    /// <summary>A directory.</summary>
    internal const char TypeDirectory = '5';

    /// <summary>GNU long name: the following data blocks hold the real name.</summary>
    internal const char TypeGnuLongName = 'L';

    /// <summary>GNU long link target, structured like <see cref="TypeGnuLongName"/>.</summary>
    internal const char TypeGnuLongLink = 'K';

    /// <summary>A PAX extended header, applying to the entry that follows.</summary>
    internal const char TypePaxExtended = 'x';

    /// <summary>A PAX extended header, as some producers spell it.</summary>
    internal const char TypePaxExtendedUpper = 'X';

    /// <summary>A PAX global header, applying to the rest of the archive.</summary>
    internal const char TypePaxGlobal = 'g';

    /// <summary>
    /// Names and PAX records are decoded as UTF-8, which is what every current
    /// producer writes. Non-throwing, because a name this build cannot decode is
    /// still a name it must round-trip: the bytes are copied from the retained
    /// header either way, and only the display string would be affected.
    /// </summary>
    private static readonly UTF8Encoding NameEncoding = new(false, throwOnInvalidBytes: false);

    /// <summary>Whether a block is entirely zero, which is how an archive ends.</summary>
    /// <param name="block">A 512-byte block.</param>
    /// <returns><see langword="true"/> when every byte is zero.</returns>
    internal static bool IsZeroBlock(ReadOnlySpan<byte> block)
    {
        foreach (byte value in block)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the block's stored checksum matches the bytes around it.
    /// </summary>
    /// <param name="block">A 512-byte block.</param>
    /// <returns><see langword="true"/> when the block is a plausible header.</returns>
    /// <remarks>
    /// This is the only structural check TAR offers — there is no magic number at
    /// offset 0 and no central directory to cross-check against — so it is what
    /// stands between us and reading a truncated or mis-sniffed file as if it
    /// held entries.
    /// <para>
    /// Both signed and unsigned sums are accepted. The field was historically
    /// computed with a <c>char</c> that was signed on some platforms, so archives
    /// written by old tools on those platforms carry the signed sum, and readers
    /// are expected to tolerate both.
    /// </para>
    /// </remarks>
    internal static bool ChecksumMatches(ReadOnlySpan<byte> block)
    {
        long stored = ReadOctal(block.Slice(ChecksumOffset, ChecksumLength));
        if (stored < 0)
        {
            return false;
        }

        int unsigned = 0;
        int signed = 0;

        for (int i = 0; i < BlockSize; i++)
        {
            // The checksum field itself is summed as if it held spaces, since its
            // contents cannot be known while computing it.
            byte value = i >= ChecksumOffset && i < ChecksumOffset + ChecksumLength
                ? (byte)' '
                : block[i];

            unsigned += value;
            signed += (sbyte)value;
        }

        return stored == unsigned || stored == signed;
    }

    /// <summary>Reads the entry name, joining the ustar prefix when there is one.</summary>
    /// <param name="block">A 512-byte header block.</param>
    /// <returns>The name, with forward slashes, exactly as stored.</returns>
    internal static string ReadName(ReadOnlySpan<byte> block)
    {
        string name = ReadString(block.Slice(NameOffset, NameLength));

        // The prefix field is ustar's answer to the 100-byte name limit: the real
        // name is prefix + '/' + name. It is only meaningful when the ustar magic
        // is present, because older archives use those bytes as padding.
        if (!HasUstarMagic(block))
        {
            return name;
        }

        string prefix = ReadString(block.Slice(PrefixOffset, PrefixLength));
        return prefix.Length == 0 ? name : prefix + "/" + name;
    }

    /// <summary>Reads the entry's content length in bytes.</summary>
    /// <param name="block">A 512-byte header block.</param>
    /// <returns>The length, or -1 when the field is unreadable.</returns>
    internal static long ReadSize(ReadOnlySpan<byte> block) =>
        ReadOctal(block.Slice(SizeOffset, SizeLength));

    /// <summary>Reads the modification time.</summary>
    /// <param name="block">A 512-byte header block.</param>
    /// <returns>The timestamp, or <see langword="default"/> when unreadable.</returns>
    internal static DateTimeOffset ReadLastModified(ReadOnlySpan<byte> block)
    {
        long seconds = ReadOctal(block.Slice(ModifiedOffset, ModifiedLength));

        if (seconds < 0)
        {
            return default;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
    }

    /// <summary>Reads the type flag, which says what kind of entry this is.</summary>
    /// <param name="block">A 512-byte header block.</param>
    /// <returns>The type flag character.</returns>
    internal static char ReadTypeFlag(ReadOnlySpan<byte> block) => (char)block[TypeFlagOffset];

    /// <summary>Whether a type flag describes something with file content.</summary>
    /// <param name="type">A type flag from <see cref="ReadTypeFlag"/>.</param>
    /// <returns><see langword="true"/> for a regular file.</returns>
    internal static bool IsRegularFile(char type) =>
        type is TypeRegular or TypeRegularLegacy;

    /// <summary>
    /// Whether a type flag introduces metadata for the entry that follows rather
    /// than an entry of its own.
    /// </summary>
    /// <param name="type">A type flag from <see cref="ReadTypeFlag"/>.</param>
    /// <returns><see langword="true"/> for GNU long-name and PAX header blocks.</returns>
    internal static bool IsPrefixBlock(char type) =>
        type is TypeGnuLongName or TypeGnuLongLink
            or TypePaxExtended or TypePaxExtendedUpper or TypePaxGlobal;

    /// <summary>Rounds a content length up to a whole number of blocks.</summary>
    /// <param name="size">A content length in bytes.</param>
    /// <returns>The number of bytes the content occupies, including padding.</returns>
    internal static long Padded(long size) => (size + BlockSize - 1) / BlockSize * BlockSize;

    /// <summary>
    /// Extracts the <c>path</c> override from a GNU long-name or PAX extended
    /// header's data.
    /// </summary>
    /// <param name="type">The type flag of the block the data belongs to.</param>
    /// <param name="data">The data blocks' content, trimmed to the declared size.</param>
    /// <returns>The name it declares, or <see langword="null"/> if it declares none.</returns>
    internal static string? ReadNameOverride(char type, ReadOnlySpan<byte> data)
    {
        if (type == TypeGnuLongName)
        {
            // GNU stores the name raw, usually with a trailing NUL.
            return ReadString(data);
        }

        if (type is not (TypePaxExtended or TypePaxExtendedUpper))
        {
            return null;
        }

        // PAX records are "<length> <key>=<value>\n", where length counts the whole
        // record including itself.
        string text = NameEncoding.GetString(data.ToArray());
        int position = 0;

        while (position < text.Length)
        {
            int space = text.IndexOf(' ', position);
            if (space < 0 ||
                !int.TryParse(
                    text.Substring(position, space - position),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int length) ||
                length <= 0 ||
                position + length > text.Length)
            {
                return null;
            }

            string record = text.Substring(space + 1, position + length - space - 1).TrimEnd('\n');
            if (record.StartsWith("path=", StringComparison.Ordinal))
            {
                return record.Substring("path=".Length);
            }

            position += length;
        }

        return null;
    }

    /// <summary>
    /// Whether a PAX extended header declares a <c>size</c>, which would
    /// contradict a patched size field in the header that follows.
    /// </summary>
    /// <param name="type">The type flag of the block the data belongs to.</param>
    /// <param name="data">The data blocks' content, trimmed to the declared size.</param>
    /// <returns><see langword="true"/> when a <c>size</c> record is present.</returns>
    /// <remarks>
    /// Only produced for entries of 8 GiB or more, which the metadata document
    /// never is — but a header patched into disagreement with its own PAX record
    /// is a corrupt archive, so it is worth the few bytes to notice.
    /// </remarks>
    internal static bool DeclaresPaxSize(char type, ReadOnlySpan<byte> data)
    {
        if (type is not (TypePaxExtended or TypePaxExtendedUpper))
        {
            return false;
        }

        return NameEncoding.GetString(data.ToArray()).Contains(" size=", StringComparison.Ordinal);
    }

    /// <summary>
    /// Copies a header, changing only the content length and the checksum that
    /// covers it.
    /// </summary>
    /// <param name="header">The original block.</param>
    /// <param name="size">The new content length.</param>
    /// <returns>A new 512-byte block.</returns>
    /// <remarks>
    /// This is what keeps a save faithful. Mode, uid, gid, uname, gname, the type
    /// flag and the ustar prefix are carried through untouched — this build has no
    /// opinion about any of them, and a user who edits a comic's title has not
    /// asked for its permissions to change.
    /// <para>
    /// The terminator byte of the size field is preserved rather than normalised:
    /// producers disagree about whether it is a NUL or a space, both are legal, and
    /// keeping the original is what makes an unchanged entry byte-identical.
    /// </para>
    /// </remarks>
    internal static byte[] WithSize(ReadOnlySpan<byte> header, long size)
    {
        byte[] patched = header.ToArray();

        WriteOctal(patched.AsSpan(SizeOffset, SizeLength - 1), size);
        WriteChecksum(patched);

        return patched;
    }

    /// <summary>
    /// Builds a header for an entry that was not in the source archive.
    /// </summary>
    /// <param name="name">The entry name, with forward slashes.</param>
    /// <param name="size">The content length in bytes.</param>
    /// <param name="lastModified">
    /// The timestamp to record; <see langword="default"/> writes the epoch.
    /// </param>
    /// <returns>A 512-byte ustar header block.</returns>
    /// <exception cref="BookFormatException">
    /// The name does not fit the ustar name and prefix fields.
    /// </exception>
    /// <remarks>
    /// Deliberately deterministic — a fixed mode, no owner, and no timestamp of its
    /// own — so that building the same archive twice produces the same bytes, which
    /// the golden-file tests depend on.
    /// </remarks>
    internal static byte[] Synthesize(string name, long size, DateTimeOffset lastModified)
    {
        byte[] block = new byte[BlockSize];

        WriteName(block, name);

        WriteOctal(block.AsSpan(ModeOffset, 7), Convert.ToInt64("644", 8));
        WriteOctal(block.AsSpan(UidOffset, 7), 0);
        WriteOctal(block.AsSpan(GidOffset, 7), 0);
        WriteOctal(block.AsSpan(SizeOffset, SizeLength - 1), size);
        WriteOctal(
            block.AsSpan(ModifiedOffset, ModifiedLength - 1),
            lastModified == default ? 0 : Math.Max(0, lastModified.ToUnixTimeSeconds()));

        block[TypeFlagOffset] = (byte)TypeRegular;

        // "ustar\0" then version "00" — the POSIX spelling. GNU writes "ustar  \0"
        // across the same eight bytes; either is read by everything, and this is
        // the one the standard specifies.
        NameEncoding.GetBytes("ustar", 0, 5, block, MagicOffset);
        block[MagicOffset + 6] = (byte)'0';
        block[MagicOffset + 7] = (byte)'0';

        WriteChecksum(block);

        return block;
    }

    /// <summary>
    /// Writes a name across the ustar name and prefix fields.
    /// </summary>
    private static void WriteName(byte[] block, string name)
    {
        byte[] bytes = NameEncoding.GetBytes(name);

        if (bytes.Length <= NameLength)
        {
            Array.Copy(bytes, 0, block, NameOffset, bytes.Length);
            return;
        }

        // ustar splits a long name at a slash: everything before goes in the
        // prefix, everything after in the name.
        int split = -1;
        for (int i = Math.Min(bytes.Length - 1, PrefixLength); i > 0; i--)
        {
            if (bytes[i] == (byte)'/' && bytes.Length - i - 1 <= NameLength)
            {
                split = i;
                break;
            }
        }

        if (split < 0)
        {
            // A GNU long-name block would be the way to express this, and writing
            // one is a capability nothing in this build needs: the only synthesized
            // entry is ComicInfo.xml at the archive root.
            throw new BookFormatException(
                $"Entry name '{name}' is too long for a TAR header and cannot be written.",
                name);
        }

        Array.Copy(bytes, split + 1, block, NameOffset, bytes.Length - split - 1);
        Array.Copy(bytes, 0, block, PrefixOffset, split);
    }

    /// <summary>
    /// Computes and stores the block's checksum, which must be done last.
    /// </summary>
    private static void WriteChecksum(byte[] block)
    {
        int sum = 0;

        for (int i = 0; i < BlockSize; i++)
        {
            sum += i >= ChecksumOffset && i < ChecksumOffset + ChecksumLength ? ' ' : block[i];
        }

        // Six octal digits, a NUL, then a space. Other spellings exist and are
        // read, but this is the one POSIX describes and every tool accepts.
        WriteOctal(block.AsSpan(ChecksumOffset, 6), sum);
        block[ChecksumOffset + 6] = 0;
        block[ChecksumOffset + 7] = (byte)' ';
    }

    /// <summary>
    /// Reads an octal ASCII field, tolerating the padding producers disagree
    /// about.
    /// </summary>
    /// <returns>The value, or -1 when the field cannot be read.</returns>
    private static long ReadOctal(ReadOnlySpan<byte> field)
    {
        // GNU escapes a value too large for the field by setting the high bit of
        // the first byte and storing it as base 256, big-endian. Only reachable
        // for sizes of 8 GiB or more, but reading it costs four lines.
        if (field.Length > 0 && (field[0] & 0x80) != 0)
        {
            long binary = field[0] & 0x3F;

            for (int i = 1; i < field.Length; i++)
            {
                binary = (binary << 8) | field[i];
            }

            return (field[0] & 0x40) != 0 ? -1 : binary;
        }

        long value = 0;
        bool any = false;

        foreach (byte character in field)
        {
            if (character is 0 or (byte)' ')
            {
                // Leading padding is skipped; trailing padding ends the field.
                if (any)
                {
                    break;
                }

                continue;
            }

            if (character < '0' || character > '7')
            {
                return -1;
            }

            value = (value << 3) | (long)(character - '0');
            any = true;
        }

        return any ? value : 0;
    }

    /// <summary>
    /// Writes a right-aligned, zero-padded octal value filling the whole span.
    /// </summary>
    private static void WriteOctal(Span<byte> field, long value)
    {
        for (int i = field.Length - 1; i >= 0; i--)
        {
            field[i] = (byte)('0' + (int)(value & 7));
            value >>= 3;
        }
    }

    /// <summary>Reads a NUL-terminated ASCII field.</summary>
    private static string ReadString(ReadOnlySpan<byte> field)
    {
        int length = field.IndexOf((byte)0);
        if (length < 0)
        {
            length = field.Length;
        }

        return length == 0 ? string.Empty : NameEncoding.GetString(field.Slice(0, length).ToArray());
    }

    private static bool HasUstarMagic(ReadOnlySpan<byte> block) =>
        block[MagicOffset] == 'u' &&
        block[MagicOffset + 1] == 's' &&
        block[MagicOffset + 2] == 't' &&
        block[MagicOffset + 3] == 'a' &&
        block[MagicOffset + 4] == 'r';
}
