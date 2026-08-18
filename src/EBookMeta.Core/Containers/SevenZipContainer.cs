using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace EBookMeta.Containers;

/// <summary>
/// A 7z container — the storage behind CB7. Reads unaided; rebuilds only through an
/// archiver already on the machine, because SharpCompress reads 7z but cannot write
/// it and this build hand-rolls no compressor. <b>A rebuild does not preserve entry
/// order</b>: 7-Zip sorts what it is given, and nothing on its command line asks it
/// not to.
/// </summary>
public sealed class SevenZipContainer : IContainer
{
    private readonly SevenZipArchive _archive;
    private readonly SevenZipArchiveEntry[] _source;
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly ContainerEntry[] _entries;
    private readonly bool _allStored;
    private bool _disposed;

    private SevenZipContainer(
        SevenZipArchive archive,
        SevenZipArchiveEntry[] source,
        Stream stream,
        bool ownsStream,
        ContainerEntry[] entries,
        bool allStored,
        string? path)
    {
        _archive = archive;
        _source = source;
        _stream = stream;
        _ownsStream = ownsStream;
        _entries = entries;
        _allStored = allStored;
        Path = path;
    }

    /// <summary>
    /// 7-Zip's console program. Never <c>7zFM.exe</c> or <c>7zG.exe</c>: this command
    /// line is console switches, and either windowed build would put a progress
    /// window on screen mid-save.
    /// </summary>
    internal static ExternalArchiver Archiver { get; } = new(
        "7z.exe",
        [@"SOFTWARE\7-Zip", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\7zFM.exe"],

        // No -- separator, unlike RAR: 7-Zip's stops list-file parsing as well as
        // switch parsing, so @list after it is taken as the name of a file to add.
        // Nothing is lost — the only path on this command line is a .tmp sibling this
        // build composed, and the entry names arrive inside the list file, which is
        // never switch-parsed. -scsUTF-16LE says how that file is encoded and -bd
        // keeps a progress indicator out of the pipe this build drains.
        (target, list, stored) => string.Join(
            " ", "a", "-t7z", "-y", "-bd", stored ? "-mx0" : "-mx5", "-scsUTF-16LE",
            target, "@" + list));

    /// <summary>
    /// Where the archiver is looked for. Only tests replace it — whether a save is
    /// refused must not depend on what the machine running them has installed.
    /// </summary>
    internal static Func<string?> Locator { get; set; } = Archiver.Search;

    /// <inheritdoc />
    public bool IsWritable => Locator() is not null;

    /// <inheritdoc />
    public IReadOnlyList<ContainerEntry> Entries => _entries;

    /// <summary>The file this container was opened from, when it came from one.</summary>
    public string? Path { get; }

    /// <inheritdoc />
    public string? ArchiveComment => null;

    /// <summary>Opens a 7z container from a file path.</summary>
    /// <param name="path">The file to open.</param>
    /// <returns>The open container; the caller disposes it.</returns>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    /// <exception cref="BookFormatException">
    /// Not a readable 7z, or encrypted (CB7-F001).
    /// </exception>
    public static SevenZipContainer Open(string path)
    {
        Throw.IfNullOrEmpty(path);

        return BookContainers.OpenFile(path, stream => Open(stream, path, leaveOpen: false));
    }

    /// <summary>Opens a 7z container over an existing seekable stream.</summary>
    /// <param name="stream">The archive's bytes.</param>
    /// <param name="path">The file it came from, for diagnostics.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
    /// <returns>The open container; the caller disposes it.</returns>
    /// <exception cref="BookFormatException">
    /// Not a readable 7z, or encrypted (CB7-F001).
    /// </exception>
    public static SevenZipContainer Open(Stream stream, string? path = null, bool leaveOpen = false)
    {
        Throw.IfNull(stream);

        SevenZipArchive archive;

        try
        {
            archive = SevenZipArchive.Open(stream, new ReaderOptions { LeaveStreamOpen = true });
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            throw new BookFormatException($"'{path}' is not a readable 7z archive.", ex);
        }

        try
        {
            // A solid 7z is not refused the way a solid RAR is: 7-Zip packs one
            // compression block by default, so refusing would refuse nearly every
            // comic, and SharpCompress decodes the block to serve an entry from it.
            if (archive.IsEncrypted)
            {
                const string Reason =
                    "This 7z archive is encrypted and this build has no way to ask for a "
                    + "password.";

                Log.Rule(LogLevel.Error, "CB7-F001", Reason, path);
                throw new BookFormatException(Reason);
            }

            // Materialised once so an entry's Index keeps addressing the same archive
            // entry. 7z names are no more unique than ZIP's, so nothing looks an entry
            // up by name.
            SevenZipArchiveEntry[] source = [.. archive.Entries];
            var entries = new ContainerEntry[source.Length];
            bool allStored = true;

            for (int i = 0; i < source.Length; i++)
            {
                SevenZipArchiveEntry entry = source[i];

                allStored &= entry.IsDirectory || entry.CompressionType == CompressionType.None;

                entries[i] = new ContainerEntry
                {
                    // 7z records Windows backslashes. CbzFormat decides whether
                    // ComicInfo.xml is nested by looking for a slash, so
                    // "sub\ComicInfo.xml" left alone would read as a root entry and
                    // CBZ-E011 would never fire.
                    Name = (entry.Key ?? $"entry{i}").Replace('\\', '/'),
                    Index = i,
                    Length = entry.Size,
                    CompressionMethod = entry.CompressionType == CompressionType.None
                        ? ZipCompressionMethods.Stored
                        : ZipCompressionMethods.Deflate,
                    LastModified = entry.LastModifiedTime is { } modified
                        ? new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc))
                        : default,
                    IsDirectory = entry.IsDirectory,
                };
            }

            Log.Debug($"Opened 7z archive '{path}' with {entries.Length} entries.");

            return new SevenZipContainer(
                archive, source, stream, ownsStream: !leaveOpen, entries, allStored, path);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            archive.Dispose();
            throw new BookFormatException($"'{path}' could not be read as a 7z archive.", ex);
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
            throw new BookFormatException(
                $"Entry '{entry.Name}' could not be decompressed.", ex);
        }
    }

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// No archiver on this machine (CB7-F002), or an unsafe entry name.
    /// </exception>
    /// <exception cref="BookIoException">The archiver did not produce the file.</exception>
    public void Rebuild(IEnumerable<PendingEntry> entries, string targetPath)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);
        Throw.IfDisposed(_disposed, this);

        string? tool = Locator();

        if (tool is null)
        {
            const string Reason =
                "CB7 files cannot be saved: no program that can create 7z archives was "
                + "found on this computer. The file was not changed. (You need to install "
                + "https://www.7-zip.org/)";

            Log.Rule(LogLevel.Error, "CB7-F002", Reason, Path);
            throw new BookFormatException(Reason);
        }

        Log.Debug($"Using the 7z archiver at '{tool}'.");

        Archiver.Create(entries, targetPath, tool, _allStored);
    }

    /// <summary>
    /// Whether an exception means "these bytes did not read", not a bug. Wider than
    /// the RAR reader's list because the 7z one reports a malformed header by
    /// throwing plain BCL exceptions rather than its own.
    /// </summary>
    private static bool IsUnreadable(Exception ex) =>
        ex is SharpCompressException or IOException or InvalidDataException
            or IndexOutOfRangeException or ArgumentException or NotSupportedException
            or InvalidOperationException or OverflowException;

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
