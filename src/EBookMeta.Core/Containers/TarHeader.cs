using System.Text;

namespace EBookMeta.Containers;

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
