using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Covers the session log: what gets recorded, and the rule that a clean run
/// never touches the disk while a bad one always does.
/// </summary>
public sealed class LogTests : IDisposable
{
    public LogTests()
    {
        Log.Clear();
        Log.FilePath = null;
        Log.AlwaysWriteToFile = false;
    }

    public void Dispose()
    {
        Log.Clear();
        Log.FilePath = null;
        Log.AlwaysWriteToFile = false;
    }

    [Fact]
    public void EntriesAreRecordedInOrderWithTheirLevel()
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
    public void AnEntryFormatsAsOneAlignedLine()
    {
        var entry = new LogEntry(new DateTime(2026, 8, 14, 9, 5, 3, 42), LogLevel.Warning, "something odd");

        Assert.Equal("09:05:03.042  WARNING  something odd", entry.ToString());
    }

    [Fact]
    public void AnExceptionIsRecordedWithItsTypeAndMessage()
    {
        Log.Error("Could not open 'x.epub'", new BookFormatException("not well-formed"));

        LogEntry entry = Assert.Single(Log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("BookFormatException", entry.Message, StringComparison.Ordinal);
        Assert.Contains("not well-formed", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Severity.Info, LogLevel.Info)]
    [InlineData(Severity.Warning, LogLevel.Warning)]
    [InlineData(Severity.Error, LogLevel.Error)]
    [InlineData(Severity.Fatal, LogLevel.Error)]
    public void FindingsAreLoggedAtAMatchingLevel(Severity severity, LogLevel expected)
    {
        Log.Finding(new Finding
        {
            RuleId = "EPUB-W070",
            Severity = severity,
            Message = "Namespace prefix 'opf' is used but never declared.",
            Location = "OEBPS/content.opf",
            Line = 4,
        });

        LogEntry entry = Assert.Single(Log.Entries);
        Assert.Equal(expected, entry.Level);
        Assert.Contains("EPUB-W070", entry.Message, StringComparison.Ordinal);
        Assert.Contains("OEBPS/content.opf:4", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubscribersAreNotifiedOfEachEntry()
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
    public void ACleanRunNeverTouchesTheDisk()
    {
        using var temp = new TempDir();
        string path = temp.File("EBookMetaEditor.log");
        Log.FilePath = path;

        Log.Info("opened a file");
        Log.Debug("all fine");

        // Info and Debug alone must not create the file: startup has a 400 ms
        // budget and opening a file can cost an antivirus scan.
        Assert.False(File.Exists(path));
        Assert.False(Log.FileWritten);
    }

    [Fact]
    public void AWarningWritesTheWholeSessionSoFar()
    {
        using var temp = new TempDir();
        string path = temp.File("EBookMetaEditor.log");
        Log.FilePath = path;

        Log.Info("this happened first");
        Log.Debug("and this");
        Log.Warning("then something odd");

        // The run-up matters as much as the problem, so earlier entries are
        // flushed too rather than only the warning.
        string written = File.ReadAllText(path);
        Assert.True(Log.FileWritten);
        Assert.Contains("this happened first", written, StringComparison.Ordinal);
        Assert.Contains("and this", written, StringComparison.Ordinal);
        Assert.Contains("then something odd", written, StringComparison.Ordinal);
    }

    [Fact]
    public void EntriesAfterAWarningAreAppendedAsTheyArrive()
    {
        using var temp = new TempDir();
        string path = temp.File("EBookMetaEditor.log");
        Log.FilePath = path;

        Log.Warning("first problem");
        Log.Info("carried on");

        string written = File.ReadAllText(path);
        Assert.Contains("first problem", written, StringComparison.Ordinal);
        Assert.Contains("carried on", written, StringComparison.Ordinal);

        // Written once each, not duplicated by the second flush.
        Assert.Equal(1, written.Split(["carried on"], StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void AnUnwritableFileDoesNotBecomeTheProblem()
    {
        // A directory that does not exist. The log must degrade to memory rather
        // than throwing out of whatever was being logged about.
        Log.FilePath = Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("n"), "x.log");

        Log.Warning("something odd");

        Assert.False(Log.FileWritten);
        Assert.Single(Log.Entries);
    }

    [Fact]
    public void MemoryIsBounded()
    {
        for (int i = 0; i < Log.Capacity + 100; i++)
        {
            Log.Debug($"entry {i}");
        }

        Assert.True(Log.Entries.Count <= Log.Capacity);

        // The newest are the ones kept.
        Assert.Contains($"entry {Log.Capacity + 99}", Log.Entries[Log.Entries.Count - 1].Message, StringComparison.Ordinal);
    }

    // --- what real work records ------------------------------------------

    [Fact]
    public void OpeningABookIsLogged()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid.epub"));

        Log.Clear();

        using ZipContainer container = ZipContainer.Open(path);
        new EpubHandler().Read(container);

        Assert.Contains(
            Log.Entries,
            e => e.Level == LogLevel.Info && e.Message.Contains("Read EPUB"));
    }

    [Fact]
    public void ARepairIsLoggedAsAWarning()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithOpf(EpubBuilder.Epub2OpfUndeclaredOpfPrefix)
            .WriteTo(temp.File("broken.epub"));

        Log.Clear();

        using ZipContainer container = ZipContainer.Open(path);
        new EpubHandler().Read(container);

        // A repair is exactly the kind of thing a user should be able to find out
        // about after the fact, so it is a warning rather than a debug line.
        LogEntry repair = Assert.Single(
            Log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("repaired")));

        Assert.Contains("xmlns", repair.Message, StringComparison.Ordinal);
        Assert.Contains("opf", repair.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrepairableDocumentIsLoggedAsAnError()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithOpf(EpubBuilder.OpfUnknownPrefix)
            .WriteTo(temp.File("unknown-prefix.epub"));

        Log.Clear();

        using ZipContainer container = ZipContainer.Open(path);
        Assert.Throws<BookFormatException>(() => new EpubHandler().Read(container));

        Assert.Contains(
            Log.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("acme"));
    }

    [Fact]
    public void AMisleadingExtensionIsLoggedAsAWarning()
    {
        using var temp = new TempDir();
        string path = temp.File("rar-disguised-as-cbz.cbz");
        File.WriteAllBytes(
            path,
            System.Text.Encoding.ASCII.GetBytes("Rar!\x1a\x07\x00").Concat(new byte[64]).ToArray());

        Log.Clear();
        FormatDetector.Detect(path);

        Assert.Contains(
            Log.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("extension"));
    }
}
