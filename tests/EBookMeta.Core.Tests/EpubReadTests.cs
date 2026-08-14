using EBookMeta.Containers;
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
/// carries only <c>calibre:series</c>, and an EPUB 2 may carry EPUB 3
/// refinements a later tool added. Reading only the version a file declares
/// would silently lose fields.
/// </remarks>
public sealed class EpubReadTests
{
    private static BookMetadata Read(string path)
    {
        using ZipContainer container = ZipContainer.Open(path);
        return new EpubHandler().Read(container);
    }

    [Fact]
    public void Epub3_refinements_supply_sort_names_and_roles()
    {
        using var temp = new TempDir();
        BookMetadata m = Read(new EpubBuilder().WriteTo(temp.File("valid-epub3.epub")));

        Assert.Equal("The Ocean at the End of the Lane", m.Title);
        Assert.Equal("Ocean at the End of the Lane, The", m.SortTitle);

        Creator author = m.Creators[0];
        Assert.Equal("Neil Gaiman", author.Name);
        Assert.Equal("Gaiman, Neil", author.SortName);
        Assert.Equal("aut", author.Role);
        Assert.Equal(CreatorKind.Creator, author.Kind);

        Assert.Equal(CreatorKind.Contributor, m.Creators[1].Kind);
        Assert.Equal("ill", m.Creators[1].NativeRole);
    }

    [Fact]
    public void Epub2_attributes_supply_sort_names_and_roles()
    {
        using var temp = new TempDir();
        BookMetadata m = Read(new EpubBuilder()
            .WithOpf(EpubBuilder.Epub2Opf)
            .WriteTo(temp.File("valid-epub2.epub")));

        Assert.Equal("Neverwhere", m.Title);
        Assert.Equal("Gaiman, Neil", m.Creators[0].SortName);
        Assert.Equal("aut", m.Creators[0].Role);
    }

    /// <summary>
    /// A half-numbered series entry is normal — a novella published as book 2.5
    /// — and must survive as an exact value.
    /// </summary>
    [Fact]
    public void Epub3_collection_supplies_series_with_fractional_index()
    {
        using var temp = new TempDir();
        BookMetadata m = Read(new EpubBuilder().WriteTo(temp.File("valid-epub3.epub")));

        Assert.Equal("Sandman Universe", m.Series?.Name);
        Assert.Equal(2.5m, m.Series?.Index);
    }

    [Fact]
    public void Epub2_calibre_meta_supplies_series()
    {
        using var temp = new TempDir();
        BookMetadata m = Read(new EpubBuilder()
            .WithOpf(EpubBuilder.Epub2Opf)
            .WriteTo(temp.File("valid-epub2.epub")));

        Assert.Equal("London Below", m.Series?.Name);
        Assert.Equal(1m, m.Series?.Index);
    }

    [Fact]
    public void Unique_identifier_is_resolved_through_the_package_reference()
    {
        using var temp = new TempDir();
        BookMetadata m = Read(new EpubBuilder().WriteTo(temp.File("valid-epub3.epub")));

        Assert.Equal("urn:isbn:9781472200341", m.UniqueIdentifier?.Value);
        Assert.True(m.Identifiers.Single().IsUnique);
    }

    /// <summary>
    /// A bare year must not be promoted to 1 January, which would assert a
    /// publication day the publisher never claimed.
    /// </summary>
    [Fact]
    public void Partial_dates_keep_their_precision_and_raw_text()
    {
        using var temp = new TempDir();
        BookMetadata m = Read(new EpubBuilder().WriteTo(temp.File("valid-epub3.epub")));

        Assert.Equal("2013", m.PublicationDate?.Raw);
        Assert.Equal(DatePrecision.Year, m.PublicationDate?.Precision);
    }

    [Theory]
    [InlineData("2013", DatePrecision.Year)]
    [InlineData("2013-05", DatePrecision.Month)]
    [InlineData("2013-05-03", DatePrecision.Day)]
    [InlineData("sometime in the 90s", DatePrecision.Unknown)]
    public void Date_precision_reflects_what_the_source_stated(string raw, DatePrecision expected)
    {
        BookDate date = EBookMeta.Documents.OpfDocument.ParseDate(raw);

        Assert.Equal(expected, date.Precision);
        Assert.Equal(raw, date.Raw);
    }

    [Fact]
    public void Cover_is_found_through_the_epub3_manifest_property()
    {
        using var temp = new TempDir();
        BookMetadata m = Read(new EpubBuilder().WriteTo(temp.File("valid-epub3.epub")));

        Assert.NotNull(m.Cover);
        Assert.Equal("image/png", m.Cover!.MediaType);
        Assert.Equal("OEBPS/images/cover.png", m.Cover.SourceEntryName);
        Assert.Equal(PngBuilder.OnePixel, m.Cover.Data);
    }

    [Fact]
    public void Cover_is_found_through_the_epub2_meta_element()
    {
        using var temp = new TempDir();
        BookMetadata m = Read(new EpubBuilder()
            .WithOpf(EpubBuilder.Epub2Opf)
            .WriteTo(temp.File("valid-epub2.epub")));

        Assert.Equal("cover-img", m.Cover?.SourceManifestId);
    }

    /// <summary>
    /// A cover declaration pointing at a missing entry is EPUB-E030's business.
    /// Reading must not throw over it.
    /// </summary>
    [Fact]
    public void Missing_cover_entry_does_not_break_reading()
    {
        using var temp = new TempDir();
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
        BookMetadata m = Read(new EpubBuilder().WriteTo(temp.File("valid-epub3.epub")));

        UnmappedField mood = m.UnmappedFields.Single(f => f.Key == "custom:mood");

        Assert.Equal("wistful", mood.Text);
        Assert.Equal("OPF", mood.Source);
        Assert.True(mood.Line > 0, "line info should survive parsing");
    }

    [Fact]
    public void Malformed_opf_is_fatal_rather_than_silently_empty()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithOpf("<package><metadata><dc:title>unclosed")
            .WriteTo(temp.File("broken-unclosed-tag.epub"));

        // EPUB-F001. Editing a document we cannot parse would risk destroying
        // content, so reading refuses and the repair path takes over.
        Assert.Throws<BookFormatException>(() => Read(path));
    }

    [Fact]
    public void Missing_container_xml_is_fatal()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithContainerXml(null)
            .WriteTo(temp.File("broken-epub-f002-no-container.epub"));

        Assert.Throws<BookFormatException>(() => Read(path));
    }

    [Fact]
    public void Container_xml_pointing_nowhere_is_fatal()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithContainerXml("""
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/nowhere.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """)
            .WriteTo(temp.File("broken-epub-f002-dangling-rootfile.epub"));

        Assert.Throws<BookFormatException>(() => Read(path));
    }

    [Theory]
    [InlineData("OEBPS/content.opf", "images/cover.png", "OEBPS/images/cover.png")]
    [InlineData("OEBPS/content.opf", "my%20cover.png", "OEBPS/my cover.png")]
    [InlineData("OEBPS/content.opf", "text.xhtml#part2", "OEBPS/text.xhtml")]
    [InlineData("content.opf", "cover.png", "cover.png")]
    public void Hrefs_resolve_against_the_package_document_directory(
        string opfPath, string href, string expected)
    {
        Assert.Equal(expected, EpubHandler.ResolveHref(opfPath, href));
    }
}
