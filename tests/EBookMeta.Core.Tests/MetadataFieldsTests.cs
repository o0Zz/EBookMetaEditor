using EBookMeta.Documents;
using EBookMeta.Formats;
using EBookMeta.Model;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Tests for the field projection both editors share.
/// </summary>
/// <remarks>
/// These rules are what makes "open a file, save it, get identical bytes" true, so
/// every <c>Apply</c> that changes nothing must report that it changed nothing.
/// </remarks>
public sealed class MetadataFieldsTests
{
    private static BookMetadata Sample()
    {
        var metadata = new BookMetadata
        {
            Title = "Neverwhere",
            SortTitle = "Neverwhere",
            Publisher = "Headline",
            Language = "en-GB",
            Description = "A blurb.",
            PublicationDate = OpfDocument.ParseDate("2013"),
            Series = new SeriesInfo { Name = "London Below", Index = 2.5m },
        };

        metadata.Creators.Add(new Creator
        {
            Name = "Neil Gaiman",
            SortName = "Gaiman, Neil",
            Role = "aut",
            NativeRole = "aut",
            Kind = CreatorKind.Creator,
        });

        metadata.Creators.Add(new Creator
        {
            Name = "Dave McKean",
            Role = "ill",
            NativeRole = "ill",
            Kind = CreatorKind.Contributor,
        });

        metadata.Subjects.Add("Fantasy");
        metadata.Subjects.Add("London");

        return metadata;
    }

    [Theory]
    [InlineData(MetadataField.Title, "Neverwhere")]
    [InlineData(MetadataField.SortTitle, "Neverwhere")]
    [InlineData(MetadataField.Creators, "Neil Gaiman")]
    [InlineData(MetadataField.Series, "London Below")]
    [InlineData(MetadataField.SeriesIndex, "2.5")]
    [InlineData(MetadataField.Publisher, "Headline")]
    [InlineData(MetadataField.PublicationDate, "2013")]
    [InlineData(MetadataField.Language, "en-GB")]
    [InlineData(MetadataField.Subjects, "Fantasy, London")]
    [InlineData(MetadataField.Description, "A blurb.")]
    public void Reads_each_field_as_editable_text(MetadataField field, string expected) =>
        Assert.Equal(expected, MetadataFields.Read(Sample(), field));

    [Fact]
    public void An_absent_field_reads_as_empty_text()
    {
        var empty = new BookMetadata();

        Assert.Equal(string.Empty, MetadataFields.Read(empty, MetadataField.Title));
        Assert.Equal(string.Empty, MetadataFields.Read(empty, MetadataField.Series));
        Assert.Equal(string.Empty, MetadataFields.Read(empty, MetadataField.SeriesIndex));
        Assert.Equal(string.Empty, MetadataFields.Read(empty, MetadataField.Creators));
    }

    /// <summary>The property everything else rests on.</summary>
    [Theory]
    [InlineData(MetadataField.Title)]
    [InlineData(MetadataField.SortTitle)]
    [InlineData(MetadataField.Creators)]
    [InlineData(MetadataField.Series)]
    [InlineData(MetadataField.SeriesIndex)]
    [InlineData(MetadataField.Publisher)]
    [InlineData(MetadataField.PublicationDate)]
    [InlineData(MetadataField.Language)]
    [InlineData(MetadataField.Subjects)]
    [InlineData(MetadataField.Description)]
    public void Applying_the_text_that_was_read_changes_nothing(MetadataField field)
    {
        BookMetadata metadata = Sample();

        Assert.False(MetadataFields.Apply(metadata, field, MetadataFields.Read(metadata, field)));
    }

    [Fact]
    public void Applying_text_trims_it_and_treats_blank_as_a_clear()
    {
        BookMetadata metadata = Sample();

        Assert.True(MetadataFields.Apply(metadata, MetadataField.Title, "  American Gods  "));
        Assert.Equal("American Gods", metadata.Title);

        Assert.True(MetadataFields.Apply(metadata, MetadataField.Publisher, "   "));
        Assert.Null(metadata.Publisher);
    }

    [Fact]
    public void Rewriting_the_authors_keeps_the_contributors()
    {
        BookMetadata metadata = Sample();

        // Only the primary creators are shown, because only they are edited — an
        // editor that shows only authors must not delete the illustrator.
        Assert.DoesNotContain("McKean", MetadataFields.Read(metadata, MetadataField.Creators));

        Assert.True(MetadataFields.Apply(metadata, MetadataField.Creators, "Terry Pratchett"));

        Assert.Equal(["Terry Pratchett"], metadata.PrimaryCreators.Select(c => c.Name));
        Assert.Equal(
            ["Dave McKean"],
            metadata.Creators.Where(c => c.Kind == CreatorKind.Contributor).Select(c => c.Name));
    }

    [Fact]
    public void A_sort_name_follows_its_author_and_no_one_else()
    {
        BookMetadata metadata = Sample();

        MetadataFields.Apply(metadata, MetadataField.Creators, "Neil Gaiman; Terry Pratchett");

        // "Gaiman, Neil" belongs to Neil Gaiman. Carrying it onto whoever ends up
        // in that position would be worse than leaving it empty.
        Assert.Equal("Gaiman, Neil", metadata.PrimaryCreators.First().SortName);
        Assert.Null(metadata.PrimaryCreators.Last().SortName);

        MetadataFields.Apply(metadata, MetadataField.Creators, "Terry Pratchett");

        Creator author = metadata.PrimaryCreators.Single();
        Assert.Equal("Terry Pratchett", author.Name);
        Assert.Null(author.SortName);
        Assert.Equal("aut", author.Role);
    }

    [Fact]
    public void Authors_split_on_semicolons_and_subjects_on_commas()
    {
        BookMetadata metadata = Sample();

        MetadataFields.Apply(metadata, MetadataField.Creators, " A ;; B ; ");
        Assert.Equal(["A", "B"], metadata.PrimaryCreators.Select(c => c.Name));

        Assert.True(MetadataFields.Apply(metadata, MetadataField.Subjects, "Horror,, Comics , "));
        Assert.Equal(["Horror", "Comics"], metadata.Subjects);
    }

    [Fact]
    public void The_series_name_carries_the_index()
    {
        BookMetadata metadata = Sample();

        MetadataFields.Apply(metadata, MetadataField.Series, "Discworld");
        Assert.Equal("Discworld", metadata.Series?.Name);
        Assert.Equal(2.5m, metadata.Series?.Index);

        // An index on its own is not something the model can hold, so clearing the
        // name clears both.
        Assert.True(MetadataFields.Apply(metadata, MetadataField.Series, ""));
        Assert.Null(metadata.Series);
        Assert.Equal(string.Empty, MetadataFields.Read(metadata, MetadataField.SeriesIndex));

        Assert.False(MetadataFields.Apply(metadata, MetadataField.SeriesIndex, "3"));
        Assert.Null(metadata.Series);
    }

    [Fact]
    public void An_index_is_parsed_invariantly_or_kept_verbatim()
    {
        BookMetadata metadata = Sample();

        MetadataFields.Apply(metadata, MetadataField.SeriesIndex, "2.5");
        Assert.Equal(2.5m, metadata.Series?.Index);
        Assert.Null(metadata.Series?.RawIndex);

        // "3 of 7" and "Annual" are real, and the format that supplied one can
        // usually store it back.
        MetadataFields.Apply(metadata, MetadataField.SeriesIndex, "3 of 7");
        Assert.Null(metadata.Series?.Index);
        Assert.Equal("3 of 7", metadata.Series?.RawIndex);
        Assert.Equal("3 of 7", MetadataFields.Read(metadata, MetadataField.SeriesIndex));

        Assert.True(MetadataFields.Apply(metadata, MetadataField.SeriesIndex, ""));
        Assert.Equal("London Below", metadata.Series?.Name);
        Assert.Null(metadata.Series?.Index);
    }

    [Fact]
    public void A_date_keeps_the_characters_it_arrived_as()
    {
        BookMetadata metadata = Sample();

        // A file that said "2013" must not come back as "2013-01-01", which would
        // assert a day it never claimed.
        Assert.False(MetadataFields.Apply(metadata, MetadataField.PublicationDate, "2013"));
        Assert.Equal("2013", metadata.PublicationDate?.Raw);
        Assert.Equal(DatePrecision.Year, metadata.PublicationDate?.Precision);

        Assert.True(MetadataFields.Apply(metadata, MetadataField.PublicationDate, "2013-09-24"));
        Assert.Equal(DatePrecision.Day, metadata.PublicationDate?.Precision);

        MetadataFields.Apply(metadata, MetadataField.PublicationDate, "circa 1890");
        Assert.Equal("circa 1890", metadata.PublicationDate?.Raw);
        Assert.Equal(DatePrecision.Unknown, metadata.PublicationDate?.Precision);
    }

    [Theory]
    [InlineData(MetadataField.Cover)]
    [InlineData(MetadataField.Identifiers)]
    [InlineData(MetadataField.None)]
    public void A_field_with_no_text_projection_is_rejected(MetadataField field)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MetadataFields.Read(Sample(), field));
        Assert.Throws<ArgumentOutOfRangeException>(() => MetadataFields.Apply(Sample(), field, "x"));
    }
}
