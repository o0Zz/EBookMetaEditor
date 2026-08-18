using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace EBookMeta.Containers;

/// <summary>
/// A RAR container — the storage behind CBR. Reads unaided; rebuilds only through an
/// archiver already on the machine, because RAR compression is proprietary and no
/// free compressor exists to depend on.
/// </summary>
public sealed class RarContainer : IContainer
{
    private readonly RarArchive _archive;
    private readonly RarArchiveEntry[] _source;
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly ContainerEntry[] _entries;
    private readonly bool _allStored;
    private bool _disposed;

    private RarContainer(
        RarArchive archive,
        RarArchiveEntry[] source,
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
    /// WinRAR's console archiver. Never <c>WinRAR.exe</c>: this command line is
    /// console switches, and the windowed build puts a progress window on screen
    /// mid-save. An install missing <c>Rar.exe</c> counts as no archiver.
    /// </summary>
    internal static ExternalArchiver Archiver { get; } = new(
        "Rar.exe",
        [@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe"],

        // Everything after -- is a path, so a page named like a switch cannot become
        // one; WinRAR still reads the list file there. -scul says that file is UTF-16.
        (target, list, stored) => string.Join(
            " ", "a", "-y", "-idq", stored ? "-m0" : "-m3", "-scul", "--", target, "@" + list));

    /// <summary>
    /// Where the archiver is looked for. Only tests replace it — no build machine has
    /// WinRAR, so they state what is installed instead of asking.
    /// </summary>
    internal static Func<string?> Locator { get; set; } = Archiver.Search;

    /// <summary>How <see cref="BookContainers"/> knows this container.</summary>
    public static ContainerFormat Format { get; } = new()
    {
        Kind = ContainerKind.Rar,
        Open = Open,
        Signatures =
        [
            ContainerSignature.Text("Rar!\u001A\u0007\u0001\u0000", "RAR 5 archive"),
            ContainerSignature.Text("Rar!\u001A\u0007\u0000", "RAR 4 archive"),
        ],
    };

    /// <inheritdoc />
    public bool IsWritable => Locator() is not null;

    /// <inheritdoc />
    public IReadOnlyList<ContainerEntry> Entries => _entries;

    /// <summary>The file this container was opened from, when it came from one.</summary>
    public string? Path { get; }

    /// <inheritdoc />
    public string? ArchiveComment => null;

    /// <summary>Opens a RAR container from a file path.</summary>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    /// <exception cref="BookFormatException">
    /// Not a readable RAR, or solid or encrypted (CBR-F001).
    /// </exception>
    public static RarContainer Open(string path)
    {
        Throw.IfNullOrEmpty(path);

        return BookContainers.OpenFile(path, stream => Open(stream, path, leaveOpen: false));
    }

    /// <summary>Opens a RAR container over an existing seekable stream.</summary>
    /// <exception cref="BookFormatException">
    /// Not a readable RAR, or solid or encrypted (CBR-F001).
    /// </exception>
    public static RarContainer Open(Stream stream, string? path = null, bool leaveOpen = false)
    {
        Throw.IfNull(stream);

        RarArchive archive;

        try
        {
            archive = RarArchive.Open(stream, new ReaderOptions { LeaveStreamOpen = true });
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            throw new BookFormatException($"'{path}' is not a readable RAR archive.", ex);
        }

        try
        {
            // Both refused at open rather than at OpenRead: the entry list of an
            // archive whose ComicInfo.xml cannot be read is not a book.
            if (archive.IsSolid)
            {
                const string Reason =
                    "This RAR archive is solid, which stores every file in one compression "
                    + "stream. Entries cannot be read individually, so its metadata cannot "
                    + "be read either.";

                Log.Rule(LogLevel.Error, "CBR-F001", Reason, path);
                throw new BookFormatException(Reason);
            }

            if (archive.IsEncrypted)
            {
                const string Reason =
                    "This RAR archive is encrypted and this build has no way to ask for a "
                    + "password.";

                Log.Rule(LogLevel.Error, "CBR-F001", Reason, path);
                throw new BookFormatException(Reason);
            }

            // Materialised once so an entry's Index keeps addressing the same archive
            // entry. RAR names are no more unique than ZIP's, so nothing looks an
            // entry up by name.
            RarArchiveEntry[] source = [.. archive.Entries];
            var entries = new ContainerEntry[source.Length];
            bool allStored = true;

            for (int i = 0; i < source.Length; i++)
            {
                RarArchiveEntry entry = source[i];

                allStored &= entry.IsDirectory || entry.CompressionType == CompressionType.None;

                entries[i] = new ContainerEntry
                {
                    // RAR records Windows backslashes. CbzFormat decides whether
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

            Log.Debug($"Opened RAR archive '{path}' with {entries.Length} entries.");

            return new RarContainer(
                archive, source, stream, ownsStream: !leaveOpen, entries, allStored, path);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            archive.Dispose();
            throw new BookFormatException(
                $"'{path}' could not be read as a RAR archive.", ex);
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
    /// No archiver on this machine (CBR-F002), or an unsafe entry name.
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
                "CBR files cannot be saved: no program that can create RAR archives was "
                + "found on this computer. The file was not changed. (You need to install https://www.win-rar.com/)";

            Log.Rule(LogLevel.Error, "CBR-F002", Reason, Path);
            throw new BookFormatException(Reason);
        }

        Log.Debug($"Using the RAR archiver at '{tool}'.");

        Archiver.Create(entries, targetPath, tool, _allStored);
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
