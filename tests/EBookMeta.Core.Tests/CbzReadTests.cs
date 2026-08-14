using EBookMeta.Compat;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

public sealed class CbzReadTests
{
    private static BookMetadata Read(string path)
    {
        using ZipContainer container = ZipContainer.Open(path);
        return new CbzFormat().Read(container);
    }

    private static BookMetadata ReadComicInfo(TempDir temp, string comicInfo) =>
        Read(new CbzBuilder().WithComicInfo(comicInfo).WriteTo(temp.File("comic.cbz")));

    [Fact]
    public void Reads_the_fields_the_model_carries()
    {
        using var temp = new TempDir();
        BookMetadata metadata = Read(new CbzBuilder().WriteTo(temp.File("comic.cbz")));

        Assert.Equal("The Doll's House", metadata.Title);
        Assert.Equal("The Sandman", metadata.Series?.Name);
        Assert.Equal(2.5m, metadata.Series?.Index);
        Assert.Equal("A short summary.", metadata.Description);
        Assert.Equal("DC Comics", metadata.Publisher);
        Assert.Equal("en", metadata.Language);
        Assert.Equal(["Fantasy", "Horror"], metadata.Subjects);
    }

    [Fact]
    public void Reads_creators_with_their_native_roles()
    {
        using var temp = new TempDir();
        BookMetadata metadata = Read(new CbzBuilder().WriteTo(temp.File("comic.cbz")));

        // The writer is the primary creator and everyone else a contributor, so
        // "authors" means the same thing for a comic as for a book.
        Assert.Equal(["Neil Gaiman"], metadata.PrimaryCreators.Select(c => c.Name));

        Creator writer = metadata.Creators.First();
        Assert.Equal("Writer", writer.NativeRole);
        Assert.Equal("aut", writer.Role);

        // One element, two names, comma-separated — how every tool writes this.
        Assert.Equal(
            ["Mike Dringenberg", "Malcolm Jones III"],
            metadata.Creators.Where(c => c.NativeRole == "Penciller").Select(c => c.Name));

        Creator cover = metadata.Creators.Single(c => c.NativeRole == "CoverArtist");
        Assert.Equal("Dave McKean", cover.Name);
        Assert.Equal("cov", cover.Role);
        Assert.Equal(CreatorKind.Contributor, cover.Kind);
    }

    [Fact]
    public void Reads_year_month_and_day_as_one_date_of_the_precision_stated()
    {
        using var temp = new TempDir();
        BookMetadata full = Read(new CbzBuilder().WriteTo(temp.File("comic.cbz")));

        Assert.Equal("1989-03-07", full.PublicationDate?.Raw);
        Assert.Equal(DatePrecision.Day, full.PublicationDate?.Precision);

        // A bare year must not be promoted to a January date.
        BookMetadata yearOnly = ReadComicInfo(
            temp, "<ComicInfo><Series>S</Series><Year>1989</Year></ComicInfo>");

        Assert.Equal("1989", yearOnly.PublicationDate?.Raw);
        Assert.Equal(DatePrecision.Year, yearOnly.PublicationDate?.Precision);
    }

    [Fact]
    public void An_issue_number_that_is_not_a_number_is_kept_as_written()
    {
        using var temp = new TempDir();
        SeriesInfo series = ReadComicInfo(
            temp, "<ComicInfo><Series>S</Series><Number>3 of 7</Number></ComicInfo>").Series!;

        Assert.Null(series.Index);
        Assert.Equal("3 of 7", series.RawIndex);
    }

    [Fact]
    public void An_archive_with_no_ComicInfo_reads_as_empty_metadata()
    {
        using var temp = new TempDir();

        // An untagged comic is the ordinary case, not a failure.
        BookMetadata metadata = Read(
            new CbzBuilder().WithoutComicInfo().WriteTo(temp.File("untagged.cbz")));

        Assert.Null(metadata.Title);
        Assert.Null(metadata.Series);
        Assert.Empty(metadata.Creators);
        Assert.NotNull(metadata.Cover);
    }

    [Fact]
    public void The_cover_is_the_first_page_in_reading_order()
    {
        using var temp = new TempDir();

        // Page one is decided by reading order rather than by archive order or by
        // an ordinal name sort.
        string path = new CbzBuilder()
            .WithPages("10.png", "2.png", "1.png")
            .WriteTo(temp.File("comic.cbz"));

        Assert.Equal("1.png", Read(path).Cover?.SourceEntryName);

        string jpeg = new CbzBuilder().WithPages("01.jpg").WriteTo(temp.File("jpeg.cbz"));
        Assert.Equal("image/jpeg", Read(jpeg).Cover?.MediaType);
    }

    /// <summary>
    /// Everything the model has no field for is recorded so the UI can show it,
    /// which is separate from how it is preserved — that happens by never touching
    /// the element.
    /// </summary>
    [Fact]
    public void Elements_the_model_does_not_carry_are_reported_as_unmapped()
    {
        using var temp = new TempDir();
        BookMetadata metadata = Read(new CbzBuilder().WriteTo(temp.File("comic.cbz")));

        string[] keys = [.. metadata.UnmappedFields.Select(f => f.Key)];

        Assert.Contains("Notes", keys);
        Assert.Contains("Volume", keys);
        Assert.Contains("Count", keys);
        Assert.Contains("AgeRating", keys);
        Assert.Contains("Pages", keys);
        Assert.DoesNotContain("Title", keys);
        Assert.All(metadata.UnmappedFields, field => Assert.Equal("ComicInfo", field.Source));
    }

    [Fact]
    public void ComicInfo_is_found_whatever_its_casing()
    {
        using var temp = new TempDir();

        // Casing is a producer's mistake, not a reason to refuse a file.
        string path = new CbzBuilder()
            .WithComicInfoAt("comicinfo.xml")
            .WriteTo(temp.File("comic.cbz"));

        Assert.Equal("The Doll's House", Read(path).Title);
    }

    [Fact]
    public void A_document_that_is_not_a_ComicInfo_is_a_format_error()
    {
        using var temp = new TempDir();

        BookFormatException error = Assert.Throws<BookFormatException>(() => ReadComicInfo(
            temp, "<ComicInfo><Series>Unclosed</ComicInfo>"));

        Assert.Contains("ComicInfo.xml", error.Message, StringComparison.Ordinal);

        // XML whose root is something else entirely is not a ComicInfo document,
        // and pretending otherwise would produce empty metadata for a file that
        // has some.
        Assert.Throws<BookFormatException>(() => ReadComicInfo(
            temp, "<comet><title>x</title></comet>"));
    }

    /// <summary>
    /// A comic archive is detected as one whether it is tagged or not, and the
    /// the implementation comes from the registry rather than from the extension.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_comic_archive_resolves_to_the_comic_format(bool tagged)
    {
        using var temp = new TempDir();
        CbzBuilder builder = tagged ? new CbzBuilder() : new CbzBuilder().WithoutComicInfo();
        string path = builder.WriteTo(temp.File("comic.cbz"));

        IBookFormat? format = BookFormats.Resolve(path, out DetectedFormat detected);

        Assert.Equal(FormatId.Cbz, detected.Format);
        Assert.IsType<CbzFormat>(format);
        Assert.IsType<CbzFormat>(BookFormats.For(FormatId.Cbz));
    }

    [Fact]
    public void A_legacy_encoded_document_decodes_correctly()
    {
        using var temp = new TempDir();

        // Windows-1252 declaring itself as such decodes with its accents intact
        // rather than as replacement characters.
        byte[] bytes = Encodings.Latin1.GetBytes(
            "<?xml version=\"1.0\" encoding=\"windows-1252\"?>\n"
            + "<ComicInfo><Series>Astérix</Series></ComicInfo>");

        string path = new CbzBuilder()
            .WithComicInfoBytes(bytes)
            .WriteTo(temp.File("comic.cbz"));

        Assert.Equal("Astérix", Read(path).Series?.Name);
    }
}
