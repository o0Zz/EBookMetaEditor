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
        RawPackageDocument raw = EpubHandler.ReadRawPackageDocument(container);
        return new UTF8Encoding(false).GetString(raw.Bytes);
    }

    // --- opening ---------------------------------------------------------

    [Fact]
    public void ABrokenBookOpensAndReadsCorrectly()
    {
        using var temp = new TempDir();
        string path = BrokenEpub(temp);

        using ZipContainer container = ZipContainer.Open(path);
        BookMetadata metadata = new EpubHandler().Read(container);

        // The undeclared opf: prefix is corrected on the way in, so the metadata
        // it carried is available rather than the file being refused.
        Assert.Equal("Neverwhere", metadata.Title);
        Assert.Equal("Gaiman, Neil", Assert.Single(metadata.Creators).SortName);
    }

    [Fact]
    public void OpeningDoesNotTouchTheFileOnDisk()
    {
        using var temp = new TempDir();
        string path = BrokenEpub(temp);

        Dictionary<string, byte[]> before = ReadAllEntries(path);

        using (ZipContainer container = ZipContainer.Open(path))
        {
            new EpubHandler().Read(container);
        }

        // The correction is in memory only. Nothing is written, and no backup is
        // left behind, until the user saves.
        Assert.Equal(before, ReadAllEntries(path));
        Assert.DoesNotContain("xmlns:opf", OpfTextOf(path), StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void ADocumentBrokenBeyondRepairStillFailsToOpen()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithOpf(EpubBuilder.OpfUnknownPrefix)
            .WriteTo(temp.File("broken-unknown-prefix.epub"));

        using ZipContainer container = ZipContainer.Open(path);

        // 'acme' has no known namespace, so nothing is guessed and the original
        // error is what the user gets.
        BookFormatException ex = Assert.Throws<BookFormatException>(
            () => new EpubHandler().Read(container));

        Assert.Contains("acme", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadReportsWhatItCorrected()
    {
        using var temp = new TempDir();
        string path = BrokenEpub(temp);

        using ZipContainer container = ZipContainer.Open(path);
        var findings = new List<Finding>();

        // Reported by the read itself. There is no separate validate step: a repair
        // is something the load did, so the load is what says it happened.
        new EpubHandler().Read(container, findings: findings);

        Finding finding = Assert.Single(findings, f => f.RuleId == "EPUB-W070");

        Assert.True(finding.HasAutofix);
        Assert.Equal("OEBPS/content.opf", finding.Location);
    }

    /// <summary>
    /// A well-formed book produces nothing an editor has to act on.
    /// </summary>
    /// <remarks>
    /// Errors, not findings outright. A conformant EPUB 3 declares its cover and its
    /// series the EPUB 3 way and nothing else, which is exactly what EPUB-W032 and
    /// EPUB-W061 exist to point out — saving writes both conventions so older
    /// readers can see them too. Those are advisories about what a save will improve,
    /// so a fixture tripping them is right rather than broken.
    /// </remarks>
    [Fact]
    public void AValidBookReportsNothingWrong()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid.epub"));

        using ZipContainer container = ZipContainer.Open(path);
        var findings = new List<Finding>();

        new EpubHandler().Read(container, findings: findings);

        Assert.DoesNotContain(findings, f => f.Severity >= Severity.Error);
        Assert.DoesNotContain(findings, f => f.RuleId == "EPUB-W070");
    }

    // --- saving ----------------------------------------------------------

    [Fact]
    public void SavingPersistsTheCorrection()
    {
        using var temp = new TempDir();
        string source = BrokenEpub(temp);
        string target = temp.File("saved.epub");

        using (ZipContainer container = ZipContainer.Open(source))
        {
            var handler = new EpubHandler();
            BookMetadata metadata = handler.Read(container);
            handler.Write(container, metadata, target);
        }

        Assert.Contains(@"xmlns:opf=""http://www.idpf.org/2007/opf""", OpfTextOf(target), StringComparison.Ordinal);

        // And the saved file is a book that opens on its own terms, with no
        // repair needed the second time.
        using ZipContainer saved = ZipContainer.Open(target);
        Assert.Equal("Neverwhere", new EpubHandler().Read(saved).Title);
    }

    [Fact]
    public void SavingAnEditKeepsBothTheEditAndTheCorrection()
    {
        using var temp = new TempDir();
        string source = BrokenEpub(temp);
        string target = temp.File("edited.epub");

        using (ZipContainer container = ZipContainer.Open(source))
        {
            var handler = new EpubHandler();
            BookMetadata metadata = handler.Read(container);
            metadata.Title = "Neverwhere: Author's Preferred Text";
            handler.Write(container, metadata, target);
        }

        using ZipContainer saved = ZipContainer.Open(target);
        Assert.Equal("Neverwhere: Author's Preferred Text", new EpubHandler().Read(saved).Title);
        Assert.Contains("xmlns:opf", OpfTextOf(target), StringComparison.Ordinal);
    }

    /// <summary>
    /// Hard invariants 3 and 4: only the package document changes, and entry
    /// order is preserved.
    /// </summary>
    [Fact]
    public void SavingLeavesEveryOtherEntryByteForByte()
    {
        using var temp = new TempDir();
        string source = BrokenEpub(temp);
        string target = temp.File("saved.epub");

        Dictionary<string, byte[]> before = ReadAllEntries(source);
        List<string> orderBefore = ReadEntryOrder(source);

        using (ZipContainer container = ZipContainer.Open(source))
        {
            var handler = new EpubHandler();
            handler.Write(container, handler.Read(container), target);
        }

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
    /// The correction is a single insertion, so saving a repaired book must not
    /// reformat the rest of the package document.
    /// </summary>
    [Fact]
    public void SavingChangesOneLineOfThePackageDocument()
    {
        using var temp = new TempDir();
        string source = BrokenEpub(temp);
        string target = temp.File("saved.epub");

        using (ZipContainer container = ZipContainer.Open(source))
        {
            var handler = new EpubHandler();
            handler.Write(container, handler.Read(container), target);
        }

        string[] before = OpfTextOf(source).Split('\n');
        string[] after = OpfTextOf(target).Split('\n');

        Assert.Equal(before.Length, after.Length);

        int[] differing = Enumerable.Range(0, before.Length)
            .Where(i => before[i] != after[i])
            .ToArray();

        Assert.Single(differing);
        Assert.Contains("<package", after[differing[0]], StringComparison.Ordinal);
    }

    /// <summary>
    /// Hard invariant 7. A save that broke this would produce a file readers
    /// reject outright, which is worse than the problem it fixed.
    /// </summary>
    [Fact]
    public void SavingKeepsMimetypeFirstAndStored()
    {
        using var temp = new TempDir();
        string source = BrokenEpub(temp);
        string target = temp.File("saved.epub");

        using (ZipContainer container = ZipContainer.Open(source))
        {
            var handler = new EpubHandler();
            handler.Write(container, handler.Read(container), target);
        }

        using ZipContainer saved = ZipContainer.Open(target);
        ContainerEntry first = saved.Entries[0];

        Assert.Equal("mimetype", first.Name);
        Assert.Equal(0, first.CompressionMethod);

        using Stream stream = saved.OpenRead(first);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        Assert.Equal("application/epub+zip", reader.ReadToEnd());
    }

    /// <summary>
    /// Saving a repaired book goes through the ordinary atomic write, so an
    /// interrupted save cannot leave a truncated book and the original is kept.
    /// </summary>
    [Fact]
    public void SavingInPlaceIsAtomicAndKeepsABackup()
    {
        using var temp = new TempDir();
        string path = BrokenEpub(temp);

        AtomicFileWriter.Write(
            path,
            target =>
            {
                using ZipContainer container = ZipContainer.Open(path);
                var handler = new EpubHandler();
                handler.Write(container, handler.Read(container), target);
            },
            keepBackup: true);

        Assert.True(File.Exists(path + ".bak"));
        Assert.Contains("xmlns:opf", OpfTextOf(path), StringComparison.Ordinal);

        // The backup is the file as it was: still missing the declaration.
        Assert.DoesNotContain("xmlns:opf", OpfTextOf(path + ".bak"), StringComparison.Ordinal);
    }

    // --- unaffected files ------------------------------------------------

    /// <summary>
    /// Hard invariant 6. Repair-on-open must not disturb a file that needs none.
    /// </summary>
    [Fact]
    public void AValidBookStillRoundTripsByteIdentically()
    {
        using var temp = new TempDir();
        string source = new EpubBuilder().WriteTo(temp.File("valid.epub"));
        string target = temp.File("round-tripped.epub");

        using (ZipContainer container = ZipContainer.Open(source))
        {
            var handler = new EpubHandler();
            handler.Write(container, handler.Read(container), target);
        }

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }
}
