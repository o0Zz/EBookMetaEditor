using System.Text;
using EBookMeta.Containers;
using EBookMeta.Documents;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Tests for the comic write path — the part that destroys libraries when it is
/// wrong.
/// </summary>
public sealed class CbzWriteTests
{
    private static void Write(string source, string target, Action<BookMetadata> edit)
    {
        using ZipContainer container = ZipContainer.Open(source);
        var handler = new CbzHandler();
        BookMetadata metadata = handler.Read(container);
        edit(metadata);
        handler.Write(container, metadata, target);
    }

    private static BookMetadata Read(string path)
    {
        using ZipContainer container = ZipContainer.Open(path);
        return new CbzHandler().Read(container);
    }

    private static string ComicInfoText(string path)
    {
        using ZipContainer container = ZipContainer.Open(path);
        ContainerEntry entry = container.Entries.Single(e =>
            e.Name.Equals(ComicInfoDocument.DefaultEntryName, StringComparison.OrdinalIgnoreCase));

        using Stream stream = container.OpenRead(entry);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Hard invariant 6, for comics. Open a file, save it without editing, get
    /// identical bytes back.
    /// </summary>
    [Fact]
    public void Saving_without_editing_is_byte_identical()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, _ => { });

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    /// <summary>
    /// Where the metadata entry sits in the archive is a producer's choice, and a
    /// save must not move it.
    /// </summary>
    [Fact]
    public void Saving_without_editing_is_byte_identical_with_ComicInfo_last()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WithComicInfoLast().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, _ => { });

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    [Fact]
    public void Saving_a_minimal_document_without_editing_is_byte_identical()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder()
            .WithComicInfo(CbzBuilder.MinimalComicInfo)
            .WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, _ => { });

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    /// <summary>
    /// Hard invariant 9. Changing a title must produce a one-line diff, not a
    /// reformat of the whole document.
    /// </summary>
    [Fact]
    public void Editing_one_field_changes_one_line()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m => m.Title = "Season of Mists");

        string[] before = ComicInfoText(source).Split('\n');
        string[] after = ComicInfoText(target).Split('\n');

        Assert.Equal(before.Length, after.Length);
        Assert.Single(before.Where((line, i) => line != after[i]));
        Assert.Contains("<Title>Season of Mists</Title>", after.Select(line => line.Trim()));
    }

    /// <summary>
    /// Hard invariant 4. Page images are copied byte for byte, in order, with
    /// their compression method unchanged.
    /// </summary>
    [Fact]
    public void Entry_order_and_compression_survive_a_write()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder()
            .WithEntry("stored.txt", Encoding.UTF8.GetBytes("x"), stored: true)
            .WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m => m.Publisher = "Vertigo");

        using ZipContainer before = ZipContainer.Open(source);
        using ZipContainer after = ZipContainer.Open(target);

        Assert.Equal(
            before.Entries.Select(e => (e.Name, e.CompressionMethod)),
            after.Entries.Select(e => (e.Name, e.CompressionMethod)));
    }

    /// <summary>
    /// Elements this build maps onto no model field survive verbatim, because
    /// nothing ever goes near them.
    /// </summary>
    [Fact]
    public void Unrecognised_elements_survive_a_write_verbatim()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m => m.Title = "Season of Mists");

        string after = ComicInfoText(target);

        Assert.Contains("<Notes>Tagged with ComicTagger 1.3.2</Notes>", after, StringComparison.Ordinal);
        Assert.Contains("<AgeRating>Mature 17+</AgeRating>", after, StringComparison.Ordinal);
        Assert.Contains("<Volume>1989</Volume>", after, StringComparison.Ordinal);
        Assert.Contains("<Page Image=\"0\" Type=\"FrontCover\"/>", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Edits_survive_a_read_back()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m =>
        {
            m.Title = "Season of Mists";
            m.Publisher = "Vertigo";
            m.Language = "fr";
            m.Description = "Rewritten.";
            m.Series = new SeriesInfo { Name = "The Sandman", Index = 4 };
        });

        BookMetadata metadata = Read(target);

        Assert.Equal("Season of Mists", metadata.Title);
        Assert.Equal("Vertigo", metadata.Publisher);
        Assert.Equal("fr", metadata.Language);
        Assert.Equal("Rewritten.", metadata.Description);
        Assert.Equal(4m, metadata.Series?.Index);
    }

    /// <summary>
    /// A French locale would write "2,5", which no reader parses.
    /// </summary>
    [Fact]
    public void A_fractional_issue_number_uses_an_invariant_decimal_point()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m => m.Series = new SeriesInfo { Name = "The Sandman", Index = 3.5m });

        Assert.Contains("<Number>3.5</Number>", ComicInfoText(target), StringComparison.Ordinal);
    }

    /// <summary>
    /// Creators go back into the element they came out of. A penciller and an
    /// inker are both <c>ill</c> in MARC, so mapping through the relator alone
    /// would turn one into the other.
    /// </summary>
    [Fact]
    public void Creators_are_written_back_under_their_native_roles()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m =>
        {
            Creator inker = m.Creators.First(c => c.NativeRole == "Penciller");
            m.Creators.Remove(inker);
            m.Creators.Add(inker with { Name = "Steve Parkhouse", NativeRole = "Inker", Role = "ill" });
        });

        string after = ComicInfoText(target);

        Assert.Contains("<Inker>Steve Parkhouse</Inker>", after, StringComparison.Ordinal);
        Assert.Contains("<Penciller>Malcolm Jones III</Penciller>", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name typed into an authors box arrives with the relator for author and no
    /// ComicInfo role, and belongs in <c>Writer</c>.
    /// </summary>
    [Fact]
    public void A_creator_with_only_a_relator_becomes_the_writer()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder()
            .WithComicInfo(CbzBuilder.MinimalComicInfo)
            .WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m => m.Creators.Add(new Creator
        {
            Name = "Neil Gaiman",
            Role = "aut",
            NativeRole = "aut",
            Kind = CreatorKind.Creator,
        }));

        Assert.Contains("<Writer>Neil Gaiman</Writer>", ComicInfoText(target), StringComparison.Ordinal);
    }

    [Fact]
    public void Clearing_a_field_removes_its_element()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m => m.Publisher = null);

        string after = ComicInfoText(target);

        Assert.DoesNotContain("<Publisher>", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clearing the series clears the issue number with it: a number belongs to a
    /// series, and one left behind is exactly what rule CBZ-W030 reports.
    /// </summary>
    [Fact]
    public void Clearing_the_series_removes_the_number_too()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m => m.Series = null);

        string after = ComicInfoText(target);

        Assert.DoesNotContain("<Series>", after, StringComparison.Ordinal);
        Assert.DoesNotContain("<Number>", after, StringComparison.Ordinal);
        Assert.Contains("<Title>", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// A date states no more than it knows: clearing the day must not leave a day
    /// element behind claiming the 7th.
    /// </summary>
    [Fact]
    public void A_less_precise_date_drops_the_elements_it_no_longer_states()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m => m.PublicationDate = OpfDocument.ParseDate("1990"));

        string after = ComicInfoText(target);

        Assert.Contains("<Year>1990</Year>", after, StringComparison.Ordinal);
        Assert.DoesNotContain("<Month>", after, StringComparison.Ordinal);
        Assert.DoesNotContain("<Day>", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// The common case in a collection: an untagged comic gains a metadata
    /// document, and every page stays exactly where it was.
    /// </summary>
    [Fact]
    public void Tagging_an_untagged_archive_appends_the_metadata_entry()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WithoutComicInfo().WriteTo(temp.File("untagged.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m => m.Series = new SeriesInfo { Name = "The Sandman", Index = 1 });

        using ZipContainer before = ZipContainer.Open(source);
        using ZipContainer after = ZipContainer.Open(target);

        Assert.Equal(
            ["01.png", "02.png", "03.png"],
            before.Entries.Select(e => e.Name));

        // Appended, so nothing that was already there moved.
        Assert.Equal(
            ["01.png", "02.png", "03.png", "ComicInfo.xml"],
            after.Entries.Select(e => e.Name));
    }

    /// <summary>
    /// A document this build creates is schema-ordered and states the page count
    /// it can see, so the file it produces validates and agrees with itself.
    /// </summary>
    [Fact]
    public void A_created_document_is_schema_ordered_and_states_the_page_count()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WithoutComicInfo().WriteTo(temp.File("untagged.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m =>
        {
            m.Title = "The Doll's House";
            m.Series = new SeriesInfo { Name = "The Sandman", Index = 2 };
            m.Publisher = "DC Comics";
            m.Language = "en";
        });

        string after = ComicInfoText(target);

        Assert.Equal(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ComicInfo xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Title>The Doll's House</Title>
              <Series>The Sandman</Series>
              <Number>2</Number>
              <Publisher>DC Comics</Publisher>
              <PageCount>3</PageCount>
              <LanguageISO>en</LanguageISO>
            </ComicInfo>

            """.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            after);
    }

    /// <summary>
    /// The ComicBookLover blob lives in the ZIP comment, a rebuild cannot write
    /// one, and losing it to a title edit is not a trade this tool makes. So the
    /// write is refused and the target is never created.
    /// </summary>
    [Fact]
    public void An_archive_comment_blocks_the_write()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("cbl.cbz"));
        CbzBuilder.AddArchiveComment(source, "{\"appID\":\"ComicBookLover\"}");

        string target = temp.File("saved.cbz");

        BookFormatException error = Assert.Throws<BookFormatException>(
            () => Write(source, target, m => m.Title = "Anything"));

        Assert.Contains("ComicBookLover", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    /// <summary>
    /// The comment survives being read, which is what makes refusing possible.
    /// </summary>
    [Fact]
    public void An_archive_comment_is_readable()
    {
        using var temp = new TempDir();
        string path = new CbzBuilder().WriteTo(temp.File("cbl.cbz"));
        CbzBuilder.AddArchiveComment(path, "{\"appID\":\"ComicBookLover\"}");

        using ZipContainer container = ZipContainer.Open(path);

        Assert.Equal("{\"appID\":\"ComicBookLover\"}", container.ArchiveComment);
        Assert.Equal(4, container.Entries.Count);
    }

    /// <summary>
    /// The cover is a page image, so a comic cannot store one separately and must
    /// say so rather than accepting an edit it would discard.
    /// </summary>
    [Fact]
    public void A_comic_cannot_write_a_cover()
    {
        FormatCapabilities capabilities = new CbzHandler().Capabilities;

        Assert.True(capabilities.CanWrite);
        Assert.False(capabilities.CanWriteAll(MetadataField.Cover));
        Assert.False(capabilities.CanWriteAll(MetadataField.SortTitle));
        Assert.True(capabilities.CanWriteAll(MetadataField.Series | MetadataField.SeriesIndex));
        Assert.Equal(MetadataField.Cover, capabilities.UnsupportedIn(MetadataField.Cover));
    }
}
