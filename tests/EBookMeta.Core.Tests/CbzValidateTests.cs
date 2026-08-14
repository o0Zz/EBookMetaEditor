using System.Text;
using EBookMeta.Containers;
using EBookMeta.Formats;
using Xunit;
using EBookMeta.Tests.Builders;

namespace EBookMeta.Tests;

/// <summary>
/// One fixture per comic validation rule, each triggering its rule in isolation.
/// </summary>
/// <remarks>
/// Asserting the whole set of rule IDs rather than just the expected one is what
/// makes "in isolation" testable: a fixture that quietly trips a second rule is a
/// fixture that will stop proving what it claims to.
/// </remarks>
public sealed class CbzValidateTests
{
    private static List<Finding> Load(string path)
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

    private static string[] Rules(string path) =>
        [.. Load(path).Select(f => f.RuleId).OrderBy(id => id, StringComparer.Ordinal)];

    private static Finding Single(string path, string ruleId) =>
        Load(path).Single(f => f.RuleId == ruleId);

    private static string Comic(TempDir temp, string name, string comicInfo) =>
        new CbzBuilder().WithComicInfo(comicInfo).WriteTo(temp.File(name));

    [Fact]
    public void A_valid_archive_has_no_findings()
    {
        using var temp = new TempDir();
        Assert.Empty(Rules(new CbzBuilder().WriteTo(temp.File("valid.cbz"))));
    }

    [Fact]
    public void Cbz_f001_a_metadata_document_that_is_not_well_formed()
    {
        using var temp = new TempDir();
        string path = Comic(
            temp, "broken-cbz-f001-malformed.cbz", "<ComicInfo><Series>Unclosed</ComicInfo>");

        Assert.Equal(["CBZ-F001"], Rules(path));

        Finding finding = Single(path, "CBZ-F001");
        Assert.Equal(Severity.Fatal, finding.Severity);
        Assert.Equal("ComicInfo.xml", finding.Location);

        // Fatal means the open fails, not that it returns a book with a complaint
        // attached. Nothing is recoverable here, and invariant 15 forbids guessing.
        using var container = ZipContainer.Open(path);
        Assert.Throws<BookFormatException>(() => new CbzFormat().Read(container));
    }

    [Fact]
    public void Cbz_w010_no_metadata_document_at_all()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder().WithoutComicInfo().WriteTo(temp.File("untagged.cbz"));

        // Not a defect: most comics have never been tagged. The finding exists to
        // say what saving will do.
        Assert.Equal(["CBZ-W010"], Rules(path));
        Assert.Equal(Severity.Warning, Single(path, "CBZ-W010").Severity);
    }

    [Fact]
    public void Cbz_e011_metadata_document_below_the_root()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfoAt("meta/ComicInfo.xml")
            .WriteTo(temp.File("broken-cbz-e011-nested.cbz"));

        Assert.Equal(["CBZ-E011"], Rules(path));

        Finding finding = Single(path, "CBZ-E011");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("meta/ComicInfo.xml", finding.Location);
    }

    [Fact]
    public void Cbz_w012_more_than_one_metadata_convention()
    {
        using var temp = new TempDir();

        string comet = new CbzBuilder().WithCoMet().WriteTo(temp.File("broken-cbz-w012-comet.cbz"));

        Assert.Equal(["CBZ-W012"], Rules(comet));
        Assert.Contains("comet.xml", Single(comet, "CBZ-W012").Message, StringComparison.Ordinal);

        // The ZIP comment counts as a convention too, and additionally blocks saving.
        string blob = new CbzBuilder().WriteTo(temp.File("broken-cbz-w012-cbl.cbz"));
        CbzBuilder.AddArchiveComment(blob, "{\"appID\":\"ComicBookLover\"}");

        Assert.Equal(["CBZ-W012"], Rules(blob));
        Assert.Contains("ZIP comment", Single(blob, "CBZ-W012").Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Cbz_e020_page_count_disagrees_with_the_images_present()
    {
        using var temp = new TempDir();
        string path = Comic(
            temp,
            "broken-cbz-e020-pagecount.cbz",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ComicInfo>
              <Series>The Sandman</Series>
              <PageCount>5</PageCount>
            </ComicInfo>
            """);

        Assert.Equal(["CBZ-E020"], Rules(path));

        Finding finding = Single(path, "CBZ-E020");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("5", finding.Message, StringComparison.Ordinal);
        Assert.Contains("3 images", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cbz_w021_the_pages_block_does_not_match_the_archive()
    {
        using var temp = new TempDir();
        string path = Comic(
            temp,
            "broken-cbz-w021-pages.cbz",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ComicInfo>
              <Series>The Sandman</Series>
              <PageCount>3</PageCount>
              <Pages>
                <Page Image="0" Type="FrontCover"/>
                <Page Image="1"/>
              </Pages>
            </ComicInfo>
            """);

        Assert.Equal(["CBZ-W021"], Rules(path));
    }

    [Fact]
    public void Cbz_w022_page_names_that_do_not_sort_into_reading_order()
    {
        using var temp = new TempDir();

        // The classic collection defect: unpadded numbers, so a reader sorting by
        // name shows page 10 before page 2.
        Assert.Equal(
            ["CBZ-W022"],
            Rules(new CbzBuilder()
                .WithPages("1.png", "2.png", "10.png")
                .WriteTo(temp.File("broken-cbz-w022-order.cbz"))));

        Assert.Empty(Rules(new CbzBuilder()
            .WithPages("001.png", "002.png", "010.png")
            .WriteTo(temp.File("padded.cbz"))));
    }

    [Fact]
    public void Cbz_w023_entries_that_are_neither_images_nor_metadata()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithEntry("Thumbs.db", Encoding.UTF8.GetBytes("junk"))
            .WriteTo(temp.File("broken-cbz-w023-extras.cbz"));

        Assert.Equal(["CBZ-W023"], Rules(path));
        Assert.Equal("Thumbs.db", Single(path, "CBZ-W023").Detail);
    }

    [Fact]
    public void Cbz_w030_an_issue_number_with_no_series()
    {
        using var temp = new TempDir();
        string path = Comic(temp, "broken-cbz-w030-number.cbz", "<ComicInfo><Number>3</Number></ComicInfo>");

        Assert.Equal(["CBZ-W030"], Rules(path));
        Assert.Equal("3", Single(path, "CBZ-W030").Detail);
    }

    [Theory]
    [InlineData("<Year>1989</Year><Month>13</Month>", "1989-13")]
    [InlineData("<Year>1989</Year><Month>2</Month><Day>31</Day>", "1989-2-31")]
    [InlineData("<Year>circa</Year>", "circa")]
    public void Cbz_w031_an_impossible_date(string dateElements, string expectedDetail)
    {
        using var temp = new TempDir();
        string path = Comic(
            temp, "broken-cbz-w031-date.cbz", $"<ComicInfo><Series>S</Series>{dateElements}</ComicInfo>");

        Assert.Equal(["CBZ-W031"], Rules(path));
        Assert.Equal(expectedDetail, Single(path, "CBZ-W031").Detail);
    }

    [Theory]
    [InlineData("<Year>1988</Year><Month>2</Month><Day>29</Day>")]
    [InlineData("<Year>1989</Year><Month>12</Month>")]
    public void A_real_date_is_not_reported(string dateElements)
    {
        using var temp = new TempDir();
        Assert.Empty(Rules(Comic(
            temp, "dated.cbz", $"<ComicInfo><Series>S</Series>{dateElements}</ComicInfo>")));
    }

    [Theory]
    [InlineData("english", true)]
    [InlineData("en-US", true)]
    [InlineData("zz", true)]
    [InlineData("en", false)]
    [InlineData("ja", false)]
    public void Cbz_w032_a_language_that_is_not_an_iso_639_1_code(string language, bool reported)
    {
        using var temp = new TempDir();
        string path = Comic(
            temp,
            "broken-cbz-w032-language.cbz",
            $"<ComicInfo><Series>S</Series><LanguageISO>{language}</LanguageISO></ComicInfo>");

        if (!reported)
        {
            Assert.Empty(Rules(path));
            return;
        }

        Assert.Equal(["CBZ-W032"], Rules(path));
        Assert.Equal(language, Single(path, "CBZ-W032").Detail);
    }

    /// <summary>
    /// Validation of a 300-page comic must not decompress anything: every rule
    /// here works from entry names and the one metadata document.
    /// </summary>
    [Fact]
    public void Validating_a_long_comic_reads_only_the_metadata_document()
    {
        using var temp = new TempDir();
        string[] pages = [.. Enumerable.Range(1, 300).Select(i => $"{i:000}.png")];

        string path = new CbzBuilder()
            .WithPages(pages)
            .WithComicInfo("<ComicInfo><Series>S</Series><PageCount>300</PageCount></ComicInfo>")
            .WriteTo(temp.File("long.cbz"));

        Assert.Empty(Rules(path));
    }
}
