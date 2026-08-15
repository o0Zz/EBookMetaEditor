using System.Text;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Covers the session log: what gets recorded, and the rule that a clean run never
/// touches the disk while a bad one always does.
/// </summary>
public sealed class LogTests : IDisposable
{
    public LogTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        Log.Clear();
        Log.FilePath = null;
        Log.AlwaysWriteToFile = false;
    }

    [Fact]
    public void Entries_are_recorded_in_order_with_their_level()
    {
        Log.Debug("one");
        Log.Info("two");
        Log.Warning("three");
        Log.Error("four");

        Assert.Equal(
            [LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error],
            Log.Entries.Select(e => e.Level));

        Assert.Equal(["one", "two", "three", "four"], Log.Entries.Select(e => e.Message));
    }

    [Fact]
    public void An_entry_formats_as_one_aligned_line()
    {
        var entry = new LogEntry(new DateTime(2026, 8, 14, 9, 5, 3, 42), LogLevel.Warning, "something odd");

        Assert.Equal("09:05:03.042  WARNING  something odd", entry.ToString());
    }

    [Fact]
    public void An_exception_is_recorded_with_its_type_and_message()
    {
        Log.Error("Could not open 'x.epub'", new BookFormatException("not well-formed"));

        LogEntry entry = Assert.Single(Log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("BookFormatException", entry.Message, StringComparison.Ordinal);
        Assert.Contains("not well-formed", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule id leads the line, because it is what a user pastes into a bug
    /// report and what they search the log for.
    /// </summary>
    [Theory]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    public void A_rule_is_logged_at_the_level_it_was_given(LogLevel level)
    {
        Log.Rule(
            level,
            "EPUB-W070",
            "Namespace prefix 'opf' is used but never declared.",
            "OEBPS/content.opf");

        LogEntry entry = Assert.Single(Log.Entries);

        Assert.Equal(level, entry.Level);
        Assert.StartsWith("EPUB-W070:", entry.Message, StringComparison.Ordinal);
        Assert.Contains("OEBPS/content.opf", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_with_no_location_omits_it()
    {
        Log.Rule(LogLevel.Warning, "CBZ-W010", "There is no ComicInfo.xml.");

        Assert.DoesNotContain("(", Assert.Single(Log.Entries).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Subscribers_are_notified_of_each_entry()
    {
        var seen = new List<LogEntry>();
        void Handler(LogEntry e) => seen.Add(e);

        Log.Written += Handler;
        try
        {
            Log.Info("watched");
        }
        finally
        {
            Log.Written -= Handler;
        }

        Log.Info("unwatched");

        Assert.Equal("watched", Assert.Single(seen).Message);
    }

    // --- the file, and when it appears -----------------------------------

    [Fact]
    public void A_clean_run_never_touches_the_disk()
    {
        using var temp = new TempDir();
        string path = temp.File("EBookMetaEditor.log");
        Log.FilePath = path;

        // Info and Debug alone must not create the file: startup has a 400 ms
        // budget and opening a file can cost an antivirus scan.
        Log.Info("opened a file");
        Log.Debug("all fine");

        Assert.False(File.Exists(path));
        Assert.False(Log.FileWritten);
    }

    [Fact]
    public void A_warning_writes_the_whole_session_so_far_and_then_appends()
    {
        using var temp = new TempDir();
        string path = temp.File("EBookMetaEditor.log");
        Log.FilePath = path;

        Log.Info("this happened first");
        Log.Debug("and this");
        Log.Warning("then something odd");
        Log.Info("carried on");

        // The run-up matters as much as the problem, so earlier entries are
        // flushed too rather than only the warning.
        string written = File.ReadAllText(path);
        Assert.True(Log.FileWritten);
        Assert.Contains("this happened first", written, StringComparison.Ordinal);
        Assert.Contains("and this", written, StringComparison.Ordinal);
        Assert.Contains("then something odd", written, StringComparison.Ordinal);

        // Written once each, not duplicated by the second flush.
        Assert.Equal(1, written.Split(["carried on"], StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void An_unwritable_file_does_not_become_the_problem()
    {
        // A directory that does not exist. The log must degrade to memory rather
        // than throwing out of whatever was being logged about.
        Log.FilePath = Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("n"), "x.log");

        Log.Warning("something odd");

        Assert.False(Log.FileWritten);
        Assert.Single(Log.Entries);
    }

    [Fact]
    public void Memory_is_bounded_and_keeps_the_newest()
    {
        for (int i = 0; i < Log.Capacity + 100; i++)
        {
            Log.Debug($"entry {i}");
        }

        Assert.True(Log.Entries.Count <= Log.Capacity);
        Assert.Contains(
            $"entry {Log.Capacity + 99}",
            Log.Entries[Log.Entries.Count - 1].Message,
            StringComparison.Ordinal);
    }

    // --- what real work records ------------------------------------------

    [Fact]
    public void Opening_a_book_is_logged()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid.epub"));

        Log.Clear();

        using ZipContainer container = ZipContainer.Open(path);
        new EpubFormat().Read(container);

        Assert.Contains(Log.Entries, e => e.Level == LogLevel.Info && e.Message.Contains("Read EPUB"));
    }

    [Fact]
    public void A_repair_is_logged_as_a_warning()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithOpf(EpubBuilder.Epub2OpfUndeclaredOpfPrefix)
            .WriteTo(temp.File("broken.epub"));

        Log.Clear();

        // Through Book.Load, which is the path the app takes: forwarding findings
        // to the log is Book's job. A repair is exactly the kind of thing a user
        // should be able to find out about after the fact.
        Book.Load(path);

        LogEntry repair = Assert.Single(
            Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("repaired"));

        Assert.Contains("xmlns", repair.Message, StringComparison.Ordinal);
        Assert.Contains("opf", repair.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrepairable_document_is_logged_as_an_error()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithOpf(EpubBuilder.OpfUnknownPrefix)
            .WriteTo(temp.File("unknown-prefix.epub"));

        Log.Clear();

        using ZipContainer container = ZipContainer.Open(path);
        Assert.Throws<BookFormatException>(() => new EpubFormat().Read(container));

        Assert.Contains(Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("acme"));
    }

    [Fact]
    public void A_misleading_extension_is_logged_as_a_warning()
    {
        using var temp = new TempDir();
        string path = temp.File("rar-disguised-as-cbz.cbz");
        File.WriteAllBytes(
            path,
            [.. Encoding.ASCII.GetBytes("Rar!\x1a\x07\x00"), .. new byte[64]]);

        Log.Clear();

        // Detection alone says nothing: it answers a question and is asked more
        // than once per file. The disagreement is rule GEN-W002, reported by the load.
        Assert.Throws<UnsupportedFormatException>(() => Book.Load(path));

        Assert.Contains(Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("extension"));
    }
}
