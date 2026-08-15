using System.Text;
using EBookMeta.Containers;
using EBookMeta.Xml;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

public sealed class CbzWriteTests
{
    private static void Write(
        string source, string target, Action<BookMetadata> edit)
    {
        using ZipContainer container = ZipContainer.Open(source);
        var format = new CbzFormat();
        BookMetadata metadata = format.Read(container);
        edit(metadata);
        format.Write(container, metadata, target);
    }

    private static BookMetadata Read(string path)
    {
        using ZipContainer container = ZipContainer.Open(path);
        return new CbzFormat().Read(container);
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

    /// <summary>Hard invariant 6, for comics.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saving_without_editing_is_byte_identical(bool comicInfoLast)
    {
        using var temp = new TempDir();

        // Where the metadata entry sits in the archive is a producer's choice, and
        // a save must not move it.
        CbzBuilder builder = comicInfoLast ? new CbzBuilder().WithComicInfoLast() : new CbzBuilder();
        string source = builder.WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, _ => { });

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    /// <summary>
    /// The page count is recomputed from the images present, whether it was missing
    /// or simply wrong.
    /// </summary>
    /// <remarks>
    /// The one place saving an unedited file deliberately changes it. Byte-identity
    /// is a property of saving a <em>correct</em> archive; supplying a field whose
    /// value is sitting in the archive waiting to be counted is the whole point of
    /// correcting on save.
    /// </remarks>
    [Fact]
    public void Saving_recomputes_the_page_count()
    {
        using var temp = new TempDir();
        string target = temp.File("saved.cbz");

        string missing = new CbzBuilder()
            .WithComicInfo(CbzBuilder.MinimalComicInfo)
            .WriteTo(temp.File("missing.cbz"));

        Write(missing, target, _ => { });

        Assert.Contains("<PageCount>3</PageCount>", ComicInfoText(target), StringComparison.Ordinal);

        // Everything else in the document still survives untouched.
        Assert.Contains("<Series>The Sandman</Series>", ComicInfoText(target), StringComparison.Ordinal);

        string wrong = new CbzBuilder()
            .WithComicInfo(CbzBuilder.MinimalComicInfo.Replace(
                "<Series>The Sandman</Series>",
                "<Series>The Sandman</Series>\n  <PageCount>99</PageCount>"))
            .WriteTo(temp.File("wrong.cbz"));

        Write(wrong, temp.File("saved-2.cbz"), _ => { });

        Assert.Contains("<PageCount>3</PageCount>", ComicInfoText(temp.File("saved-2.cbz")), StringComparison.Ordinal);
        Assert.DoesNotContain("99", ComicInfoText(temp.File("saved-2.cbz")), StringComparison.Ordinal);
    }

    /// <summary>
    /// A metadata document below the root is moved up to it — the only correction
    /// that changes the archive's entry list rather than a document's contents.
    /// </summary>
    [Fact]
    public void Saving_moves_a_nested_metadata_document_to_the_root()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder()
            .WithComicInfoAt("meta/ComicInfo.xml")
            .WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, _ => { });

        using ZipContainer saved = ZipContainer.Open(target);
        List<string> names = [.. saved.Entries.Select(e => e.Name)];

        Assert.Contains("ComicInfo.xml", names);
        Assert.DoesNotContain("meta/ComicInfo.xml", names);

        // It is safe because the entry it moves is not a page: the images keep
        // their order, which for a comic is the reading order.
        Assert.Equal(
            ["01.png", "02.png", "03.png"],
            names.Where(n => n.EndsWith(".png", StringComparison.Ordinal)));

    }

    /// <summary>Hard invariant 9.</summary>
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

    /// <summary>Hard invariant 4.</summary>
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
            m.Series = new SeriesInfo { Name = "The Sandman", Index = 3.5m };
        });

        BookMetadata metadata = Read(target);

        Assert.Equal("Season of Mists", metadata.Title);
        Assert.Equal("Vertigo", metadata.Publisher);
        Assert.Equal("fr", metadata.Language);
        Assert.Equal("Rewritten.", metadata.Description);
        Assert.Equal(3.5m, metadata.Series?.Index);

        // A French locale would write "2,5", which no reader parses.
        Assert.Contains("<Number>3.5</Number>", ComicInfoText(target), StringComparison.Ordinal);
    }

    /// <summary>
    /// Creators go back into the element they came out of. A penciller and an inker
    /// are both <c>ill</c> in MARC, so mapping through the relator alone would turn
    /// one into the other.
    /// </summary>
    [Fact]
    public void Creators_are_written_back_under_their_native_roles()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m =>
        {
            Creator penciller = m.Creators.First(c => c.NativeRole == "Penciller");
            m.Creators.Remove(penciller);
            m.Creators.Add(penciller with { Name = "Steve Parkhouse", NativeRole = "Inker", Role = "ill" });

            // A name typed into an authors box arrives with the relator for author
            // and no ComicInfo role, and belongs in Writer.
            m.Creators.Add(new Creator
            {
                Name = "Jill Thompson",
                Role = "aut",
                NativeRole = "aut",
                Kind = CreatorKind.Creator,
            });
        });

        string after = ComicInfoText(target);

        Assert.Contains("<Inker>Steve Parkhouse</Inker>", after, StringComparison.Ordinal);
        Assert.Contains("<Penciller>Malcolm Jones III</Penciller>", after, StringComparison.Ordinal);
        Assert.Contains("Jill Thompson", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clearing a field removes its element, and a cleared series takes its issue
    /// number with it, rather than leaving an issue number behind with no series.
    /// </summary>
    [Fact]
    public void Clearing_a_field_removes_its_element()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("comic.cbz"));
        string target = temp.File("saved.cbz");

        Write(source, target, m =>
        {
            m.Publisher = null;
            m.Series = null;
        });

        string after = ComicInfoText(target);

        Assert.DoesNotContain("<Publisher>", after, StringComparison.Ordinal);
        Assert.DoesNotContain("<Series>", after, StringComparison.Ordinal);
        Assert.DoesNotContain("<Number>", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", after, StringComparison.Ordinal);
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

        Write(source, target, m => m.PublicationDate = BookDate.Parse("1990"));

        string after = ComicInfoText(target);

        Assert.Contains("<Year>1990</Year>", after, StringComparison.Ordinal);
        Assert.DoesNotContain("<Month>", after, StringComparison.Ordinal);
        Assert.DoesNotContain("<Day>", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// The common case in a collection: an untagged comic gains a metadata document
    /// that validates and agrees with itself, and every page stays where it was.
    /// </summary>
    [Fact]
    public void Tagging_an_untagged_archive_appends_a_created_document()
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

        using ZipContainer after = ZipContainer.Open(target);

        // Appended, so nothing that was already there moved.
        Assert.Equal(
            ["01.png", "02.png", "03.png", "ComicInfo.xml"],
            after.Entries.Select(e => e.Name));

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
            ComicInfoText(target));
    }

    /// <summary>
    /// The ComicBookLover blob lives in the ZIP comment, a rebuild cannot write
    /// one, and losing it to a title edit is not a trade this tool makes.
    /// </summary>
    [Fact]
    public void An_archive_comment_blocks_the_write()
    {
        using var temp = new TempDir();
        string source = new CbzBuilder().WriteTo(temp.File("cbl.cbz"));
        CbzBuilder.AddArchiveComment(source, "{\"appID\":\"ComicBookLover\"}");

        string target = temp.File("saved.cbz");

        // The comment survives being read, which is what makes refusing possible.
        using (ZipContainer container = ZipContainer.Open(source))
        {
            Assert.Equal("{\"appID\":\"ComicBookLover\"}", container.ArchiveComment);
        }

        Log.Clear();

        BookFormatException error = Assert.Throws<BookFormatException>(
            () => Write(source, target, m => m.Title = "Anything"));

        Assert.Contains("ComicBookLover", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));

        // A refusal says which rule refused. It fires on write rather than on
        // open: nothing is wrong with the file until a save would drop something.
        Assert.Contains(
            Log.Entries,
            e => e.Level == LogLevel.Error
                && e.Message.StartsWith("CBZ-W012:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The cover is a page image, so a comic cannot store one separately and must
    /// say so rather than accepting an edit it would discard.
    /// </summary>
    [Fact]
    public void A_comic_cannot_write_a_cover()
    {
        FormatCapabilities capabilities = new CbzFormat().Capabilities;

        Assert.True(capabilities.CanWrite);
        Assert.False(capabilities.CanWriteAll(MetadataField.Cover));
        Assert.False(capabilities.CanWriteAll(MetadataField.SortTitle));
        Assert.True(capabilities.CanWriteAll(MetadataField.Series | MetadataField.SeriesIndex));
        Assert.Equal(MetadataField.Cover, capabilities.UnsupportedIn(MetadataField.Cover));
    }
}
