using System.Text;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Covers the repair-on-open behaviour: a recoverable package document opens as
/// though it were correct, and the correction reaches the disk only on save.
/// </summary>
public sealed class RepairWriteTests
{
    private static string BrokenEpub(TempDir temp) =>
        new EpubBuilder()
            .WithOpf(EpubBuilder.Epub2OpfUndeclaredOpfPrefix)
            .WriteTo(temp.File("broken-epub-w070-undeclared-prefix.epub"));

    private static Dictionary<string, byte[]> ReadAllEntries(string path)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        using ZipContainer container = ZipContainer.Open(path);
        foreach (ContainerEntry entry in container.Entries)
        {
            using Stream stream = container.OpenRead(entry);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result[entry.Name] = buffer.ToArray();
        }

        return result;
    }

    private static List<string> ReadEntryOrder(string path)
    {
        using ZipContainer container = ZipContainer.Open(path);
        return container.Entries.Select(e => e.Name).ToList();
    }

    private static string OpfTextOf(string path)
    {
        using ZipContainer container = ZipContainer.Open(path);
        return new UTF8Encoding(false).GetString(EpubFormat.ReadRawPackageDocument(container).Bytes);
    }

    private static void SaveTo(string source, string target, Action<BookMetadata>? edit = null)
    {
        using ZipContainer container = ZipContainer.Open(source);
        var format = new EpubFormat();
        BookMetadata metadata = format.Read(container);
        edit?.Invoke(metadata);
        format.Write(container, metadata, target);
    }

    // --- opening ---------------------------------------------------------

    [Fact]
    public void A_broken_book_opens_and_reads_correctly()
    {
        using var temp = new TempDir();
        string path = BrokenEpub(temp);

        using ZipContainer container = ZipContainer.Open(path);
        BookMetadata metadata = new EpubFormat().Read(container);

        // The undeclared opf: prefix is corrected on the way in, so the metadata it
        // carried is available rather than the file being refused.
        Assert.Equal("Neverwhere", metadata.Title);
        Assert.Equal("Gaiman, Neil", Assert.Single(metadata.Creators).SortName);
    }

    [Fact]
    public void Opening_does_not_touch_the_file_on_disk()
    {
        using var temp = new TempDir();
        string path = BrokenEpub(temp);

        Dictionary<string, byte[]> before = ReadAllEntries(path);

        using (ZipContainer container = ZipContainer.Open(path))
        {
            new EpubFormat().Read(container);
        }

        // The correction is in memory only. Nothing is written, and no backup is
        // left behind, until the user saves.
        Assert.Equal(before, ReadAllEntries(path));
        Assert.DoesNotContain("xmlns:opf", OpfTextOf(path), StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void A_document_broken_beyond_repair_still_fails_to_open()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithOpf(EpubBuilder.OpfUnknownPrefix)
            .WriteTo(temp.File("broken-unknown-prefix.epub"));

        using ZipContainer container = ZipContainer.Open(path);

        // 'acme' has no known namespace, so nothing is guessed and the original
        // error is what the user gets.
        BookFormatException ex = Assert.Throws<BookFormatException>(
            () => new EpubFormat().Read(container));

        Assert.Contains("acme", ex.Message, StringComparison.Ordinal);
    }

    // --- saving ----------------------------------------------------------

    [Fact]
    public void Saving_persists_the_correction_alongside_any_edit()
    {
        using var temp = new TempDir();
        string source = BrokenEpub(temp);
        string target = temp.File("saved.epub");

        SaveTo(source, target, m => m.Title = "Neverwhere: Author's Preferred Text");

        Assert.Contains(@"xmlns:opf=""http://www.idpf.org/2007/opf""", OpfTextOf(target), StringComparison.Ordinal);

        // The saved file is a book that opens on its own terms, with no repair
        // needed the second time.
        using ZipContainer saved = ZipContainer.Open(target);
        Assert.Equal("Neverwhere: Author's Preferred Text", new EpubFormat().Read(saved).Title);
    }

    /// <summary>Hard invariants 3 and 4: only the package document changes.</summary>
    [Fact]
    public void Saving_leaves_every_other_entry_byte_for_byte()
    {
        using var temp = new TempDir();
        string source = BrokenEpub(temp);
        string target = temp.File("saved.epub");

        Dictionary<string, byte[]> before = ReadAllEntries(source);
        List<string> orderBefore = ReadEntryOrder(source);

        SaveTo(source, target);

        Dictionary<string, byte[]> after = ReadAllEntries(target);

        Assert.Equal(orderBefore, ReadEntryOrder(target));
        Assert.Equal(before.Count, after.Count);

        foreach (KeyValuePair<string, byte[]> entry in before)
        {
            if (entry.Key.EndsWith(".opf", StringComparison.Ordinal))
            {
                Assert.NotEqual(entry.Value, after[entry.Key]);
                continue;
            }

            Assert.Equal(entry.Value, after[entry.Key]);
        }
    }

    /// <summary>
    /// Saving a repaired book goes through the ordinary atomic write, so an
    /// interrupted save cannot leave a truncated book and the original is kept.
    /// </summary>
    [Fact]
    public void Saving_in_place_is_atomic_and_keeps_a_backup()
    {
        using var temp = new TempDir();
        string path = BrokenEpub(temp);

        AtomicFileWriter.Write(path, target => SaveTo(path, target), keepBackup: true);

        Assert.True(File.Exists(path + ".bak"));
        Assert.Contains("xmlns:opf", OpfTextOf(path), StringComparison.Ordinal);

        // The backup is the file as it was: still missing the declaration.
        Assert.DoesNotContain("xmlns:opf", OpfTextOf(path + ".bak"), StringComparison.Ordinal);
    }
}
