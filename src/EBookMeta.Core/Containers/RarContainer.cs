using Microsoft.Win32;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;
using System.Diagnostics;
using System.Text;

namespace EBookMeta.Containers;

/// <summary>
/// A RAR container — the storage behind CBR. Reads unaided; rebuilds only through an
/// archiver already on the machine.
/// </summary>
/// <remarks>
/// The one container that cannot write itself. RAR compression is proprietary:
/// SharpCompress decompresses RAR 4 and RAR 5 and writes neither, the UnRAR source
/// licence forbids using it to build a compatible compressor, and no free one exists
/// to depend on. So <see cref="Rebuild"/> hands the entries to whatever
/// <see cref="RarLocation"/> finds, and reports <c>CBR-F002</c> when that is nothing.
/// <para>
/// Nothing above this class knows the difference: <c>CbzFormat</c> reads
/// <c>ComicInfo.xml</c> through <see cref="IContainer"/> whatever the container is,
/// and a save runs the whole ordinary path — <c>Book.Save</c>,
/// <c>AtomicFileWriter</c>, <c>CbzFormat.Write</c> — to either reach an archiver or
/// fail at the last step, with the user's file untouched because only the temporary
/// path was ever written.
/// </para>
/// </remarks>
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

    /// <summary>The console archiver a WinRAR installation carries.</summary>
    private const string ExecutableName = "Rar.exe";

    /// <summary>Where Windows records the path of a program by its file name.</summary>
    private const string AppPathsKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe";

    /// <summary>
    /// Where the archiver is looked for. Only tests replace it: no build machine has
    /// WinRAR, so they have to state what is installed instead of asking.
    /// </summary>
    internal static Func<string?> Locator { get; set; } = RarLocation;

    /// <inheritdoc />
    /// <remarks>
    /// True when the machine has an archiver. Whether it works is not a question asked
    /// here — see <see cref="Rebuild"/>.
    /// </remarks>
    public bool IsWritable => Locator() is not null;

    /// <inheritdoc />
    public IReadOnlyList<ContainerEntry> Entries => _entries;

    /// <summary>The file this container was opened from, when it came from one.</summary>
    public string? Path { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Always <see langword="null"/>. RAR does carry an archive comment, but
    /// SharpCompress does not expose one, so this cannot report what it cannot see —
    /// which means a rebuild drops it without the CBZ-W012 warning a ZIP would get.
    /// Known limitation, and the one thing an in-place update would fix for free,
    /// since the archiver would never touch the comment.
    /// </remarks>
    public string? ArchiveComment => null;

    /// <summary>
    /// WinRAR's <c>Rar.exe</c>, or <see langword="null"/> if this machine has none.
    /// </summary>
    /// <remarks>
    /// Two places, in order: WinRAR's registered install directory, then the search
    /// path for a <c>Rar.exe</c> unpacked by hand. No hard-coded directories and no
    /// version check.
    /// <para>
    /// <c>Rar.exe</c> and never <c>WinRAR.exe</c>: <see cref="BuildArguments"/> uses
    /// console switches, and the windowed build reads a different command line and puts
    /// a progress window on screen mid-save.
    /// </para>
    /// </remarks>
    internal static string? RarLocation() =>
        ArchiverIn(WinRarDirectory()) ??
        Environment.GetEnvironmentVariable("PATH")?.Split(';')
            .Select(ArchiverIn).FirstOrDefault(found => found is not null);

    /// <summary>
    /// WinRAR's install directory as <c>App Paths</c> records it.
    /// </summary>
    /// <remarks>
    /// <c>App Paths</c> is where an installer records a program's full path under its
    /// bare file name. Both bitness views of <c>HKLM</c> and then <c>HKCU</c>, because a
    /// 32-bit WinRAR on 64-bit Windows registers under <c>Wow6432Node</c> and whether a
    /// CBR saves must not depend on how this build was compiled.
    /// </remarks>
    private static string? WinRarDirectory()
    {
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
                    // WinRAR.exe's own path, which names the same directory.
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
                    // A denied or malformed key is one more place that did not have it.
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
    /// <param name="path">The archive to open.</param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookIoException">The file could not be opened.</exception>
    /// <exception cref="BookFormatException">
    /// The file is not a readable RAR, or is solid or encrypted (CBR-F001).
    /// </exception>
    public static RarContainer Open(string path)
    {
        Throw.IfNullOrEmpty(path);

        return BookContainers.OpenFile(path, stream => Open(stream, path, leaveOpen: false));
    }

    /// <summary>Opens a RAR container over an existing seekable stream.</summary>
    /// <param name="stream">A readable, seekable stream over the archive.</param>
    /// <param name="path">The originating path, for diagnostics. May be null.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave <paramref name="stream"/> open when the
    /// container is disposed.
    /// </param>
    /// <returns>An open container; the caller disposes it.</returns>
    /// <exception cref="BookFormatException">
    /// The stream is not a readable RAR, or is solid or encrypted (CBR-F001).
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
            throw new BookFormatException($"'{path}' is not a readable RAR archive.", path, ex);
        }

        try
        {
            if (archive.IsSolid)
            {
                const string Reason =
                    "This RAR archive is solid, which stores every file in one compression "
                    + "stream. Entries cannot be read individually, so its metadata cannot "
                    + "be read either.";

                Log.Rule(LogLevel.Error, "CBR-F001", Reason, path);
                throw new BookFormatException(Reason, path);
            }

            if (archive.IsEncrypted)
            {
                const string Reason =
                    "This RAR archive is encrypted and this build has no way to ask for a "
                    + "password.";

                Log.Rule(LogLevel.Error, "CBR-F001", Reason, path);
                throw new BookFormatException(Reason, path);
            }

            // Materialised once and kept, so an entry's Index addresses the same
            // archive entry for the container's lifetime. RAR names are no more
            // unique than ZIP's, which is why nothing here looks an entry up by name.
            RarArchiveEntry[] source = [.. archive.Entries];
            var entries = new ContainerEntry[source.Length];
            bool allStored = true;

            for (int i = 0; i < source.Length; i++)
            {
                RarArchiveEntry entry = source[i];

                allStored &= entry.IsDirectory || entry.CompressionType == CompressionType.None;

                entries[i] = new ContainerEntry
                {
                    // RAR records backslashes when the archive was made on Windows,
                    // which for a comic is nearly always. ContainerEntry promises
                    // forward slashes, and CbzFormat decides whether the metadata
                    // document is nested by looking for one — "sub\ComicInfo.xml"
                    // left alone would read as a root entry and CBZ-E011 never fire.
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
                $"'{path}' could not be read as a RAR archive.", path, ex);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One entry at a time. SharpCompress decompresses through the single stream the
    /// archive was opened over, so two open entry streams would read each other's
    /// bytes — the same restriction the other single-handle containers carry.
    /// </remarks>
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
                $"Entry '{entry.Name}' could not be decompressed.", entry.Name, ex);
        }
    }

    /// <inheritdoc />
    /// <exception cref="BookFormatException">
    /// The machine has no archiver (CBR-F002), or an entry name cannot be written
    /// safely.
    /// </exception>
    /// <exception cref="BookIoException">
    /// The archiver did not produce the file. One message for every way that can
    /// happen — see the remarks.
    /// </exception>
    /// <remarks>
    /// Refused rather than quietly written as something else. Producing a ZIP at
    /// <paramref name="targetPath"/> would leave a <c>.cbr</c> that is not a RAR,
    /// which is the disguised-archive problem this tool exists to report.
    /// </remarks>
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
                + "found on this computer. The file was not changed.";

            Log.Rule(LogLevel.Error, "CBR-F002", Reason, Path);
            throw new BookFormatException(Reason, Path);
        }

        Log.Debug($"Using the RAR archiver at '{tool}'.");

        Create(entries, targetPath, tool, _allStored);
    }

    /// <summary>
    /// Writes a RAR containing the given entries by handing them to an external
    /// archiver.
    /// </summary>
    /// <param name="entries">The entries to write, in order.</param>
    /// <param name="targetPath">The file to create.</param>
    /// <param name="tool">The archiver to run.</param>
    /// <param name="stored">
    /// Whether to ask for no compression, which is what the source archive did.
    /// </param>
    /// <exception cref="BookFormatException">An entry name cannot be written safely.</exception>
    /// <exception cref="BookIoException">The archiver did not produce the file.</exception>
    /// <remarks>
    /// A full rebuild: every entry is staged to a directory beside the target and the
    /// whole set archived in one go. Copying the source and updating the one changed
    /// entry would be cheaper, but the pending list can also add, move and drop
    /// entries, and getting that diff subtly wrong is how an archive loses a page.
    /// <para>
    /// Staging is a sibling of the target for the same reason
    /// <c>AtomicFileWriter</c>'s temporary file is: a comic is hundreds of megabytes
    /// and <c>%TEMP%</c> is often on another volume.
    /// </para>
    /// </remarks>
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

            // The archiver adds to an existing archive rather than replacing it, so
            // a leftover from an earlier attempt would silently merge into this one.
            File.Delete(targetPath);

            List<string> names = Stage(entries, staging);
            Run(tool, staging, System.IO.Path.GetFullPath(targetPath), names, stored);
        }
        catch (BookFormatException)
        {
            // Ours, and already specific about what is wrong with the entry.
            throw;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            // Every other way this can go wrong collapses to one answer, on purpose —
            // see IsWriteFailure. The particulars are logged here because the message
            // deliberately withholds them: without this the log records that a save
            // failed and nothing whatever about why, which is not what "the
            // particulars go to Log.Debug" is supposed to mean.
            Log.Debug($"Could not write '{targetPath}': {ex.GetType().Name}: {ex.Message}");

            throw new BookIoException($"Could not write '{targetPath}'.", targetPath, ex);
        }
        finally
        {
            Delete(staging);
        }
    }

    /// <summary>The staging directory's suffix, appended to the target path.</summary>
    private const string StagingSuffix = ".stage";

    /// <summary>The list of names handed to the archiver, inside the staging directory.</summary>
    private const string ListFileName = "__entries.lst";

    /// <summary>
    /// How long the archiver is given before it is assumed to have hung. Generous
    /// rather than tuned — it exists so a wedged child cannot hang a save forever,
    /// not to enforce a budget.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Writes every entry into the staging directory and returns their relative
    /// names, in order.
    /// </summary>
    /// <remarks>
    /// The only place in Core that puts a container's entries on disk, which makes
    /// hard invariant 4 enforceable rather than report-only. An entry named
    /// <c>..\..\autoexec.bat</c> is refused here, where <c>Book.Load</c> only logs
    /// <c>GEN-E003</c> and reads on — reading resolves nothing against the file system.
    /// </remarks>
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
                    + "cannot be written. Nothing was changed.",
                    pending.Name);
            }

            string relative = pending.Name.Replace('/', System.IO.Path.DirectorySeparatorChar);
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));

            // The predicate above reads the name; this checks where it actually
            // landed. Belt and braces: the cost of being wrong is a file written
            // outside the folder we are allowed to touch.
            if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new BookFormatException(
                    $"Entry '{pending.Name}' does not stay inside the archive.", pending.Name);
            }

            // A directory marker is not a file, and opening one as a file is how a
            // save fails on an ordinary comic: a CBR packed from an extracted folder
            // carries a marker for that folder, and RAR — unlike ZIP — records it
            // with no trailing separator, so IsDirectory is the only thing telling it
            // apart from a page. The FileStream below would be pointed at a path that
            // is already a directory and refuse.
            //
            // Nothing is listed for it. The archiver recreates the folder from the
            // paths of the entries inside it, so the only marker lost is one for a
            // directory with nothing in it — which no comic depends on, and CBR has no
            // byte-identity guarantee for this to break.
            if (pending.Source?.IsDirectory == true || pending.Name.EndsWith('/'))
            {
                Directory.CreateDirectory(full);
                Log.Debug($"Staged '{pending.Name}' as a directory; the archiver is not asked for it.");
                continue;
            }

            // Archives may legally repeat a name and malformed ones in the wild do.
            // On disk the second would overwrite the first and a page would vanish.
            if (!seen.Add(relative))
            {
                throw new BookFormatException(
                    $"Entry '{pending.Name}' appears more than once, which cannot be "
                    + "reproduced. Nothing was changed.",
                    pending.Name);
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

    /// <summary>
    /// Runs the archiver over the staged files and waits for it to finish.
    /// </summary>
    /// <remarks>
    /// Names go in a list file rather than on the command line, because three hundred
    /// pages is more than a command line holds. It is written UTF-16 and declared as
    /// such, so a page named in any script survives the hand-off.
    /// <para>
    /// Both output streams are redirected and drained asynchronously: a child that
    /// fills a pipe nobody is reading blocks forever, and this one is being waited on.
    /// </para>
    /// </remarks>
    private static void Run(
        string tool, string workingDirectory, string targetPath, List<string> names, bool stored)
    {
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

        // The parameterless overload after the timed one, so the output handlers
        // above are known to have run before their buffer is read.
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

        // It can report success and still not have produced anything, and
        // AtomicFileWriter's own check would then blame the wrong thing.
        if (!File.Exists(targetPath))
        {
            throw new IOException($"'{tool}' reported success but produced no archive.");
        }

        Log.Info($"Wrote {names.Count} entries to '{targetPath}' using '{tool}'.");
    }

    /// <summary>
    /// The command line handed to the archiver.
    /// </summary>
    /// <param name="targetPath">The archive to create, as a full path.</param>
    /// <param name="stored">Whether to ask for no compression.</param>
    /// <returns>The argument string.</returns>
    /// <remarks>
    /// Separated from running it so the switches can be asserted without an archiver
    /// installed, which no build machine has and none should need.
    /// <list type="bullet">
    /// <item><c>a</c> — add to an archive.</item>
    /// <item><c>-y</c> — assume yes; nothing is watching for a prompt.</item>
    /// <item><c>-idq</c> — quiet.</item>
    /// <item><c>-m0</c> / <c>-m3</c> — no compression when the source had none,
    /// otherwise the default. Comic pages are already-compressed images either way,
    /// so this is about not inflating a stored archive, not about saving space.</item>
    /// <item><c>-scul</c> — the list file is UTF-16.</item>
    /// <item><c>--</c> — everything after this is a path, so a file named like a
    /// switch cannot become one.</item>
    /// </list>
    /// </remarks>
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
            // Bounded: a chatty archiver must not turn a failed save into a
            // megabyte of log.
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
            // It exited between the wait timing out and this, or cannot be killed.
            // Either way the save has already failed and this changes nothing.
        }
    }

    /// <summary>Quotes an argument, which on Windows means wrapping it.</summary>
    /// <remarks>
    /// No escaping of inner quotes: the file system rejects a path containing one long
    /// before it reaches here, and an escape scheme for a case that cannot occur is
    /// how one gets it wrong.
    /// </remarks>
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
            // Best effort, like AtomicFileWriter's. A leftover staging folder is
            // untidy; throwing from cleanup would mask the real failure.
        }
    }

    /// <summary>
    /// Whether an exception means "these bytes did not read", as opposed to a bug.
    /// </summary>
    /// <remarks>
    /// Every SharpCompress failure derives from <see cref="SharpCompressException"/>,
    /// the cryptographic and invalid-format ones included, so the family is translated
    /// in one place. Core's callers see <see cref="BookFormatException"/> and never a
    /// dependency's type.
    /// </remarks>
    private static bool IsUnreadable(Exception ex) =>
        ex is SharpCompressException or IOException or InvalidDataException
            or IndexOutOfRangeException or ArgumentOutOfRangeException or NotSupportedException;

    /// <summary>
    /// Whether an exception means "the save did not happen", as opposed to a bug.
    /// </summary>
    /// <remarks>
    /// Deliberately one bucket. Running an external program fails in a dozen ways —
    /// not there, not executable, malformed path, will not start, hangs, refuses the
    /// arguments, disk full, folder denied — and the user can do exactly one thing
    /// about any of them, which is check the path they configured. So no ladder of
    /// checks and no message per cause; <c>Log.Debug</c> has the particulars.
    /// <para>
    /// One bucket, not a blanket. <c>Win32Exception</c> is how a process that will not
    /// start arrives, and is named for that reason; its base
    /// <see cref="SystemException"/> is not, because that is the base of almost
    /// everything and would turn a null-reference bug here into a polite "could not
    /// write" instead of a crash worth fixing.
    /// </para>
    /// </remarks>
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
