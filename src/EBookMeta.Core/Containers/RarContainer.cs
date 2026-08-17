using Microsoft.Win32;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;
using System.Diagnostics;
using System.Text;

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

    private const string ExecutableName = "Rar.exe";

    private const string AppPathsKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe";

    /// <summary>
    /// Where the archiver is looked for. Only tests replace it — no build machine has
    /// WinRAR, so they state what is installed instead of asking.
    /// </summary>
    internal static Func<string?> Locator { get; set; } = RarLocation;

    /// <inheritdoc />
    public bool IsWritable => Locator() is not null;

    /// <inheritdoc />
    public IReadOnlyList<ContainerEntry> Entries => _entries;

    /// <summary>The file this container was opened from, when it came from one.</summary>
    public string? Path { get; }

    /// <inheritdoc />
    public string? ArchiveComment => null;

    /// <summary>
    /// WinRAR's <c>Rar.exe</c>, or <see langword="null"/> if this machine has none.
    /// </summary>
    internal static string? RarLocation() =>
        ArchiverIn(WinRarDirectory()) ??
        Environment.GetEnvironmentVariable("PATH")?.Split(';')
            .Select(ArchiverIn).FirstOrDefault(found => found is not null);

    /// <summary>WinRAR's install directory as <c>App Paths</c> records it.</summary>
    private static string? WinRarDirectory()
    {
        // Both bitness views and both hives: a 32-bit WinRAR on 64-bit Windows
        // registers under Wow6432Node, and a per-user install under HKCU.
        foreach (RegistryView view in
            new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default })
        {
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? key = root.OpenSubKey(AppPathsKey);

                    // "Path" is the install directory; the default value is
                    // WinRAR.exe's own path, which names the same one.
                    if (key?.GetValue("Path") as string is { Length: > 0 } directory)
                    {
                        return directory;
                    }

                    if (key?.GetValue(null) as string is { Length: > 0 } executable)
                    {
                        return System.IO.Path.GetDirectoryName(executable.Trim().Trim('"'));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or ArgumentException)
                {
                    Log.Debug($"Could not read {hive}\\{AppPathsKey}: {ex.Message}");
                }
            }
        }

        return null;
    }

    /// <summary>The archiver inside a directory, when it is there.</summary>
    internal static string? ArchiverIn(string? directory)
    {
        string trimmed = (directory ?? string.Empty).Trim().Trim('"');

        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            string candidate = System.IO.Path.Combine(trimmed, ExecutableName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (ArgumentException)
        {
            // PATH collects entries with characters no path may contain.
            return null;
        }
    }

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

        Create(entries, targetPath, tool, _allStored);
    }

    /// <summary>
    /// Writes a RAR containing the given entries by handing them to an external
    /// archiver.
    /// </summary>
    /// <exception cref="BookFormatException">An unsafe entry name.</exception>
    /// <exception cref="BookIoException">The archiver did not produce the file.</exception>
    public static void Create(
        IEnumerable<PendingEntry> entries, string targetPath, string tool, bool stored = false)
    {
        Throw.IfNull(entries);
        Throw.IfNullOrEmpty(targetPath);
        Throw.IfNullOrEmpty(tool);

        string staging = targetPath + StagingSuffix;

        try
        {
            Delete(staging);
            Directory.CreateDirectory(staging);

            // The archiver adds to an existing archive rather than replacing it, so a
            // leftover from an earlier attempt would silently merge into this one.
            File.Delete(targetPath);

            List<string> names = Stage(entries, staging);
            Run(tool, staging, System.IO.Path.GetFullPath(targetPath), names, stored);
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            // One answer for every cause, on purpose — see IsWriteFailure. The
            // particulars go here because the message withholds them.
            Log.Debug($"Could not write '{targetPath}': {ex.GetType().Name}: {ex.Message}");

            throw new BookIoException($"Could not write '{targetPath}'.", ex);
        }
        finally
        {
            Delete(staging);
        }
    }

    private const string StagingSuffix = ".stage";

    private const string ListFileName = "__entries.lst";

    /// <summary>So a wedged archiver cannot hang a save forever. Not a budget.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Writes every entry into the staging directory and returns their relative names,
    /// in order.
    /// </summary>
    internal static List<string> Stage(IEnumerable<PendingEntry> entries, string staging)
    {
        string root = System.IO.Path.GetFullPath(staging);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PendingEntry pending in entries)
        {
            if (ContainerEntry.EscapesArchive(pending.Name))
            {
                throw new BookFormatException(
                    $"Entry '{pending.Name}' is absolute or escapes the archive, so it "
                    + "cannot be written. Nothing was changed.");
            }

            string relative = pending.Name.Replace('/', System.IO.Path.DirectorySeparatorChar);
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));

            // The predicate reads the name; this checks where it actually landed.
            if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new BookFormatException(
                    $"Entry '{pending.Name}' does not stay inside the archive.");
            }

            // RAR records a folder marker with no trailing separator, so IsDirectory is
            // all that tells it from a page. Written as a file it would fail on the
            // directory its own pages created. Not listed: the archiver recreates the
            // folder from the paths inside it.
            if (pending.Source?.IsDirectory == true || pending.Name.EndsWith('/'))
            {
                Directory.CreateDirectory(full);
                continue;
            }

            // Archives may legally repeat a name. On disk the second would overwrite
            // the first and a page would vanish.
            if (!seen.Add(relative))
            {
                throw new BookFormatException(
                    $"Entry '{pending.Name}' appears more than once, which cannot be "
                    + "reproduced. Nothing was changed.");
            }

            string? directory = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (Stream content = pending.OpenContent())
            using (var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                content.CopyTo(file);
            }

            names.Add(relative);
        }

        return names;
    }

    /// <summary>Runs the archiver over the staged files and waits for it to finish.</summary>
    private static void Run(
        string tool, string workingDirectory, string targetPath, List<string> names, bool stored)
    {
        // A list file, not a command line: three hundred pages is more than one holds.
        File.WriteAllLines(
            System.IO.Path.Combine(workingDirectory, ListFileName),
            names,
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        var info = new ProcessStartInfo
        {
            FileName = tool,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.Arguments = BuildArguments(targetPath, stored);

        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };

        // Drained asynchronously: a child that fills a pipe nobody reads blocks
        // forever, and this one is being waited on.
        process.OutputDataReceived += (_, e) => Capture(output, e.Data);
        process.ErrorDataReceived += (_, e) => Capture(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            Stop(process);
            throw new TimeoutException($"'{tool}' did not finish within {Timeout.TotalMinutes} minutes.");
        }

        // The parameterless overload after the timed one, so the handlers above are
        // known to have run before their buffer is read.
        process.WaitForExit();

        int exitCode = process.ExitCode;
        string text = output.ToString().Trim();

        if (exitCode != 0)
        {
            Log.Debug($"'{tool}' exited with {exitCode}. {text}");

            throw new IOException(
                $"'{tool}' exited with code {exitCode}.");
        }

        if (text.Length > 0)
        {
            Log.Debug($"'{tool}': {text}");
        }

        // It can report success and produce nothing, and AtomicFileWriter's own check
        // would then blame the wrong thing.
        if (!File.Exists(targetPath))
        {
            throw new IOException($"'{tool}' reported success but produced no archive.");
        }

        Log.Info($"Wrote {names.Count} entries to '{targetPath}' using '{tool}'.");
    }

    /// <summary>The command line handed to the archiver.</summary>
    internal static string BuildArguments(string targetPath, bool stored) => string.Join(
        " ",
        "a",
        "-y",
        "-idq",
        stored ? "-m0" : "-m3",
        "-scul",
        "--",
        Quote(targetPath),
        "@" + Quote(ListFileName));

    private static void Capture(StringBuilder output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (output)
        {
            // Bounded: a chatty archiver must not turn a failed save into a megabyte
            // of log.
            if (output.Length < 4096)
            {
                output.Append(line).Append(' ');
            }
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // It exited already, or cannot be killed. The save has failed either way.
        }
    }

    /// <summary>Quotes an argument, which on Windows means wrapping it.</summary>
    private static string Quote(string value) => $"\"{value}\"";

    private static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: throwing from cleanup would mask the real failure.
        }
    }

    /// <summary>Whether an exception means "these bytes did not read", not a bug.</summary>
    private static bool IsUnreadable(Exception ex) =>
        ex is SharpCompressException or IOException or InvalidDataException
            or IndexOutOfRangeException or ArgumentOutOfRangeException or NotSupportedException;

    /// <summary>Whether an exception means "the save did not happen", not a bug.</summary>
    private static bool IsWriteFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException
            or TimeoutException or InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception;

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
