using System.IO.Compression;
using System.Text;
using EBookMeta.Formats;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Tests that a file is identified by offering it to every registered format and
/// taking the strongest claim — never by its extension, because in collections
/// extensions lie constantly and a disagreement with the name has to be reported
/// rather than silently tolerated.
/// </summary>
public sealed class DetectionTests
{
    [Fact]
    public void Epub_is_recognised_from_the_mimetype_entry()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));

        DetectedFormat result = BookFormats.Identify(path);

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

        DetectedFormat result = BookFormats.Identify(path);

        Assert.Equal(FormatId.Cbz, result.Format);
        Assert.True(result.ExtensionAgrees);
    }

    /// <summary>
    /// The headline case: a RAR archive wearing a <c>.cbz</c> extension, which is
    /// one of the most common things in a real comic library.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, "RAR 5")]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, "RAR 4")]
    public void Rar_disguised_as_cbz_is_detected_and_reported(byte[] magic, string what)
    {
        using var temp = new TempDir();
        string path = temp.File("rar-disguised-as-cbz.cbz");
        File.WriteAllBytes(path, magic.Concat(new byte[64]).ToArray());

        DetectedFormat result = BookFormats.Identify(path);

        Assert.Equal(FormatId.Cbr, result.Format);
        Assert.Equal(ContainerKind.Rar, result.Container);

        // GEN-W002. Naming the format precisely is what tells the user why it will
        // not open.
        Assert.False(result.ExtensionAgrees);
        Assert.Equal(FormatId.Cbz, result.ClaimedByExtension);
        Assert.Contains(what, result.Detail);
    }

    [Fact]
    public void Near_miss_magic_is_not_matched()
    {
        using var temp = new TempDir();
        string path = temp.File("not-rar.bin");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("Rar! not really").Concat(new byte[64]).ToArray());

        Assert.Equal(FormatId.Unknown, BookFormats.Identify(path).Format);
    }

    /// <summary>
    /// Naming a format costs a few byte comparisons; supporting it costs a container
    /// implementation. These stay named so the user is told what the file is rather
    /// than "unrecognised".
    /// </summary>
    [Fact]
    public void Unsupported_formats_are_still_named()
    {
        using var temp = new TempDir();

        Assert.Equal(FormatId.Cb7, Detect(temp, "7z.dat", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]));
        Assert.Equal(FormatId.Pdf, Detect(temp, "doc.dat", [0x25, 0x50, 0x44, 0x46, 0x2D]));

        // MOBI is named from its PalmDB type+creator at offset 60.
        byte[] pdb = new byte[128];
        Encoding.ASCII.GetBytes("BOOKMOBI").CopyTo(pdb, 60);
        string mobi = temp.File("book.mobi");
        File.WriteAllBytes(mobi, pdb);

        Assert.Equal(FormatId.Mobi, BookFormats.Identify(mobi).Format);
    }

    [Fact]
    public void Unknown_extension_is_not_treated_as_a_disagreement()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("book.unknownext"));

        DetectedFormat result = BookFormats.Identify(path);

        Assert.Equal(FormatId.Epub, result.Format);

        // There is nothing to disagree with, so this must not raise GEN-W002.
        Assert.True(result.ExtensionAgrees);
    }

    /// <summary>
    /// The startup budget depends on identification staying structural. Asking
    /// every format walks the entry list, which the central directory has already
    /// supplied, and reads exactly one entry — the EPUB <c>mimetype</c>, whose
    /// content has to be checked. A 200-page archive must therefore cost what a
    /// three-page one costs.
    /// </summary>
    /// <remarks>
    /// This asserts the answer; the no-decompression half of the property is held
    /// by <c>CbzValidateTests.Validating_a_long_comic_reads_only_the_metadata_document</c>,
    /// which runs the whole load path over a 300-page comic.
    /// </remarks>
    [Fact]
    public void A_large_archive_is_identified_from_its_structure()
    {
        using var temp = new TempDir();

        var builder = new EpubBuilder();
        for (int i = 0; i < 200; i++)
        {
            builder.WithEntry($"OEBPS/text/page{i:D3}.xhtml", new string('x', 4096));
        }

        string path = builder.WriteTo(temp.File("big.epub"));

        DetectedFormat result = BookFormats.Identify(path);

        Assert.Equal(FormatId.Epub, result.Format);
        Assert.Contains("mimetype", result.Detail);
    }

    /// <summary>
    /// A ZIP with no <c>mimetype</c>, no <c>ComicInfo.xml</c> and no images is
    /// indistinguishable from any other ZIP by content — so every format declines
    /// it, and the honest answer is that this build cannot edit it.
    /// </summary>
    [Fact]
    public void A_zip_no_format_claims_is_refused()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithoutMimetype()
            .WriteTo(temp.File("no-mimetype.epub"));

        Assert.Throws<UnsupportedFormatException>(() => Book.Load(path));
        Assert.Equal(FormatId.UnknownZip, BookFormats.Identify(path).Format);
    }

    /// <summary>
    /// A 300-page comic must cost what a three-page one costs: every rule works
    /// from entry names and the one metadata document, so nothing here decompresses
    /// a page.
    /// </summary>
    [Fact]
    public void A_long_comic_opens_without_reading_its_pages()
    {
        using var temp = new TempDir();
        string[] pages = [.. Enumerable.Range(1, 300).Select(i => $"{i:000}.png")];

        string path = new CbzBuilder()
            .WithPages(pages)
            .WithComicInfo("<ComicInfo><Series>S</Series><PageCount>300</PageCount></ComicInfo>")
            .WriteTo(temp.File("long.cbz"));

        Book book = Book.Load(path, ReadOptions.WithoutCover);

        Assert.Equal("S", book.Metadata.Series?.Name);
        Assert.Equal(301, book.EntryCount);
    }

    /// <summary>
    /// A format must decline another format's file rather than throw, because an
    /// exception would abandon the loop before the formats after it were asked.
    /// </summary>
    [Fact]
    public void Every_format_declines_the_others_files()
    {
        using var temp = new TempDir();

        (string Path, FormatId Expected)[] files =
        [
            (new EpubBuilder().WriteTo(temp.File("book.epub")), FormatId.Epub),
            (new CbzBuilder().WriteTo(temp.File("comic.cbz")), FormatId.Cbz),
            (new CbzBuilder().WriteTo(temp.File("comic.cbt"), ContainerKind.Tar), FormatId.Cbt),
            (new Fb2Builder().WriteTo(temp.File("book.fb2")), FormatId.Fb2),
            (new MobiBuilder().WriteTo(temp.File("book.mobi")), FormatId.Mobi),
        ];

        foreach ((string path, FormatId expected) in files)
        {
            using BookSource source = BookSource.Open(path);

            List<FormatId> claimants = [.. BookFormats.All
                .Select(f => f.TryOpen(source))
                .Where(c => c is not null)
                .Select(c => c!.Format)];

            Assert.Contains(expected, claimants);

            // Overlaps are allowed — an FB2.ZIP is also an archive of files — but
            // the winner must be the right one.
            Assert.Equal(expected, BookFormats.Identify(path).Format);
        }
    }

    /// <summary>
    /// Confidence, not registration order, decides an overlap. A comic's
    /// <c>ComicInfo.xml</c> inside an EPUB must not outrank the EPUB's own marker.
    /// </summary>
    [Fact]
    public void A_stronger_claim_wins_over_a_weaker_one()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithEntry("ComicInfo.xml", "<ComicInfo><Series>S</Series></ComicInfo>")
            .WriteTo(temp.File("confusing.epub"));

        DetectedFormat result = BookFormats.Identify(path);

        Assert.Equal(FormatId.Epub, result.Format);
        Assert.Contains("mimetype", result.Detail);
    }

    /// <summary>
    /// The reason TryOpen claims rather than parses. This EPUB's OPF will not parse
    /// until it is repaired; if EpubFormat declined it, nothing else would claim it
    /// and the user would be told their book is unsupported instead of having it
    /// fixed.
    /// </summary>
    [Fact]
    public void A_damaged_file_is_still_claimed_by_its_own_format()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithOpf(EpubBuilder.Epub2OpfUndeclaredOpfPrefix)
            .WriteTo(temp.File("broken-epub-w070.epub"));

        Assert.Equal(FormatId.Epub, BookFormats.Identify(path).Format);

        // The repair runs because EpubFormat claimed the file despite the damage.
        Book book = Book.Load(path, ReadOptions.WithoutCover);

        Assert.Equal("Neverwhere", book.Metadata.Title);
        Assert.Equal("Gaiman, Neil", Assert.Single(book.Metadata.Creators).SortName);
    }

    private static FormatId Detect(TempDir temp, string name, byte[] magic)
    {
        string path = temp.File(name);
        File.WriteAllBytes(path, magic.Concat(new byte[128]).ToArray());
        return BookFormats.Identify(path).Format;
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using Stream s = entry.Open();
        s.Write(content, 0, content.Length);
    }
}
