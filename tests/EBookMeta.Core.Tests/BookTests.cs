using System.Text;
using EBookMeta.Formats;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Covers <see cref="Book"/>, the one object both editors load and save through:
/// opening a file reports what is wrong with it, opening never writes, and the file
/// on disk is what the user last saved.
/// </summary>
public sealed class BookTests
{
    private static List<Finding> Findings(string path)
    {
        var findings = new List<Finding>();

        try
        {
            Book.Load(path, ReadOptions.WithoutCover, findings);
        }
        catch (BookFormatException)
        {
        }

        return findings;
    }

    [Fact]
    public void Loading_reports_the_format_the_metadata_and_the_size()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("book.epub"));

        Book book = Book.Load(path);

        Assert.Equal(FormatId.Epub, book.Detected.Format);
        Assert.Equal("The Ocean at the End of the Lane", book.Metadata.Title);
        Assert.True(book.CanSave);
        Assert.Equal(path, book.Path);

        // Reported by the Book rather than by an open container the caller has to
        // keep alive, which is what lets the window close its handle immediately.
        Assert.True(book.EntryCount > 0);
    }

    [Fact]
    public void A_recognised_but_unsupported_format_names_itself()
    {
        using var temp = new TempDir();
        string path = temp.File("rar-disguised-as-cbz.cbz");
        File.WriteAllBytes(
            path,
            [.. Encoding.ASCII.GetBytes("Rar!\x1a\x07\x00"), .. new byte[64]]);

        UnsupportedFormatException ex =
            Assert.Throws<UnsupportedFormatException>(() => Book.Load(path));

        Assert.Equal(FormatId.Cbr, ex.Detected.Format);

        // GEN-W002 is reported even though the open failed: the mismatch is the
        // reason it failed, and it is the useful half of the answer.
        Finding finding = Assert.Single(Findings(path), f => f.RuleId == "GEN-W002");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("extension", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_misleading_extension_is_reported_but_does_not_stop_the_open()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("actually-a-book.cbz"));

        // Detection is by content, so an EPUB called .cbz is edited as an EPUB. The
        // disagreement is worth a warning because it is usually a mistake — but it
        // is not a reason to refuse a file this tool can read.
        Book book = Book.Load(path);

        Assert.Equal(FormatId.Epub, book.Detected.Format);
        Assert.Equal("The Ocean at the End of the Lane", book.Metadata.Title);
        Assert.Contains(book.LoadFindings, f => f.RuleId == "GEN-W002");
    }

    [Fact]
    public void Gen_e003_an_entry_name_that_escapes_the_archive()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithEntry("../evil.txt", "nothing good")
            .WriteTo(temp.File("broken-gen-e003-traversal.cbz"));

        Finding finding = Assert.Single(Findings(path), f => f.RuleId == "GEN-E003");

        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("../evil.txt", finding.Location);
    }

    /// <summary>
    /// The property the whole design rests on: a metadata editor that changes files
    /// just by looking at them is one nobody should run over a library.
    /// </summary>
    [Fact]
    public void Loading_and_editing_without_saving_leaves_the_file_alone()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("book.epub"));
        byte[] before = File.ReadAllBytes(path);

        Book book = Book.Load(path);
        book.Metadata.Title = "Something Else Entirely";

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Saving_writes_the_edit_and_keeps_a_backup_only_when_asked()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("book.epub"));
        byte[] before = File.ReadAllBytes(path);

        Book book = Book.Load(path);
        book.Metadata.Title = "Something Else Entirely";

        Assert.Null(book.Save(keepBackup: false));
        Assert.False(File.Exists(path + ".bak"));

        // Read back through a second load, so the assertion is about the file
        // rather than about the model that wrote it.
        Book reloaded = Book.Load(path);
        Assert.Equal("Something Else Entirely", reloaded.Metadata.Title);

        reloaded.Metadata.Title = "Third Title";
        Assert.Equal(path + ".bak", reloaded.Save(keepBackup: true));
        Assert.NotEqual(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Save_findings_are_separate_from_load_findings()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfo(CbzBuilder.MinimalComicInfo)
            .WriteTo(temp.File("comic.cbz"));

        Book book = Book.Load(path);

        Assert.Empty(book.SaveFindings);

        book.Save(keepBackup: false);

        Assert.Contains(book.SaveFindings, f => f.RuleId == "CBZ-E020");

        // A second save has nothing left to correct, because the first one fixed
        // it. That is what makes the correction a fix rather than a recurring
        // complaint.
        book.Save(keepBackup: false);

        Assert.DoesNotContain(book.SaveFindings, f => f.RuleId == "CBZ-E020");
    }
}
