using System.Text;
using System.Threading;
using EBookMeta.Containers;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

public sealed class BatchSessionTests
{
    /// <summary>
    /// A folder holding one of everything: two books, a comic, a comic that is
    /// really a 7z archive, a truncated book, and a file that is none of our
    /// business.
    /// </summary>
    private static (BatchSession Session, string[] Paths) Folder(TempDir temp)
    {
        string[] paths =
        [
            new EpubBuilder().WriteTo(temp.File("book-1.epub")),
            new EpubBuilder().WithOpf(EpubBuilder.Epub2Opf).WriteTo(temp.File("book-2.epub")),
            new CbzBuilder().WriteTo(temp.File("comic-1.cbz")),
            SevenZipDisguisedAsCbz(temp.File("comic-2.cbz")),
            new EpubBuilder().WithContainerXml(null).WriteTo(temp.File("broken.epub")),
        ];

        File.WriteAllText(temp.File("notes.txt"), "not a book");

        return (BatchSession.Create(paths), paths);
    }

    /// <summary>
    /// A disguised archive this build recognises and cannot open. RAR filled this
    /// role until CBR became readable; 7z is what is left that still cannot.
    /// </summary>
    private static string SevenZipDisguisedAsCbz(string path)
    {
        File.WriteAllBytes(path, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, .. new byte[64]]);
        return path;
    }

    private static BatchEntry Entry(BatchSession session, string fileName) =>
        session.Entries.Single(e => e.FileName == fileName);

    [Fact]
    public void Create_drops_duplicates_and_keeps_order()
    {
        using var temp = new TempDir();
        string first = new EpubBuilder().WriteTo(temp.File("a.epub"));
        string second = new CbzBuilder().WriteTo(temp.File("b.cbz"));

        BatchSession session = BatchSession.Create([first, second, first, "  "]);

        Assert.Equal(["a.epub", "b.cbz"], session.Entries.Select(e => e.FileName));
        Assert.All(session.Entries, e => Assert.Equal(BatchEntryStatus.Pending, e.Status));
    }

    [Fact]
    public void FindBooks_takes_the_editable_files_in_reading_order()
    {
        using var temp = new TempDir();
        new CbzBuilder().WriteTo(temp.File("comic-10.cbz"));
        new CbzBuilder().WriteTo(temp.File("comic-2.cbz"));
        new EpubBuilder().WriteTo(temp.File("book.epub"));
        File.WriteAllText(temp.File("notes.txt"), "x");

        // Natural order, so comic-2 precedes comic-10 the way a person reads them.
        Assert.Equal(
            ["book.epub", "comic-2.cbz", "comic-10.cbz"],
            BatchSession.FindBooks(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void FindBooks_can_include_subfolders_and_reports_a_folder_it_cannot_list()
    {
        using var temp = new TempDir();
        string nested = Path.Combine(temp.Path, "series");
        Directory.CreateDirectory(nested);

        new EpubBuilder().WriteTo(temp.File("book.epub"));
        new CbzBuilder().WriteTo(Path.Combine(nested, "issue.cbz"));

        Assert.Single(BatchSession.FindBooks(temp.Path));
        Assert.Equal(2, BatchSession.FindBooks(temp.Path, recursive: true).Count);

        Assert.Throws<BookIoException>(
            () => BatchSession.FindBooks(Path.Combine(temp.Path, "does-not-exist")));
    }

    [Fact]
    public void Load_gives_every_file_its_own_outcome()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();

        Assert.Equal(BatchEntryStatus.Loaded, Entry(session, "book-1.epub").Status);
        Assert.Equal(BatchEntryStatus.Loaded, Entry(session, "book-2.epub").Status);
        Assert.Equal(BatchEntryStatus.Loaded, Entry(session, "comic-1.cbz").Status);
        Assert.Equal(BatchEntryStatus.Unsupported, Entry(session, "comic-2.cbz").Status);
        Assert.Equal(BatchEntryStatus.Failed, Entry(session, "broken.epub").Status);

        Assert.Contains("container.xml", Entry(session, "broken.epub").Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_file_says_what_it_actually_is()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();

        BatchEntry entry = Entry(session, "comic-2.cbz");

        // "This .cbz is really a 7z archive" is more useful than "unsupported file".
        Assert.Equal(FormatId.Cb7, entry.Detected?.Format);
        Assert.Contains("CB7", entry.Error!, StringComparison.Ordinal);
        Assert.Null(entry.Book);
        Assert.False(entry.IsWritable);
    }

    [Fact]
    public void Load_reads_metadata_but_not_covers()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();

        Assert.Equal(
            "The Ocean at the End of the Lane",
            Entry(session, "book-1.epub").Read(MetadataField.Title));
        Assert.Equal("The Sandman", Entry(session, "comic-1.cbz").Read(MetadataField.Series));

        // A grid of titles has no use for three hundred full-size images.
        Assert.Null(Entry(session, "book-1.epub").Metadata?.Cover);
        Assert.Null(Entry(session, "comic-1.cbz").Metadata?.Cover);
    }

    [Fact]
    public void Load_reports_progress_for_every_file()
    {
        using var temp = new TempDir();
        (BatchSession session, string[] paths) = Folder(temp);

        var reported = new List<BatchProgress>();
        var progress = new Progress(reported);

        session.Load(progress);

        Assert.Equal(paths.Length, reported.Count);
        Assert.All(reported, p => Assert.Equal(paths.Length, p.Total));
        Assert.Equal(paths.Length, reported.Max(p => p.Completed));
    }

    [Fact]
    public void Add_appends_files_and_only_the_new_ones_are_read()
    {
        using var temp = new TempDir();
        string first = new EpubBuilder().WriteTo(temp.File("book-1.epub"));

        BatchSession session = BatchSession.Create([first]);
        session.Load();

        BatchEntry existing = Entry(session, "book-1.epub");
        existing.Apply(MetadataField.Publisher, "Vertigo");

        string second = new CbzBuilder().WriteTo(temp.File("comic.cbz"));

        Assert.Single(session.Add([second, first]));
        Assert.Equal(2, session.Entries.Count);

        session.Load();

        // The edit made before the second load is still there: loading again must
        // not re-read a file the user has been typing into.
        Assert.True(existing.IsDirty);
        Assert.Equal("Vertigo", existing.Read(MetadataField.Publisher));
        Assert.Equal(BatchEntryStatus.Loaded, Entry(session, "comic.cbz").Status);
    }

    [Fact]
    public void Dirtiness_is_measured_against_what_was_read()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();

        Assert.Equal(0, session.DirtyCount);
        Assert.All(session.Entries, e => Assert.False(e.IsDirty));

        BatchEntry entry = Entry(session, "book-1.epub");

        // Typing a value and typing it back is not an edit, so it must not write.
        entry.Apply(MetadataField.Publisher, entry.Read(MetadataField.Publisher));
        Assert.False(entry.IsDirty);

        Assert.True(entry.Apply(MetadataField.Publisher, "Vertigo"));
        Assert.True(entry.IsDirty);
        Assert.Equal([MetadataField.Publisher], entry.ChangedFields);
        Assert.Equal(1, session.DirtyCount);
    }

    [Fact]
    public void A_field_the_format_cannot_store_is_refused()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();

        // The backstop behind a disabled cell: a bulk apply across a mixed
        // selection must not write a sort title into a comic.
        Assert.False(Entry(session, "comic-1.cbz").Apply(MetadataField.SortTitle, "Sandman, The"));
        Assert.False(Entry(session, "comic-1.cbz").IsDirty);
        Assert.True(Entry(session, "book-1.epub").Apply(MetadataField.SortTitle, "Ocean, The"));

        Assert.False(Entry(session, "comic-2.cbz").Apply(MetadataField.Title, "Anything"));
    }

    [Fact]
    public void Save_writes_the_edited_files_and_touches_nothing_else()
    {
        using var temp = new TempDir();
        (BatchSession session, string[] paths) = Folder(temp);

        session.Load();

        Dictionary<string, byte[]> before = paths.ToDictionary(Path.GetFileName, File.ReadAllBytes);

        Entry(session, "book-1.epub").Apply(MetadataField.Publisher, "Vertigo");
        Entry(session, "comic-1.cbz").Apply(MetadataField.Publisher, "Vertigo");

        BatchSaveReport report = session.Save(keepBackup: false);

        Assert.Equal(2, report.Saved);
        Assert.Equal(3, report.Skipped);
        Assert.Equal(0, report.Failed);

        Assert.NotEqual(before["book-1.epub"], File.ReadAllBytes(temp.File("book-1.epub")));
        Assert.NotEqual(before["comic-1.cbz"], File.ReadAllBytes(temp.File("comic-1.cbz")));

        // Untouched files are not rewritten byte-identically — they are not
        // rewritten at all.
        Assert.Equal(before["book-2.epub"], File.ReadAllBytes(temp.File("book-2.epub")));
        Assert.Equal(before["comic-2.cbz"], File.ReadAllBytes(temp.File("comic-2.cbz")));
        Assert.Equal(before["broken.epub"], File.ReadAllBytes(temp.File("broken.epub")));
    }

    [Fact]
    public void Saved_edits_survive_a_reload()
    {
        using var temp = new TempDir();
        (BatchSession session, string[] paths) = Folder(temp);

        session.Load();
        Entry(session, "comic-1.cbz").Apply(MetadataField.Publisher, "Vertigo");
        Entry(session, "comic-1.cbz").Apply(MetadataField.Subjects, "Horror, Comics");
        session.Save(keepBackup: false);

        BatchSession reloaded = BatchSession.Create(paths);
        reloaded.Load();

        BatchEntry entry = Entry(reloaded, "comic-1.cbz");
        Assert.Equal("Vertigo", entry.Read(MetadataField.Publisher));
        Assert.Equal("Horror, Comics", entry.Read(MetadataField.Subjects));
    }

    [Fact]
    public void Saving_twice_writes_once()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();
        Entry(session, "book-1.epub").Apply(MetadataField.Publisher, "Vertigo");

        Assert.Equal(1, session.Save(keepBackup: false).Saved);
        Assert.Equal(BatchEntryStatus.Saved, Entry(session, "book-1.epub").Status);
        Assert.False(Entry(session, "book-1.epub").IsDirty);
        Assert.Equal(0, session.Save(keepBackup: false).Saved);
    }

    [Fact]
    public void A_file_marked_by_hand_is_saved_although_nothing_changed()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();

        BatchEntry entry = Entry(session, "book-1.epub");

        // Repairs live in memory until a save, so a file that needs one is not
        // dirty and nothing else would ever put it through Book.Save.
        Assert.False(entry.WillSave);
        entry.SaveRequested = true;

        Assert.True(entry.WillSave);
        Assert.Equal(1, session.PendingSaveCount);
        Assert.Equal(0, session.DirtyCount);

        BatchSaveReport report = session.Save(keepBackup: false);

        Assert.Equal(1, report.Saved);
        Assert.Equal(BatchEntryStatus.Saved, entry.Status);
    }

    [Fact]
    public void Marking_an_untagged_comic_gives_it_a_ComicInfo()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder().WithoutComicInfo().WriteTo(temp.File("untagged.cbz"));

        BatchSession session = BatchSession.Create([path]);
        session.Load();

        Entry(session, "untagged.cbz").SaveRequested = true;

        Assert.Equal(1, session.Save(keepBackup: false).Saved);

        // The point of the mark: an unedited save is what writes the correction the
        // read reported, exactly as it does in the single-file window.
        using ZipContainer saved = ZipContainer.Open(path);
        Assert.Contains("ComicInfo.xml", saved.Entries.Select(e => e.Name));
    }

    [Fact]
    public void A_mark_is_spent_by_the_save_that_used_it()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();
        Entry(session, "book-1.epub").SaveRequested = true;

        Assert.Equal(1, session.Save(keepBackup: false).Saved);
        Assert.False(Entry(session, "book-1.epub").WillSave);
        Assert.Equal(0, session.Save(keepBackup: false).Saved);
    }

    [Fact]
    public void An_edited_file_is_saved_even_if_it_is_unmarked()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();

        BatchEntry entry = Entry(session, "book-1.epub");
        entry.Apply(MetadataField.Publisher, "Vertigo");

        // The mark adds files to a save and never drops one, which is why the grid
        // shows an edited row ticked and locked rather than merely ticked.
        entry.SaveRequested = false;

        Assert.True(entry.WillSave);
        Assert.Equal(1, session.Save(keepBackup: false).Saved);
    }

    [Fact]
    public void A_file_that_cannot_be_written_is_never_marked()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();

        byte[] before = File.ReadAllBytes(temp.File("comic-2.cbz"));

        Entry(session, "comic-2.cbz").SaveRequested = true;
        Entry(session, "broken.epub").SaveRequested = true;

        Assert.All(session.Entries, e => Assert.False(e.WillSave));
        Assert.Equal(0, session.PendingSaveCount);
        Assert.Equal(0, session.Save(keepBackup: false).Saved);
        Assert.Equal(before, File.ReadAllBytes(temp.File("comic-2.cbz")));
    }

    [Fact]
    public void Save_keeps_a_backup_when_asked()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();
        Entry(session, "book-1.epub").Apply(MetadataField.Publisher, "Vertigo");
        session.Save(keepBackup: true);

        Assert.True(File.Exists(temp.File("book-1.epub") + ".bak"));
    }

    [Fact]
    public void A_file_that_cannot_be_written_fails_alone()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        session.Load();

        string locked = temp.File("book-1.epub");

        Entry(session, "book-1.epub").Apply(MetadataField.Publisher, "Vertigo");
        Entry(session, "book-2.epub").Apply(MetadataField.Publisher, "Vertigo");

        File.SetAttributes(locked, FileAttributes.ReadOnly);

        try
        {
            BatchSaveReport report = session.Save(keepBackup: false);

            Assert.Equal(1, report.Saved);
            Assert.Equal(1, report.Failed);
            Assert.Equal(BatchEntryStatus.Failed, Entry(session, "book-1.epub").Status);
            Assert.NotNull(Entry(session, "book-1.epub").Error);
            Assert.Equal(BatchEntryStatus.Saved, Entry(session, "book-2.epub").Status);
        }
        finally
        {
            File.SetAttributes(locked, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_refused_write_is_reported_on_its_own_row()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder().WriteTo(temp.File("cbl.cbz"));
        CbzBuilder.AddArchiveComment(path, "{\"appID\":\"ComicBookLover\"}");

        BatchSession session = BatchSession.Create([path, new EpubBuilder().WriteTo(temp.File("book.epub"))]);
        session.Load();

        Entry(session, "cbl.cbz").Apply(MetadataField.Publisher, "Vertigo");
        Entry(session, "book.epub").Apply(MetadataField.Publisher, "Vertigo");

        BatchSaveReport report = session.Save(keepBackup: false);

        Assert.Equal(1, report.Saved);
        Assert.Equal(1, report.Failed);
        Assert.Contains("ComicBookLover", Entry(session, "cbl.cbz").Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_and_save_can_be_cancelled()
    {
        using var temp = new TempDir();
        (BatchSession session, _) = Folder(temp);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => session.Load(null, cancelled.Token));

        session.Load();
        Entry(session, "book-1.epub").Apply(MetadataField.Publisher, "Vertigo");

        Assert.Throws<OperationCanceledException>(() => session.Save(true, null, cancelled.Token));
        Assert.True(Entry(session, "book-1.epub").IsDirty);
    }

    [Fact]
    public void The_report_reads_as_a_status_line()
    {
        Assert.Equal("2 saved", new BatchSaveReport(2, 0, 0).ToString());
        Assert.Equal("2 saved · 3 unchanged · 1 failed", new BatchSaveReport(2, 3, 1).ToString());
    }

    [Fact]
    public void Rules_run_as_part_of_the_load()
    {
        using var temp = new TempDir();

        string clean = new CbzBuilder().WriteTo(temp.File("clean.cbz"));
        string untagged = new CbzBuilder().WithoutComicInfo().WriteTo(temp.File("untagged.cbz"));

        BatchSession session = BatchSession.Create([clean, untagged]);
        session.Load();

        // An untagged comic still loads; it simply has nothing to show yet, and a
        // save is what gives it a ComicInfo.xml.
        Assert.NotNull(Entry(session, "clean.cbz").Book);
        Assert.Null(Entry(session, "untagged.cbz").Book!.Metadata.Series);
    }

    private sealed class Progress(List<BatchProgress> reports) : IProgress<BatchProgress>
    {
        private readonly object _gate = new();

        public void Report(BatchProgress value)
        {
            lock (_gate)
            {
                reports.Add(value);
            }
        }
    }
}
