using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;

namespace EBookMeta;

/// <summary>
/// One book or comic, open for editing: what it is, what its metadata says, and
/// what was noticed about it.
/// </summary>
public sealed class Book
{
    private readonly IBookFormat _format;
    private readonly List<Finding> _loadFindings;
    private readonly List<Finding> _saveFindings = [];

    private Book(
        string path,
        DetectedFormat detected,
        IBookFormat format,
        BookMetadata metadata,
        List<Finding> loadFindings,
        int entryCount)
    {
        Path = path;
        Detected = detected;
        _format = format;
        Metadata = metadata;
        _loadFindings = loadFindings;
        EntryCount = entryCount;
    }

    /// <summary>The file this was loaded from, and the file <see cref="Save"/> replaces.</summary>
    public string Path { get; }

    /// <summary>What the content turned out to be, and whether the extension agreed.</summary>
    public DetectedFormat Detected { get; }

    /// <summary>Which fields this file's format can store, and whether it can be written.</summary>
    public FormatCapabilities Capabilities => _format.Capabilities;

    /// <summary>
    /// The metadata, mutable in place. Edits reach the file only through
    /// <see cref="Save"/>.
    /// </summary>
    public BookMetadata Metadata { get; }

    /// <summary>How many entries the container held when it was read.</summary>
    public int EntryCount { get; }

    /// <summary>What loading the file noticed, by stable rule ID.</summary>
    public IReadOnlyList<Finding> LoadFindings => _loadFindings;

    /// <summary>
    /// What the most recent <see cref="Save"/> noticed and corrected. Empty until
    /// the file has been saved.
    /// </summary>
    public IReadOnlyList<Finding> SaveFindings => _saveFindings;

    /// <summary>Whether this format can be written at all.</summary>
    public bool CanSave => Capabilities.CanWrite;

    /// <summary>Opens a file for editing.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="options">
    /// How much to read. <see langword="null"/> means <see cref="ReadOptions.Default"/>;
    /// a grid of hundreds of rows wants <see cref="ReadOptions.WithoutCover"/>.
    /// </param>
    /// <param name="findings">
    /// Also receives everything the load noticed, and is filled even when the load
    /// fails. <see cref="LoadFindings"/> is enough for a file that opened; this is
    /// for a caller that wants the diagnosis of one that did not.
    /// </param>
    /// <returns>The open book.</returns>
    /// <exception cref="UnsupportedFormatException">
    /// The file was recognised but no registered <see cref="IBookFormat"/> can
    /// edit it — most often a RAR archive with a <c>.cbz</c> extension.
    /// </exception>
    /// <exception cref="BookFormatException">
    /// The file is damaged beyond what can be recovered on the way in.
    /// </exception>
    /// <exception cref="BookIoException">The file could not be read.</exception>
    public static Book Load(
        string path, ReadOptions? options = null, ICollection<Finding>? findings = null)
    {
        Throw.IfNullOrEmpty(path);

        var collected = new List<Finding>();
        IBookFormat? format = BookFormats.Resolve(path, out DetectedFormat detected);

        if (!detected.ExtensionAgrees)
        {
            collected.Add(ExtensionDisagrees(path, detected));
        }

        if (format is null)
        {
            // Reported before throwing: the extension disagreement is usually the
            // most useful thing anyone will learn about this file, and it is the
            // reason the open failed.
            Publish(collected, findings);
            throw new UnsupportedFormatException(detected, path);
        }

        using IContainer container = BookContainers.Open(path, detected.Container);

        try
        {
            CheckEntryNames(container, collected);

            BookMetadata metadata = format.Read(container, options, collected);

            Publish(collected, findings);

            return new Book(path, detected, format, metadata, collected, container.Entries.Count);
        }
        catch (BookFormatException)
        {
            // Published on the way out, not swallowed. A read that fails has
            // usually already said why — CBZ-F001, or the namespace prefixes no
            // specification covers — and losing that on the exception's way up
            // would leave the user with a message and no rule ID to search for.
            Publish(collected, findings);
            throw;
        }
    }

    /// <summary>
    /// Writes the metadata, correcting whatever can be corrected, and replaces the
    /// original atomically.
    /// </summary>
    /// <param name="keepBackup">
    /// Whether to leave the previous version beside the file as <c>.bak</c>.
    /// </param>
    /// <returns>The backup's path, or <see langword="null"/> if none was kept.</returns>
    /// <exception cref="NotSupportedException">This format cannot be written.</exception>
    /// <exception cref="BookFormatException">
    /// The file cannot be rebuilt without losing something — a comic archive
    /// carrying a ZIP comment, for instance.
    /// </exception>
    /// <exception cref="BookIoException">
    /// The write or the atomic swap failed. The original is unchanged.
    /// </exception>
    public string? Save(bool keepBackup = true)
    {
        if (!CanSave)
        {
            throw new NotSupportedException(
                $"{Detected.Format.DisplayName()} cannot be written by this build.");
        }

        _saveFindings.Clear();

        try
        {
            return AtomicFileWriter.Write(
                Path,
                temp =>
                {
                    // Reopened inside the callback so the source handle is closed
                    // before File.Replace swaps the file underneath it.
                    using IContainer container = BookContainers.Open(Path, Detected.Container);
                    _format.Write(container, Metadata, temp, _saveFindings);
                },
                keepBackup);
        }
        finally
        {
            // In a finally so a correction that was made before the write failed is
            // still on the record. The user's file is unchanged either way.
            Report(_saveFindings);
        }
    }

    /// <summary>Returns the file name and what it turned out to be, for diagnostics.</summary>
    /// <returns>A short description.</returns>
    public override string ToString() =>
        $"{System.IO.Path.GetFileName(Path)} ({Detected.Format.DisplayName()})";

    /// <summary>
    /// Forwards findings to the session log, which is the only place they surface.
    /// </summary>
    private static void Report(IEnumerable<Finding> findings)
    {
        foreach (Finding finding in findings)
        {
            Log.Finding(finding);
        }
    }

    /// <summary>Logs findings and copies them to a caller's sink, if there is one.</summary>
    private static void Publish(List<Finding> collected, ICollection<Finding>? sink)
    {
        Report(collected);

        if (sink is null)
        {
            return;
        }

        foreach (Finding finding in collected)
        {
            sink.Add(finding);
        }
    }

    private static Finding ExtensionDisagrees(string path, DetectedFormat detected) =>
        new()
        {
            RuleId = "GEN-W002",
            Severity = Severity.Warning,
            Message =
                $"The extension says {detected.ClaimedByExtension.DisplayName()} "
                + $"but the content is {detected.Format.DisplayName()}.",
            Location = System.IO.Path.GetFileName(path),
            Detail = detected.Detail,
        };

    /// <summary>
    /// Reports entry names that point outside the archive.
    /// </summary>
    private static void CheckEntryNames(IContainer container, List<Finding> findings)
    {
        foreach (ContainerEntry entry in container.Entries)
        {
            if (!EscapesArchive(entry.Name))
            {
                continue;
            }

            findings.Add(new Finding
            {
                RuleId = "GEN-E003",
                Severity = Severity.Error,
                Message = "Entry name is absolute or escapes the archive.",
                Location = entry.Name,
            });
        }
    }

    private static bool EscapesArchive(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name[0] is '/' or '\\' || System.IO.Path.IsPathRooted(name) || name.IndexOf(':') >= 0)
        {
            return true;
        }

        foreach (string segment in name.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }
}
