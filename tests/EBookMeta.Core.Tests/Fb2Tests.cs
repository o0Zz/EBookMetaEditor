using System.Text;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// FictionBook: one XML file that is both the metadata and the book.
/// </summary>
public sealed class Fb2Tests
{
    private static void Write(
        string source, string target, Action<BookMetadata> edit, ICollection<Finding>? findings = null)
    {
        using RawContainer container = RawContainer.Open(source);
        var format = new Fb2Format();
        BookMetadata metadata = format.Read(container, ReadOptions.WithoutCover);
        edit(metadata);
        format.Write(container, metadata, target, findings);
    }

    private static BookMetadata Read(string path, ReadOptions? options = null)
    {
        using RawContainer container = RawContainer.Open(path);
        return new Fb2Format().Read(container, options);
    }

    private static string TextOf(string path) =>
        File.ReadAllText(path, Encoding.UTF8);

    /// <summary>Hard invariant 6, for FictionBook.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saving_without_editing_is_byte_identical(bool windowsLineEndings)
    {
        using var temp = new TempDir();

        Fb2Builder builder = windowsLineEndings
            ? new Fb2Builder().WithWindowsLineEndings()
            : new Fb2Builder();

        string source = builder.WriteTo(temp.File("book.fb2"));
        string target = temp.File("saved.fb2");

        Write(source, target, _ => { });

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    /// <summary>
    /// The reason only the description is parsed: the book is the rest of the
    /// file, and a metadata edit must not go near it.
    /// </summary>
    [Fact]
    public void Editing_leaves_the_book_body_byte_for_byte()
    {
        using var temp = new TempDir();

        string source = new Fb2Builder()
            .WithLargeBody(500)
            .WriteTo(temp.File("book.fb2"));

        string target = temp.File("saved.fb2");

        Write(source, target, m => m.Title = "Season of Mists");

        string before = TextOf(source);
        string after = TextOf(target);

        // Everything from <body> onwards is the original characters. Entities are
        // not re-escaped, markup is not reformatted, nothing is touched.
        int bodyBefore = before.IndexOf("<body>", StringComparison.Ordinal);
        int bodyAfter = after.IndexOf("<body>", StringComparison.Ordinal);

        Assert.Equal(before.Substring(bodyBefore), after.Substring(bodyAfter));
        Assert.Contains("<book-title>Season of Mists</book-title>", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_every_mapped_field()
    {
        using var temp = new TempDir();
        string path = new Fb2Builder().WriteTo(temp.File("book.fb2"));

        BookMetadata metadata = Read(path);

        Assert.Equal("The Doll's House", metadata.Title);
        Assert.Equal("en", metadata.Language);
        Assert.Equal("DC Comics", metadata.Publisher);
        Assert.Equal("A short summary.", metadata.Description);
        Assert.Equal("1989", metadata.PublicationDate?.Raw);
        Assert.Equal("The Sandman", metadata.Series?.Name);
        Assert.Equal(2m, metadata.Series?.Index);
        Assert.Equal("Neil Gaiman", Assert.Single(metadata.PrimaryCreators).Name);
        Assert.Contains("sf", metadata.Subjects);
        Assert.Contains("fantasy", metadata.Subjects);
        Assert.Contains(metadata.Identifiers, i => i.Scheme == "ISBN");
    }

    /// <summary>
    /// document-info describes the FB2 file rather than the book, so it maps onto
    /// nothing and must survive untouched.
    /// </summary>
    [Fact]
    public void Document_info_is_reported_as_unmapped_and_preserved()
    {
        using var temp = new TempDir();
        string source = new Fb2Builder().WriteTo(temp.File("book.fb2"));

        BookMetadata metadata = Read(source);

        Assert.Contains(
            metadata.UnmappedFields, f => f.Key == "program-used" && f.Text == "FB Tools");

        string target = temp.File("saved.fb2");
        Write(source, target, m => m.Title = "Season of Mists");

        Assert.Contains("<program-used>FB Tools</program-used>", TextOf(target), StringComparison.Ordinal);
        Assert.Contains("<version>1.1</version>", TextOf(target), StringComparison.Ordinal);
    }

    [Fact]
    public void Writes_every_writable_field()
    {
        using var temp = new TempDir();
        string source = new Fb2Builder().WriteTo(temp.File("book.fb2"));
        string target = temp.File("saved.fb2");

        Write(source, target, m =>
        {
            m.Title = "Season of Mists";
            m.Language = "fr";
            m.Publisher = "Vertigo";
            m.Description = "A different summary.";
            m.Series = new SeriesInfo { Name = "Endless", Index = 4m };
            m.Creators.Clear();
            m.Creators.Add(new Creator { Name = "Jill Thompson" });
        });

        BookMetadata after = Read(target, ReadOptions.WithoutCover);

        Assert.Equal("Season of Mists", after.Title);
        Assert.Equal("fr", after.Language);
        Assert.Equal("Vertigo", after.Publisher);
        Assert.Equal("A different summary.", after.Description);
        Assert.Equal("Endless", after.Series?.Name);
        Assert.Equal(4m, after.Series?.Index);
        Assert.Equal("Jill Thompson", Assert.Single(after.PrimaryCreators).Name);
    }

    /// <summary>
    /// A name goes back into the parts FB2 stores it as, not into one element.
    /// </summary>
    [Fact]
    public void An_author_is_written_as_separate_name_parts()
    {
        using var temp = new TempDir();
        string source = new Fb2Builder().WriteTo(temp.File("book.fb2"));
        string target = temp.File("saved.fb2");

        Write(source, target, m =>
        {
            m.Creators.Clear();
            m.Creators.Add(new Creator { Name = "Arthur Conan Doyle" });
            m.Creators.Add(new Creator { Name = "Voltaire" });
        });

        string after = TextOf(target);

        Assert.Contains("<first-name>Arthur</first-name>", after, StringComparison.Ordinal);
        Assert.Contains("<middle-name>Conan</middle-name>", after, StringComparison.Ordinal);
        Assert.Contains("<last-name>Doyle</last-name>", after, StringComparison.Ordinal);

        // A single word goes to nickname rather than being guessed into one of
        // the other two.
        Assert.Contains("<nickname>Voltaire</nickname>", after, StringComparison.Ordinal);

        Assert.Equal(
            ["Arthur Conan Doyle", "Voltaire"],
            Read(target, ReadOptions.WithoutCover).PrimaryCreators.Select(c => c.Name));
    }

    /// <summary>
    /// The documented limit of splitting a name on spaces: a compound surname
    /// lands in the wrong field.
    /// </summary>
    /// <remarks>
    /// Pinned rather than fixed. FB2 stores name parts and the editor stores one
    /// string, so something has to guess at the boundary, and no rule gets every
    /// name right. What matters is that the round trip through the model is
    /// stable — the displayed name comes back unchanged — so a user who never
    /// touches the author field cannot have it quietly rearranged.
    /// </remarks>
    [Fact]
    public void A_compound_surname_is_split_wrongly_but_round_trips()
    {
        using var temp = new TempDir();
        string source = new Fb2Builder().WriteTo(temp.File("book.fb2"));
        string target = temp.File("saved.fb2");

        Write(source, target, m =>
        {
            m.Creators.Clear();
            m.Creators.Add(new Creator { Name = "Ursula K Le Guin" });
        });

        string after = TextOf(target);

        Assert.Contains("<middle-name>K Le</middle-name>", after, StringComparison.Ordinal);
        Assert.Contains("<last-name>Guin</last-name>", after, StringComparison.Ordinal);

        Assert.Equal(
            "Ursula K Le Guin",
            Assert.Single(Read(target, ReadOptions.WithoutCover).PrimaryCreators).Name);
    }

    /// <summary>
    /// A new element goes where the schema's sequence says, not wherever is
    /// convenient.
    /// </summary>
    [Fact]
    public void A_created_element_lands_in_schema_order()
    {
        using var temp = new TempDir();

        string source = new Fb2Builder()
            .WithDescription(Fb2Builder.MinimalDescription)
            .WriteTo(temp.File("book.fb2"));

        string target = temp.File("saved.fb2");

        Write(source, target, m => m.Series = new SeriesInfo { Name = "Earthsea", Index = 1m });

        string after = TextOf(target);

        // sequence comes after lang in the title-info sequence.
        Assert.True(
            after.IndexOf("<lang>", StringComparison.Ordinal)
                < after.IndexOf("<sequence", StringComparison.Ordinal),
            "sequence must be written after lang");

        Assert.Equal("Earthsea", Read(target, ReadOptions.WithoutCover).Series?.Name);
    }

    [Fact]
    public void Reads_the_cover_from_the_binary_it_points_at()
    {
        using var temp = new TempDir();
        string path = new Fb2Builder().WriteTo(temp.File("book.fb2"));

        BookMetadata metadata = Read(path);

        Assert.NotNull(metadata.Cover);
        Assert.Equal("image/png", metadata.Cover!.MediaType);
        Assert.Equal(PngBuilder.OnePixel, metadata.Cover.Data);
    }

    /// <summary>
    /// The batch grid reads without covers, and must not walk the document to
    /// find one it is not going to show.
    /// </summary>
    [Fact]
    public void Reading_without_a_cover_leaves_it_null()
    {
        using var temp = new TempDir();
        string path = new Fb2Builder().WriteTo(temp.File("book.fb2"));

        Assert.Null(Read(path, ReadOptions.WithoutCover).Cover);
    }

    /// <summary>FB2-E030: the cover page points at a binary that is not there.</summary>
    [Fact]
    public void A_dangling_cover_reference_is_reported()
    {
        using var temp = new TempDir();

        string path = new Fb2Builder()
            .WithoutBinaries()
            .WriteTo(temp.File("book.fb2"));

        var findings = new List<Finding>();
        using RawContainer container = RawContainer.Open(path);
        new Fb2Format().Read(container, ReadOptions.Default, findings);

        Assert.Contains(findings, f => f.RuleId == "FB2-E030");
    }

    [Theory]
    [InlineData("<book-title>The Doll's House</book-title>", "FB2-E011")]
    [InlineData("<lang>en</lang>", "FB2-E012")]
    public void A_missing_required_field_is_reported(string removed, string rule)
    {
        using var temp = new TempDir();

        string path = new Fb2Builder()
            .WithDescription(Fb2Builder.MinimalDescription.Replace(removed, string.Empty))
            .WriteTo(temp.File("book.fb2"));

        var findings = new List<Finding>();
        using RawContainer container = RawContainer.Open(path);
        new Fb2Format().Read(container, ReadOptions.WithoutCover, findings);

        Assert.Contains(findings, f => f.RuleId == rule);
    }

    /// <summary>FB2-F002: no description at all, so there is nothing to edit.</summary>
    [Fact]
    public void A_document_with_no_description_is_refused()
    {
        using var temp = new TempDir();

        string path = new Fb2Builder()
            .WithoutDescription()
            .WriteTo(temp.File("book.fb2"));

        var findings = new List<Finding>();
        using RawContainer container = RawContainer.Open(path);

        Assert.Throws<BookFormatException>(
            () => new Fb2Format().Read(container, ReadOptions.WithoutCover, findings));

        Assert.Contains(findings, f => f.RuleId == "FB2-F002");
    }

    /// <summary>
    /// Invariant 12: prefixes are reused, never invented, and never re-declared
    /// on an element that did not carry them.
    /// </summary>
    [Fact]
    public void Namespace_prefixes_are_left_exactly_as_the_source_bound_them()
    {
        using var temp = new TempDir();

        // xlink bound as "xlink" rather than the conventional "l".
        string source = new Fb2Builder()
            .WithRootAttributes(
                " xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\""
                + " xmlns:xlink=\"http://www.w3.org/1999/xlink\"")
            .WithDescription(Fb2Builder.DefaultDescription.Replace("l:href", "xlink:href"))
            .WriteTo(temp.File("book.fb2"));

        string target = temp.File("saved.fb2");
        Write(source, target, m => m.Title = "Season of Mists");

        string after = TextOf(target);

        Assert.Contains("xlink:href=\"#cover.png\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("l:href", after.Replace("xlink:href", ""), StringComparison.Ordinal);

        // The description must not have picked up an xmlns of its own.
        Assert.DoesNotContain("<description xmlns", after, StringComparison.Ordinal);
    }

    [Fact]
    public void A_windows_1251_document_round_trips_in_its_own_encoding()
    {
        using var temp = new TempDir();

        Encoding windows1251 = Encoding.GetEncoding(1251);

        string source = new Fb2Builder()
            .WithEncoding(windows1251, "windows-1251")
            .WithDescription(Fb2Builder.MinimalDescription.Replace("Gaiman", "Пелевин"))
            .WriteTo(temp.File("book.fb2"));

        BookMetadata metadata = Read(source, ReadOptions.WithoutCover);
        Assert.Equal("Пелевин", Assert.Single(metadata.PrimaryCreators).Name);

        string target = temp.File("saved.fb2");
        Write(source, target, _ => { });

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    /// <summary>Hard invariant 6 again, for the zipped flavour.</summary>
    [Fact]
    public void Saving_a_zipped_fb2_without_editing_is_byte_identical()
    {
        using var temp = new TempDir();
        string source = new Fb2Builder().WriteZipTo(temp.File("book.fb2.zip"));
        string target = temp.File("saved.fb2.zip");

        using (ZipContainer container = ZipContainer.Open(source))
        {
            var format = new Fb2Format(FormatId.Fb2Zip);
            BookMetadata metadata = format.Read(container, ReadOptions.WithoutCover);
            format.Write(container, metadata, target);
        }

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    [Fact]
    public void An_fb2_inside_a_zip_reads_and_writes()
    {
        using var temp = new TempDir();
        string source = new Fb2Builder().WriteZipTo(temp.File("book.fb2.zip"));
        string target = temp.File("saved.fb2.zip");

        using (ZipContainer container = ZipContainer.Open(source))
        {
            var format = new Fb2Format(FormatId.Fb2Zip);
            BookMetadata metadata = format.Read(container, ReadOptions.WithoutCover);

            Assert.Equal("The Doll's House", metadata.Title);

            metadata.Title = "Season of Mists";
            format.Write(container, metadata, target);
        }

        using ZipContainer saved = ZipContainer.Open(target);
        Assert.Equal(
            "Season of Mists",
            new Fb2Format(FormatId.Fb2Zip).Read(saved, ReadOptions.WithoutCover).Title);
    }

    [Fact]
    public void Detection_and_registration_are_wired_up()
    {
        using var temp = new TempDir();
        string bare = new Fb2Builder().WriteTo(temp.File("book.fb2"));
        string zipped = new Fb2Builder().WriteZipTo(temp.File("book.fb2.zip"));

        DetectedFormat bareFormat = BookFormats.Identify(bare);
        Assert.Equal(FormatId.Fb2, bareFormat.Format);
        Assert.Equal(ContainerKind.Raw, bareFormat.Container);

        DetectedFormat zippedFormat = BookFormats.Identify(zipped);
        Assert.Equal(FormatId.Fb2Zip, zippedFormat.Format);
        Assert.Equal(ContainerKind.Zip, zippedFormat.Container);

        Assert.NotNull(BookFormats.For(FormatId.Fb2));
        Assert.NotNull(BookFormats.For(FormatId.Fb2Zip));
        Assert.True(BookContainers.IsSupported(ContainerKind.Raw));
    }

    /// <summary>The whole path a user takes: open, edit, save, reopen.</summary>
    [Fact]
    public void A_book_round_trips_through_load_and_save()
    {
        using var temp = new TempDir();
        string path = new Fb2Builder().WriteTo(temp.File("book.fb2"));
        byte[] before = File.ReadAllBytes(path);

        Book book = Book.Load(path);

        Assert.Equal(FormatId.Fb2, book.Detected.Format);
        Assert.Equal("The Doll's House", book.Metadata.Title);
        Assert.True(book.CanSave);

        book.Save(keepBackup: false);
        Assert.Equal(before, File.ReadAllBytes(path));

        Book reopened = Book.Load(path);
        reopened.Metadata.Title = "Season of Mists";
        reopened.Save(keepBackup: false);

        Assert.Equal("Season of Mists", Book.Load(path).Metadata.Title);
    }
}
