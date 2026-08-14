using System.Text;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

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
    /// <summary>
    /// Everything opening the file noticed, whether or not it opened.
    /// </summary>
    /// <remarks>
    /// There is no validate call to make: the rules run as part of the load, so this
    /// is a load. The fatal case is a document that cannot be parsed at all, which
    /// throws — and the finding that says so is published on the way out, which is
    /// why the sink is read after the catch rather than the return value.
    /// </remarks>
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
        string path = new CbzBuilder()
            .WithComicInfo("<ComicInfo><Series>Unclosed</ComicInfo>")
            .WriteTo(temp.File("broken-cbz-f001-malformed.cbz"));

        Assert.Equal(["CBZ-F001"], Rules(path));

        Finding finding = Single(path, "CBZ-F001");
        Assert.Equal(Severity.Fatal, finding.Severity);
        Assert.Equal("ComicInfo.xml", finding.Location);

        // Fatal means the open fails, not that it returns a book with a complaint
        // attached. There is nothing to recover: no declaration is missing, and
        // invariant 15 forbids guessing further.
        using var container = ZipContainer.Open(path);
        Assert.Throws<BookFormatException>(() => new CbzHandler().Read(container));
    }

    /// <summary>
    /// Not a defect: most comics have never been tagged. The finding exists to say
    /// what saving will do.
    /// </summary>
    [Fact]
    public void Cbz_w010_no_metadata_document_at_all()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder().WithoutComicInfo().WriteTo(temp.File("untagged.cbz"));

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
        string path = new CbzBuilder().WithCoMet().WriteTo(temp.File("broken-cbz-w012-comet.cbz"));

        Assert.Equal(["CBZ-W012"], Rules(path));
        Assert.Contains("comet.xml", Single(path, "CBZ-W012").Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ZIP comment counts as a convention too, and additionally blocks saving.
    /// </summary>
    [Fact]
    public void Cbz_w012_counts_a_comicbooklover_blob()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder().WriteTo(temp.File("broken-cbz-w012-cbl.cbz"));
        CbzBuilder.AddArchiveComment(path, "{\"appID\":\"ComicBookLover\"}");

        Assert.Equal(["CBZ-W012"], Rules(path));
        Assert.Contains("ZIP comment", Single(path, "CBZ-W012").Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Cbz_e020_page_count_disagrees_with_the_images_present()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfo("""
                <?xml version="1.0" encoding="utf-8"?>
                <ComicInfo>
                  <Series>The Sandman</Series>
                  <PageCount>5</PageCount>
                </ComicInfo>
                """)
            .WriteTo(temp.File("broken-cbz-e020-pagecount.cbz"));

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
        string path = new CbzBuilder()
            .WithComicInfo("""
                <?xml version="1.0" encoding="utf-8"?>
                <ComicInfo>
                  <Series>The Sandman</Series>
                  <PageCount>3</PageCount>
                  <Pages>
                    <Page Image="0" Type="FrontCover"/>
                    <Page Image="1"/>
                  </Pages>
                </ComicInfo>
                """)
            .WriteTo(temp.File("broken-cbz-w021-pages.cbz"));

        Assert.Equal(["CBZ-W021"], Rules(path));
    }

    /// <summary>
    /// The classic collection defect: unpadded numbers, so a reader sorting by
    /// name shows page 10 before page 2.
    /// </summary>
    [Fact]
    public void Cbz_w022_page_names_that_do_not_sort_into_reading_order()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithPages("1.png", "2.png", "10.png")
            .WriteTo(temp.File("broken-cbz-w022-order.cbz"));

        Assert.Equal(["CBZ-W022"], Rules(path));
    }

    [Fact]
    public void Padded_page_names_sort_cleanly()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithPages("001.png", "002.png", "010.png")
            .WriteTo(temp.File("padded.cbz"));

        Assert.Empty(Rules(path));
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
        string path = new CbzBuilder()
            .WithComicInfo("<ComicInfo><Number>3</Number></ComicInfo>")
            .WriteTo(temp.File("broken-cbz-w030-number.cbz"));

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
        string path = new CbzBuilder()
            .WithComicInfo($"<ComicInfo><Series>S</Series>{dateElements}</ComicInfo>")
            .WriteTo(temp.File("broken-cbz-w031-date.cbz"));

        Assert.Equal(["CBZ-W031"], Rules(path));
        Assert.Equal(expectedDetail, Single(path, "CBZ-W031").Detail);
    }

    [Theory]
    [InlineData("<Year>1989</Year><Month>2</Month><Day>28</Day>")]
    [InlineData("<Year>1988</Year><Month>2</Month><Day>29</Day>")]
    [InlineData("<Year>1989</Year><Month>12</Month>")]
    [InlineData("<Year>1989</Year>")]
    public void A_real_date_is_not_reported(string dateElements)
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfo($"<ComicInfo><Series>S</Series>{dateElements}</ComicInfo>")
            .WriteTo(temp.File("dated.cbz"));

        Assert.Empty(Rules(path));
    }

    [Theory]
    [InlineData("english")]
    [InlineData("en-US")]
    [InlineData("zz")]
    public void Cbz_w032_a_language_that_is_not_an_iso_639_1_code(string language)
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfo($"<ComicInfo><Series>S</Series><LanguageISO>{language}</LanguageISO></ComicInfo>")
            .WriteTo(temp.File("broken-cbz-w032-language.cbz"));

        Assert.Equal(["CBZ-W032"], Rules(path));
        Assert.Equal(language, Single(path, "CBZ-W032").Detail);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("ja")]
    public void A_valid_language_code_is_not_reported(string language)
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfo($"<ComicInfo><Series>S</Series><LanguageISO>{language}</LanguageISO></ComicInfo>")
            .WriteTo(temp.File("localised.cbz"));

        Assert.Empty(Rules(path));
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
