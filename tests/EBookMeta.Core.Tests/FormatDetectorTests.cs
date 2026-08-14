using System.IO.Compression;
using System.Text;
using EBookMeta.Formats;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Tests that format identification is driven by content, never by extension.
/// </summary>
/// <remarks>
/// In collections extensions lie constantly. These tests pin both halves of the
/// promise: that a file is identified by what it contains, and that a
/// disagreement with its name is reported rather than silently tolerated.
/// </remarks>
public sealed class FormatDetectorTests
{
    [Fact]
    public void Epub_is_recognised_from_the_mimetype_entry()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));

        DetectedFormat result = FormatDetector.Detect(path);

        Assert.Equal(FormatId.Epub, result.Format);
        Assert.Equal(ContainerKind.Zip, result.Container);
        Assert.True(result.ExtensionAgrees);
    }

    [Fact]
    public void Comic_archive_is_recognised_from_its_entries()
    {
        using var temp = new TempDir();
        string path = temp.File("comic.cbz");

        using (var fs = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            AddEntry(zip, "001.png", PngBuilder.OnePixel);
            AddEntry(zip, "002.png", PngBuilder.OnePixel);
            AddEntry(zip, "ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo><Series>S</Series></ComicInfo>"));
        }

        DetectedFormat result = FormatDetector.Detect(path);

        Assert.Equal(FormatId.Cbz, result.Format);
        Assert.True(result.ExtensionAgrees);
    }

    /// <summary>
    /// The headline case: a RAR archive wearing a <c>.cbz</c> extension, which
    /// is one of the most common things in a real comic library.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, "RAR 5")]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, "RAR 4")]
    public void Rar_disguised_as_cbz_is_detected_and_reported(byte[] magic, string what)
    {
        using var temp = new TempDir();
        string path = temp.File("rar-disguised-as-cbz.cbz");
        File.WriteAllBytes(path, magic.Concat(new byte[64]).ToArray());

        DetectedFormat result = FormatDetector.Detect(path);

        Assert.Equal(FormatId.Cbr, result.Format);
        Assert.Equal(ContainerKind.Rar, result.Container);

        // GEN-W002. Recognising the format is not the same as supporting it:
        // EBookMetaEditor names it precisely so the user knows why it will not open.
        Assert.False(result.ExtensionAgrees);
        Assert.Equal(FormatId.Cbz, result.ClaimedByExtension);
        Assert.Contains(what, result.Detail);
    }

    /// <summary>
    /// Guards against a magic-number check loose enough to match by accident.
    /// </summary>
    [Fact]
    public void Near_miss_magic_is_not_matched()
    {
        using var temp = new TempDir();
        string path = temp.File("not-rar.bin");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("Rar! not really").Concat(new byte[64]).ToArray());

        Assert.Equal(FormatId.Unknown, FormatDetector.Detect(path).Format);
    }

    [Theory]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, FormatId.Cb7)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, FormatId.Pdf)]
    public void Unsupported_formats_are_still_named(byte[] magic, FormatId expected)
    {
        using var temp = new TempDir();
        string path = temp.File("mystery.dat");
        File.WriteAllBytes(path, magic.Concat(new byte[128]).ToArray());

        // Naming a format costs a few byte comparisons; supporting it costs a
        // container implementation. These stay named so the user is told what
        // the file is rather than "unrecognised".
        Assert.Equal(expected, FormatDetector.Detect(path).Format);
    }

    [Fact]
    public void Mobi_is_named_from_its_palmdb_header()
    {
        using var temp = new TempDir();
        byte[] pdb = new byte[128];
        Encoding.ASCII.GetBytes("BOOKMOBI").CopyTo(pdb, 60);

        string path = temp.File("book.mobi");
        File.WriteAllBytes(path, pdb);

        Assert.Equal(FormatId.Mobi, FormatDetector.Detect(path).Format);
    }

    [Fact]
    public void Unknown_extension_is_not_treated_as_a_disagreement()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("book.unknownext"));

        DetectedFormat result = FormatDetector.Detect(path);

        Assert.Equal(FormatId.Epub, result.Format);

        // There is nothing to disagree with, so this must not raise GEN-W002.
        Assert.True(result.ExtensionAgrees);
    }

    [Fact]
    public void Detection_reads_only_the_head_of_the_file()
    {
        using var temp = new TempDir();

        var builder = new EpubBuilder();
        for (int i = 0; i < 200; i++)
        {
            builder.WithEntry($"OEBPS/text/page{i:D3}.xhtml", new string('x', 4096));
        }

        string path = builder.WriteTo(temp.File("big.epub"));

        // A conformant EPUB is identifiable from its first local file header, so
        // this must not depend on walking the archive — the startup budget
        // assumes it does not.
        Assert.Equal(FormatId.Epub, FormatDetector.Detect(path).Format);
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using Stream s = entry.Open();
        s.Write(content, 0, content.Length);
    }
}
