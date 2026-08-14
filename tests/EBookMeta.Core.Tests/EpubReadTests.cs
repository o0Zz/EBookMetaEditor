using EBookMeta.Containers;
using EBookMeta.Documents;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Tests that EPUB 2 and EPUB 3 metadata conventions are both read.
/// </summary>
/// <remarks>
/// Files in the wild mix them freely: an EPUB 3 produced by a converter often
/// carries only <c>calibre:series</c>, and an EPUB 2 may carry EPUB 3 refinements a
/// later tool added. Reading only the version a file declares would silently lose
/// fields.
/// </remarks>
public sealed class EpubReadTests
{
    private static BookMetadata Read(string path)
    {
        using ZipContainer container = ZipContainer.Open(path);
        return new EpubFormat().Read(container);
    }

    private static BookMetadata Epub3(TempDir temp) =>
        Read(new EpubBuilder().WriteTo(temp.File("valid-epub3.epub")));

    private static BookMetadata Epub2(TempDir temp) =>
        Read(new EpubBuilder().WithOpf(EpubBuilder.Epub2Opf).WriteTo(temp.File("valid-epub2.epub")));

    [Fact]
    public void Epub3_refinements_supply_sort_names_roles_and_series()
    {
        using var temp = new TempDir();
        BookMetadata m = Epub3(temp);

        Assert.Equal("The Ocean at the End of the Lane", m.Title);
        Assert.Equal("Ocean at the End of the Lane, The", m.SortTitle);

        Creator author = m.Creators[0];
        Assert.Equal("Neil Gaiman", author.Name);
        Assert.Equal("Gaiman, Neil", author.SortName);
        Assert.Equal("aut", author.Role);
        Assert.Equal(CreatorKind.Creator, author.Kind);

        Assert.Equal(CreatorKind.Contributor, m.Creators[1].Kind);
        Assert.Equal("ill", m.Creators[1].NativeRole);

        // A half-numbered series entry is normal — a novella published as book 2.5.
        Assert.Equal("Sandman Universe", m.Series?.Name);
        Assert.Equal(2.5m, m.Series?.Index);
    }

    [Fact]
    public void Epub2_attributes_and_calibre_meta_supply_the_same_fields()
    {
        using var temp = new TempDir();
        BookMetadata m = Epub2(temp);

        Assert.Equal("Neverwhere", m.Title);
        Assert.Equal("Gaiman, Neil", m.Creators[0].SortName);
        Assert.Equal("aut", m.Creators[0].Role);
        Assert.Equal("London Below", m.Series?.Name);
        Assert.Equal(1m, m.Series?.Index);
    }

    [Fact]
    public void Unique_identifier_is_resolved_through_the_package_reference()
    {
        using var temp = new TempDir();
        BookMetadata m = Epub3(temp);

        Assert.Equal("urn:isbn:9781472200341", m.UniqueIdentifier?.Value);
        Assert.True(m.Identifiers.Single().IsUnique);
    }

    /// <summary>
    /// A bare year must not be promoted to 1 January, which would assert a
    /// publication day the publisher never claimed.
    /// </summary>
    [Theory]
    [InlineData("2013", DatePrecision.Year)]
    [InlineData("2013-05", DatePrecision.Month)]
    [InlineData("2013-05-03", DatePrecision.Day)]
    [InlineData("sometime in the 90s", DatePrecision.Unknown)]
    public void Date_precision_reflects_what_the_source_stated(string raw, DatePrecision expected)
    {
        BookDate date = OpfDocument.ParseDate(raw);

        Assert.Equal(expected, date.Precision);
        Assert.Equal(raw, date.Raw);
    }

    [Fact]
    public void Cover_is_found_through_either_convention()
    {
        using var temp = new TempDir();

        BookMetadata epub3 = Epub3(temp);
        Assert.NotNull(epub3.Cover);
        Assert.Equal("image/png", epub3.Cover!.MediaType);
        Assert.Equal("OEBPS/images/cover.png", epub3.Cover.SourceEntryName);
        Assert.Equal(PngBuilder.OnePixel, epub3.Cover.Data);

        Assert.Equal("cover-img", Epub2(temp).Cover?.SourceManifestId);
    }

    [Fact]
    public void Missing_cover_entry_does_not_break_reading()
    {
        using var temp = new TempDir();

        // A cover declaration pointing at a missing entry is EPUB-E030's business.
        BookMetadata m = Read(new EpubBuilder()
            .WithoutCoverImage()
            .WriteTo(temp.File("broken-epub-e030-missing-cover.epub")));

        Assert.Null(m.Cover);
        Assert.Equal("The Ocean at the End of the Lane", m.Title);
    }

    [Fact]
    public void Unrecognised_meta_is_reported_with_its_line_number()
    {
        using var temp = new TempDir();
        UnmappedField mood = Epub3(temp).UnmappedFields.Single(f => f.Key == "custom:mood");

        Assert.Equal("wistful", mood.Text);
        Assert.Equal("OPF", mood.Source);
        Assert.True(mood.Line > 0, "line info should survive parsing");
    }

    /// <summary>
    /// EPUB-F001 and EPUB-F002. Editing a document we cannot parse would risk
    /// destroying content, so reading refuses and the repair path takes over.
    /// </summary>
    [Fact]
    public void A_package_document_that_cannot_be_located_or_parsed_is_fatal()
    {
        using var temp = new TempDir();

        Assert.Throws<BookFormatException>(() => Read(new EpubBuilder()
            .WithOpf("<package><metadata><dc:title>unclosed")
            .WriteTo(temp.File("broken-unclosed-tag.epub"))));

        Assert.Throws<BookFormatException>(() => Read(new EpubBuilder()
            .WithContainerXml(null)
            .WriteTo(temp.File("broken-epub-f002-no-container.epub"))));

        Assert.Throws<BookFormatException>(() => Read(new EpubBuilder()
            .WithContainerXml("""
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/nowhere.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """)
            .WriteTo(temp.File("broken-epub-f002-dangling-rootfile.epub"))));
    }

    [Theory]
    [InlineData("OEBPS/content.opf", "images/cover.png", "OEBPS/images/cover.png")]
    [InlineData("OEBPS/content.opf", "my%20cover.png", "OEBPS/my cover.png")]
    [InlineData("OEBPS/content.opf", "text.xhtml#part2", "OEBPS/text.xhtml")]
    [InlineData("content.opf", "cover.png", "cover.png")]
    public void Hrefs_resolve_against_the_package_document_directory(
        string opfPath, string href, string expected) =>
        Assert.Equal(expected, EpubFormat.ResolveHref(opfPath, href));
}
