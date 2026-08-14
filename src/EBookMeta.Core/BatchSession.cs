using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;

namespace EBookMeta;

/// <summary>What happened to one file in a batch.</summary>
public enum BatchEntryStatus
{
    /// <summary>Not read yet.</summary>
    Pending = 0,

    /// <summary>Read, and editable.</summary>
    Loaded = 1,

    /// <summary>
    /// Recognised as a format this build cannot edit — commonly a <c>.cbz</c>
    /// that is really a RAR archive.
    /// </summary>
    Unsupported = 2,

    /// <summary>Could not be read or could not be written. <see cref="BatchEntry.Error"/> says why.</summary>
    Failed = 3,

    /// <summary>Edited and written successfully.</summary>
    Saved = 4,
}

/// <summary>One file in a batch, and everything known about it.</summary>
/// <remarks>
/// Holds no open handles. A container is opened to read the metadata and closed
/// again immediately, and reopened at save time: one container is one file handle
/// and one <c>FileStream</c>, and a folder of two thousand comics would exhaust
/// both.
/// </remarks>
public sealed class BatchEntry
{
    private readonly Dictionary<MetadataField, string> _original = [];

    internal BatchEntry(string path)
    {
        Path = path;
    }

    /// <summary>The file's full path.</summary>
    public string Path { get; }

    /// <summary>The file's name, for display.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>What has happened to this file so far.</summary>
    public BatchEntryStatus Status { get; internal set; }

    /// <summary>
    /// The open file, once it has been read; null while pending or after a failure.
    /// </summary>
    /// <remarks>
    /// The same <see cref="EBookMeta.Book"/> both windows use. A batch row is one
    /// book plus the baseline text that makes dirtiness meaningful, so the single
    /// file editor and a three-hundred-row grid load and save by exactly the same
    /// code — there is no second write path to keep correct.
    /// </remarks>
    public Book? Book { get; internal set; }

    /// <summary>What the content says the file is, once it has been looked at.</summary>
    public DetectedFormat? Detected { get; internal set; }

    /// <summary>What the format can store, or null before the file was looked at.</summary>
    public FormatCapabilities? Capabilities => Book?.Capabilities;

    /// <summary>The metadata, once read.</summary>
    /// <remarks>
    /// Mutable, and meant to be edited in place: an editor applies changes through
    /// <see cref="MetadataFields.Apply"/> and this object is what
    /// <see cref="BatchSession.Save"/> writes.
    /// </remarks>
    public BookMetadata? Metadata => Book?.Metadata;

    /// <summary>Why this file failed, in words suitable for showing a user.</summary>
    public string? Error { get; internal set; }

    /// <summary>Whether this file can be written at all.</summary>
    public bool IsWritable => Book?.CanSave == true;

    /// <summary>The fields whose text differs from what was read off the disk.</summary>
    public IEnumerable<MetadataField> ChangedFields
    {
        get
        {
            if (Metadata is not { } metadata)
            {
                yield break;
            }

            foreach (MetadataField candidate in MetadataFields.All)
            {
                if (_original.TryGetValue(candidate, out string? original) &&
                    !string.Equals(original, MetadataFields.Read(metadata, candidate), StringComparison.Ordinal))
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>Whether anything about this file's metadata has been edited.</summary>
    /// <remarks>
    /// Compared against the text read from the file rather than tracked by the
    /// editor, so typing a value and typing it back leaves the file alone. That is
    /// what keeps a batch save from rewriting files nobody actually changed.
    /// </remarks>
    public bool IsDirty => ChangedFields.Any();

    /// <summary>Returns the text an editor should show for a field.</summary>
    /// <param name="field">The field to read.</param>
    /// <returns>The text, empty when the file has no such value or was not read.</returns>
    public string Read(MetadataField field) =>
        Metadata is null ? string.Empty : MetadataFields.Read(Metadata, field);

    /// <summary>Applies edited text to a field.</summary>
    /// <param name="field">The field to write.</param>
    /// <param name="value">The text the user typed.</param>
    /// <returns><see langword="true"/> if the model changed.</returns>
    /// <remarks>
    /// Refuses silently when the format cannot store the field. An editor should
    /// have disabled the cell already; this is the backstop that keeps a bulk
    /// "apply to every selected row" from writing a sort title into a comic.
    /// </remarks>
    public bool Apply(MetadataField field, string value)
    {
        if (Metadata is not { } metadata || Capabilities?.CanWriteAll(field) != true)
        {
            return false;
        }

        return MetadataFields.Apply(metadata, field, value);
    }

    /// <summary>Records the current values as the baseline for dirtiness.</summary>
    internal void Snapshot()
    {
        _original.Clear();

        if (Metadata is not { } metadata)
        {
            return;
        }

        foreach (MetadataField field in MetadataFields.All)
        {
            _original[field] = MetadataFields.Read(metadata, field);
        }
    }

    /// <summary>Returns the path and status, for diagnostics.</summary>
    public override string ToString() => $"{FileName} ({Status})";
}

/// <summary>How far through a batch operation is.</summary>
/// <param name="Completed">Files finished.</param>
/// <param name="Total">Files in the operation.</param>
/// <param name="Path">The file just finished.</param>
public sealed record BatchProgress(int Completed, int Total, string Path);

/// <summary>What a batch save did.</summary>
/// <param name="Saved">Files written.</param>
/// <param name="Skipped">Files left alone because nothing had changed, or because the format cannot be written.</param>
/// <param name="Failed">Files that could not be written. Each one's <see cref="BatchEntry.Error"/> says why.</param>
public sealed record BatchSaveReport(int Saved, int Skipped, int Failed)
{
    /// <summary>A one-line summary suitable for a status bar.</summary>
    public override string ToString()
    {
        var parts = new List<string> { $"{Saved} saved" };

        if (Skipped > 0)
        {
            parts.Add($"{Skipped} unchanged");
        }

        if (Failed > 0)
        {
            parts.Add($"{Failed} failed");
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Many files, read together, edited together and saved together.
/// </summary>
/// <remarks>
/// <para>
/// The batch equivalent of what the single-file editor does, and deliberately the
/// same machinery underneath: one <c>AtomicFileWriter.Write</c> per file, with the
/// container reopened inside the callback. There is no batch write path, because a
/// second way to replace a user's file is the last thing this codebase needs.
/// </para>
/// <para>
/// There is also deliberately no transaction across files. Twenty files are twenty
/// independent saves: one that fails leaves its own file untouched and does not stop
/// the other nineteen, and the report says which was which. Rolling back nineteen
/// successful writes because the twentieth was read-only would be worse behaviour,
/// not better.
/// </para>
/// <para>
/// This is the one operation in the product that is not held to the 400 ms startup
/// budget: that budget is about right-clicking a single file. A folder of five
/// hundred books cannot be read in 400 ms and should not pretend to be, so
/// <see cref="Load"/> reports progress and can be cancelled.
/// </para>
/// </remarks>
public sealed class BatchSession
{
    private readonly List<BatchEntry> _entries;

    private BatchSession(List<BatchEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>The files in this batch, in order.</summary>
    public IReadOnlyList<BatchEntry> Entries => _entries;

    /// <summary>How many files have unsaved edits.</summary>
    public int DirtyCount => _entries.Count(e => e.IsDirty);

    /// <summary>Creates a session over the given paths.</summary>
    /// <param name="paths">The files to edit. Duplicates are dropped.</param>
    /// <returns>A session with every file <see cref="BatchEntryStatus.Pending"/>.</returns>
    /// <remarks>
    /// Nothing is opened here. Reading happens in <see cref="Load"/>, so a caller
    /// can show the file names immediately and fill the rest in as it arrives.
    /// </remarks>
    public static BatchSession Create(IEnumerable<string> paths)
    {
        Throw.IfNull(paths);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<BatchEntry>();

        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string full = FullPath(path);

            if (seen.Add(full))
            {
                entries.Add(new BatchEntry(full));
            }
        }

        Log.Info($"Batch of {entries.Count} file(s) prepared.");
        return new BatchSession(entries);
    }

    /// <summary>Adds more files to an existing batch.</summary>
    /// <param name="paths">The files to add. Ones already present are ignored.</param>
    /// <returns>The entries actually added, in order.</returns>
    /// <remarks>
    /// Appended rather than merged into a new session, because the session holds
    /// the user's unsaved edits: rebuilding it to add a row would throw away
    /// everything they had typed. The new entries are
    /// <see cref="BatchEntryStatus.Pending"/>, so the next <see cref="Load"/> reads
    /// exactly them.
    /// </remarks>
    public IReadOnlyList<BatchEntry> Add(IEnumerable<string> paths)
    {
        Throw.IfNull(paths);

        var known = new HashSet<string>(_entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        var added = new List<BatchEntry>();

        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string full = FullPath(path);

            if (known.Add(full))
            {
                var entry = new BatchEntry(full);
                _entries.Add(entry);
                added.Add(entry);
            }
        }

        if (added.Count > 0)
        {
            Log.Info($"{added.Count} file(s) added to the batch; {_entries.Count} in total.");
        }

        return added;
    }

    /// <summary>Finds the editable files in a folder.</summary>
    /// <param name="directory">The folder to look in.</param>
    /// <param name="recursive">Whether to include subfolders.</param>
    /// <returns>The matching paths, in reading order.</returns>
    /// <exception cref="BookIoException">The folder could not be listed.</exception>
    /// <remarks>
    /// Filtered by extension, which is the one place in this codebase where an
    /// extension is trusted — and it is not really trusted even here: it only
    /// decides what to look at, and <see cref="Load"/> then identifies each file by
    /// content. Sniffing every file in a folder of five thousand would mean five
    /// thousand file opens to find the forty books.
    /// </remarks>
    public static IReadOnlyList<string> FindBooks(string directory, bool recursive = false)
    {
        Throw.IfNullOrEmpty(directory);

        try
        {
            IEnumerable<string> files = Directory.EnumerateFiles(
                directory,
                "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

            return [.. files
                .Where(path => BookFormats.IsSupported(FormatIds.FromExtension(path)))
                .OrderBy(path => path, NaturalNameComparer.Instance)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new BookIoException($"Could not list '{directory}'.", directory, ex);
        }
    }

    /// <summary>Reads the metadata of every file not read yet.</summary>
    /// <param name="progress">Reported once per file, from a worker thread.</param>
    /// <param name="cancellationToken">Stops the read between files.</param>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    /// <remarks>
    /// <para>
    /// Only <see cref="BatchEntryStatus.Pending"/> entries, which is what makes
    /// <see cref="Add"/> cheap and what stops a second call from discarding the
    /// user's unsaved edits by re-reading the files they were made against. There is
    /// deliberately no reload: a file whose metadata should be read again is a file
    /// the user should open again.
    /// </para>
    /// <para>
    /// Covers are not read: a grid of titles has no use for three hundred
    /// full-size images, and loading them would cost hundreds of megabytes.
    /// </para>
    /// <para>
    /// Several files at once, but not many: the work is dominated by opening files
    /// and decompressing one small document from each, so a handful of threads
    /// saturates a disk while more only adds seeks. A failure is recorded against
    /// its own file and never stops the batch — the whole point of a batch is that
    /// one bad file in a folder of four hundred does not ruin the operation.
    /// </para>
    /// </remarks>
    public void Load(IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        BatchEntry[] pending = [.. _entries.Where(e => e.Status == BatchEntryStatus.Pending)];

        int completed = 0;
        int total = pending.Length;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)),
            CancellationToken = cancellationToken,
        };

        Parallel.ForEach(pending, options, entry =>
        {
            LoadOne(entry);
            progress?.Report(new BatchProgress(Interlocked.Increment(ref completed), total, entry.Path));
        });

        Log.Info(
            $"Batch load finished: {_entries.Count(e => e.Status == BatchEntryStatus.Loaded)} loaded, "
            + $"{_entries.Count(e => e.Status == BatchEntryStatus.Unsupported)} unsupported, "
            + $"{_entries.Count(e => e.Status == BatchEntryStatus.Failed)} failed.");
    }

    private static void LoadOne(BatchEntry entry)
    {
        try
        {
            // Covers are skipped: a grid of three hundred titles has no use for
            // three hundred full-size images.
            entry.Book = Book.Load(entry.Path, ReadOptions.WithoutCover);
            entry.Detected = entry.Book.Detected;
            entry.Status = BatchEntryStatus.Loaded;
            entry.Error = null;
            entry.Snapshot();
        }
        catch (UnsupportedFormatException ex)
        {
            entry.Detected = ex.Detected;
            entry.Status = BatchEntryStatus.Unsupported;
            entry.Error = $"{FormatIds.ToDisplayName(ex.Detected.Format)}"
                + (ex.Detected.Detail is null ? "" : $" ({ex.Detected.Detail})")
                + " — this build cannot edit that format.";
        }
        catch (Exception ex) when (ex is BookFormatException or BookIoException)
        {
            entry.Status = BatchEntryStatus.Failed;
            entry.Error = ex.Message;
            Log.Error($"Could not read '{entry.Path}'", ex);
        }
    }

    /// <summary>Writes every file that has been edited.</summary>
    /// <param name="keepBackup">Whether to leave the previous version as a <c>.bak</c>.</param>
    /// <param name="progress">Reported once per file considered.</param>
    /// <param name="cancellationToken">Stops the save between files.</param>
    /// <returns>What was written, skipped and failed.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    /// <remarks>
    /// <para>
    /// One file at a time, on purpose. Each save writes a temporary sibling and
    /// swaps it in, so parallel saves would multiply peak disk use by the number of
    /// threads for no gain on a device that is already the bottleneck — and would
    /// make the order of the log, which is the record of what happened to the
    /// user's library, nondeterministic.
    /// </para>
    /// <para>
    /// Only files whose metadata actually differs from what was read are written.
    /// A file nobody edited is left alone rather than rewritten byte-identically,
    /// because not touching it cannot go wrong.
    /// </para>
    /// <para>
    /// Cancellation stops before the next file, never in the middle of one. A
    /// half-written file is exactly what <c>AtomicFileWriter</c> exists to prevent.
    /// </para>
    /// </remarks>
    public BatchSaveReport Save(
        bool keepBackup = true,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int saved = 0;
        int skipped = 0;
        int failed = 0;
        int completed = 0;

        Log.Info($"Batch save starting: {DirtyCount} of {_entries.Count} file(s) edited.");

        foreach (BatchEntry entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.IsWritable || !entry.IsDirty)
            {
                skipped++;
            }
            else if (SaveOne(entry, keepBackup))
            {
                saved++;
            }
            else
            {
                failed++;
            }

            progress?.Report(new BatchProgress(++completed, _entries.Count, entry.Path));
        }

        var report = new BatchSaveReport(saved, skipped, failed);
        Log.Info($"Batch save finished: {report}.");
        return report;
    }

    private static bool SaveOne(BatchEntry entry, bool keepBackup)
    {
        try
        {
            // The same call the single-file window makes. There is deliberately no
            // batch write path: a second way to replace a user's file is the last
            // thing this codebase needs.
            entry.Book!.Save(keepBackup);

            entry.Status = BatchEntryStatus.Saved;
            entry.Error = null;

            // The file on disk now says what the model says, so this is the new
            // baseline: saving twice must not write twice.
            entry.Snapshot();
            return true;
        }
        catch (Exception ex) when (ex is BookFormatException or BookIoException or NotSupportedException)
        {
            entry.Status = BatchEntryStatus.Failed;
            entry.Error = ex.Message;
            Log.Error($"Could not save '{entry.Path}'", ex);
            return false;
        }
    }

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unusable path is still a row the user should see, with the failure
            // attached to it, rather than a file that silently vanished from a
            // selection they made.
            return path;
        }
    }
}
