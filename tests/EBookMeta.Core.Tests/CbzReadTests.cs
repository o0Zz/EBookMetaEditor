using System.Text;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Tests for reading comic archive metadata.
/// </summary>
public sealed class CbzReadTests
{
    private static BookMetadata Read(string path)
    {
        using ZipContainer container = ZipContainer.Open(path);
        return new CbzHandler().Read(container);
    }

    [Fact]
    public void CbzIsRegisteredOutOfTheBox() =>
        Assert.IsType<CbzHandler>(BookFormats.For(FormatId.Cbz));

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

    /// <summary>
    /// The writer is the primary creator and everyone else a contributor, so
    /// "authors" means the same thing for a comic as for a book.
    /// </summary>
    [Fact]
    public void Reads_creators_with_their_native_roles()
    {
        using var temp = new TempDir();
        BookMetadata metadata = Read(new CbzBuilder().WriteTo(temp.File("comic.cbz")));

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
    public void Reads_year_month_and_day_as_one_date()
    {
        using var temp = new TempDir();
        BookMetadata metadata = Read(new CbzBuilder().WriteTo(temp.File("comic.cbz")));

        Assert.Equal("1989-03-07", metadata.PublicationDate?.Raw);
        Assert.Equal(DatePrecision.Day, metadata.PublicationDate?.Precision);
    }

    /// <summary>
    /// A bare year must not be promoted to a January date, so precision is
    /// recorded rather than assumed.
    /// </summary>
    [Fact]
    public void A_year_on_its_own_stays_a_year()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfo("<ComicInfo><Series>S</Series><Year>1989</Year></ComicInfo>")
            .WriteTo(temp.File("comic.cbz"));

        BookMetadata metadata = Read(path);

        Assert.Equal("1989", metadata.PublicationDate?.Raw);
        Assert.Equal(DatePrecision.Year, metadata.PublicationDate?.Precision);
    }

    [Fact]
    public void An_issue_number_that_is_not_a_number_is_kept_as_written()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfo("<ComicInfo><Series>S</Series><Number>3 of 7</Number></ComicInfo>")
            .WriteTo(temp.File("comic.cbz"));

        SeriesInfo series = Read(path).Series!;

        Assert.Null(series.Index);
        Assert.Equal("3 of 7", series.RawIndex);
    }

    /// <summary>
    /// An untagged comic is the ordinary case, not a failure: it reads as a file
    /// with no metadata and a cover.
    /// </summary>
    [Fact]
    public void An_archive_with_no_ComicInfo_reads_as_empty_metadata()
    {
        using var temp = new TempDir();
        BookMetadata metadata = Read(
            new CbzBuilder().WithoutComicInfo().WriteTo(temp.File("untagged.cbz")));

        Assert.Null(metadata.Title);
        Assert.Null(metadata.Series);
        Assert.Empty(metadata.Creators);
        Assert.NotNull(metadata.Cover);
    }

    /// <summary>
    /// The cover is page one, and page one is decided by reading order rather
    /// than by archive order or by an ordinal name sort.
    /// </summary>
    [Fact]
    public void The_cover_is_the_first_page_in_reading_order()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithPages("10.png", "2.png", "1.png")
            .WriteTo(temp.File("comic.cbz"));

        Assert.Equal("1.png", Read(path).Cover?.SourceEntryName);
    }

    [Fact]
    public void The_cover_media_type_comes_from_the_extension()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithPages("01.jpg")
            .WriteTo(temp.File("comic.cbz"));

        Assert.Equal("image/jpeg", Read(path).Cover?.MediaType);
    }

    /// <summary>
    /// Everything the model has no field for is recorded so the UI can show it,
    /// which is separate from how it is preserved — that happens by never
    /// touching the element.
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

    /// <summary>
    /// Casing is a producer's mistake, not a reason to refuse a file.
    /// </summary>
    [Fact]
    public void ComicInfo_is_found_whatever_its_casing()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfoAt("comicinfo.xml")
            .WriteTo(temp.File("comic.cbz"));

        Assert.Equal("The Doll's House", Read(path).Title);
    }

    /// <summary>
    /// A metadata document that is not well-formed blocks editing, and says why.
    /// </summary>
    [Fact]
    public void A_malformed_ComicInfo_is_a_format_error()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfo("<ComicInfo><Series>Unclosed</ComicInfo>")
            .WriteTo(temp.File("broken-cbz-f001-malformed.cbz"));

        BookFormatException error = Assert.Throws<BookFormatException>(() => Read(path));

        Assert.Contains("ComicInfo.xml", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// XML whose root is something else entirely is not a ComicInfo document, and
    /// pretending otherwise would produce empty metadata for a file that has some.
    /// </summary>
    [Fact]
    public void A_document_with_the_wrong_root_is_rejected()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder()
            .WithComicInfo("<comet><title>x</title></comet>")
            .WriteTo(temp.File("comic.cbz"));

        Assert.Throws<BookFormatException>(() => Read(path));
    }

    /// <summary>
    /// A comic archive is detected as one whether it is tagged or not, and the
    /// handler comes from the registry rather than from the extension.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_comic_archive_resolves_to_the_comic_handler(bool tagged)
    {
        using var temp = new TempDir();
        CbzBuilder builder = tagged ? new CbzBuilder() : new CbzBuilder().WithoutComicInfo();
        string path = builder.WriteTo(temp.File("comic.cbz"));

        IFormatHandler? handler = BookFormats.Resolve(path, out DetectedFormat detected);

        Assert.Equal(FormatId.Cbz, detected.Format);
        Assert.IsType<CbzHandler>(handler);
    }

    /// <summary>
    /// A Windows-1252 document declaring itself as such decodes with its accents
    /// intact rather than as replacement characters.
    /// </summary>
    [Fact]
    public void A_legacy_encoded_document_decodes_correctly()
    {
        using var temp = new TempDir();
        byte[] bytes = Encodings.Latin1.GetBytes(
            "<?xml version=\"1.0\" encoding=\"windows-1252\"?>\n"
            + "<ComicInfo><Series>Astérix</Series></ComicInfo>");

        string path = new CbzBuilder()
            .WithComicInfoBytes(bytes)
            .WriteTo(temp.File("comic.cbz"));

        Assert.Equal("Astérix", Read(path).Series?.Name);
    }

    private static class Encodings
    {
        internal static Encoding Latin1 { get; } = Encoding.GetEncoding(28591);
    }
}
