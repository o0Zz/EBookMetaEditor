using System.Text;
using EBookMeta.Containers;
using EBookMeta.Xml;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// CBT: the comic metadata document this build already understands, in a TAR.
/// </summary>
public sealed class CbtTests
{
    private static void Write(
        string source, string target, Action<BookMetadata> edit)
    {
        using TarContainer container = TarContainer.Open(source);
        var format = new CbzFormat(FormatId.Cbt);
        BookMetadata metadata = format.Read(container);
        edit(metadata);
        format.Write(container, metadata, target);
    }

    private static BookMetadata Read(string path)
    {
        using TarContainer container = TarContainer.Open(path);
        return new CbzFormat(FormatId.Cbt).Read(container);
    }

    private static string ComicInfoText(string path)
    {
        using TarContainer container = TarContainer.Open(path);
        ContainerEntry entry = container.Entries.Single(e =>
            e.Name.Equals(ComicInfoDocument.DefaultEntryName, StringComparison.OrdinalIgnoreCase));

        using Stream stream = container.OpenRead(entry);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// An archive as GNU tar would write one: an owner and a mode this build has
    /// no field for, and a tail padded to the default twenty-block factor.
    /// </summary>
    private static RawTarBuilder RealisticArchive() =>
        new RawTarBuilder()
            .WithFile("01.png", PngBuilder.OnePixel)
            .WithFile("02.png", PngBuilder.OnePixel)
            .WithFile("03.png", PngBuilder.OnePixel)
            .WithFile(ComicInfoDocument.DefaultEntryName, CbzBuilder.DefaultComicInfo);

    /// <summary>
    /// Every entry's name, content and timestamp.
    /// </summary>
    /// <remarks>
    /// Content as base64 so a tuple compares it by value; a byte[] inside one compares
    /// by reference and every assertion would pass.
    /// </remarks>
    private static (string Name, string Content, DateTimeOffset Modified)[] Contents(string path)
    {
        using TarContainer container = TarContainer.Open(path);

        return [.. container.Entries.Select(e =>
        {
            using Stream stream = container.OpenRead(e);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            return (e.Name, Convert.ToBase64String(buffer.ToArray()), e.LastModified);
        })];
    }

    /// <summary>
    /// Writing is deterministic: an archive this build produced, saved unedited, comes
    /// back byte for byte. It does <em>not</em> hold for an archive from another
    /// producer — see <see cref="Saving_keeps_what_the_writer_can_express"/>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saving_without_editing_is_byte_identical(bool comicInfoLast)
    {
        using var temp = new TempDir();

        CbzBuilder builder = comicInfoLast ? new CbzBuilder().WithComicInfoLast() : new CbzBuilder();
        string source = builder.WriteTo(temp.File("comic.cbt"), ContainerKind.Tar);
        string target = temp.File("saved.cbt");

        Write(source, target, _ => { });

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    /// <summary>
    /// What survives a save of an archive another producer wrote: names, order, content
    /// and timestamps. Its mode, uid, gid, uname, gname and blocking factor do not —
    /// SharpCompress's writer takes a name, a size and a timestamp.
    /// </summary>
    [Fact]
    public void Saving_keeps_what_the_writer_can_express()
    {
        using var temp = new TempDir();
        string source = RealisticArchive().WriteTo(temp.File("comic.cbt"));
        string target = temp.File("saved.cbt");

        // The fixture is what tar produces, so the tail is ten kilobytes rather
        // than the two blocks the writer emits.
        Assert.Equal(0, new FileInfo(source).Length % (20 * 512));

        Write(source, target, _ => { });

        Assert.Equal(Contents(source), Contents(target));
    }

    /// <summary>
    /// Editing the metadata rewrites the metadata, and touches no other entry's name,
    /// content or timestamp.
    /// </summary>
    [Fact]
    public void Editing_leaves_every_other_entry_alone()
    {
        using var temp = new TempDir();

        string source = RealisticArchive().WriteTo(temp.File("comic.cbt"));
        string target = temp.File("saved.cbt");

        Write(source, target, m => m.Title = "Season of Mists");

        static (string Name, string Content, DateTimeOffset Modified)[] Pages(string path) =>
            [.. Contents(path).Where(e =>
                !e.Name.Equals(ComicInfoDocument.DefaultEntryName, StringComparison.OrdinalIgnoreCase))];

        Assert.Equal(Pages(source), Pages(target));
        Assert.Contains("Season of Mists", ComicInfoText(target), StringComparison.Ordinal);
    }

    /// <summary>
    /// How comics in the wild are packed. The folder marker is dropped on save —
    /// SharpCompress writes a directory header with no <c>ustar</c> magic, and a comic
    /// whose first entry is its page folder would come back unrecognisable to
    /// <see cref="BookContainers.Sniff"/>. The pages carry the structure in their names.
    /// </summary>
    [Fact]
    public void A_comic_whose_pages_sit_in_a_folder_reopens_after_a_save()
    {
        using var temp = new TempDir();

        string source = new RawTarBuilder()
            .WithDirectory("pages")
            .WithFile("pages/01.png", PngBuilder.OnePixel)
            .WithFile("pages/02.png", PngBuilder.OnePixel)
            .WithFile("pages/03.png", PngBuilder.OnePixel)
            .WithFile(ComicInfoDocument.DefaultEntryName, CbzBuilder.DefaultComicInfo)
            .WriteTo(temp.File("comic.cbt"));

        Assert.True(Contents(source)[0].Name is "pages" or "pages/");

        Book book = Book.Load(source);
        book.Metadata.Title = "Season of Mists";
        book.Save(keepBackup: false);

        // The whole point: the saved file is still a TAR as far as detection is
        // concerned, and still a comic.
        Book reopened = Book.Load(source);

        Assert.Equal(FormatId.Cbt, reopened.Detected.Format);
        Assert.Equal("Season of Mists", reopened.Metadata.Title);

        Assert.Equal(
            ["pages/01.png", "pages/02.png", "pages/03.png", ComicInfoDocument.DefaultEntryName],
            Contents(source).Select(e => e.Name));
    }

    [Fact]
    public void Entry_order_is_preserved()
    {
        using var temp = new TempDir();

        string source = new CbzBuilder()
            .WithPages("01.png", "02.png", "03.png")
            .WithComicInfoLast()
            .WriteTo(temp.File("comic.cbt"), ContainerKind.Tar);

        string target = temp.File("saved.cbt");
        Write(source, target, m => m.Title = "Season of Mists");

        using TarContainer saved = TarContainer.Open(target);

        Assert.Equal(
            ["01.png", "02.png", "03.png", ComicInfoDocument.DefaultEntryName],
            saved.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Reads_the_metadata_document()
    {
        using var temp = new TempDir();
        string path = RealisticArchive().WriteTo(temp.File("comic.cbt"));

        BookMetadata metadata = Read(path);

        Assert.Equal("The Doll's House", metadata.Title);
        Assert.Equal("The Sandman", metadata.Series?.Name);
        Assert.Contains(metadata.Creators, c => c.Name == "Neil Gaiman");
    }

    /// <summary>CBZ-E020 reaches a TAR unchanged, because the rules are the document's.</summary>
    [Fact]
    public void Saving_recomputes_the_page_count()
    {
        using var temp = new TempDir();

        string source = new CbzBuilder()
            .WithComicInfo(CbzBuilder.MinimalComicInfo)
            .WriteTo(temp.File("comic.cbt"), ContainerKind.Tar);

        string target = temp.File("saved.cbt");

        Write(source, target, _ => { });

        Assert.Contains("<PageCount>3</PageCount>", ComicInfoText(target), StringComparison.Ordinal);
    }

    /// <summary>CBZ-W010: an untagged comic gains a document on save.</summary>
    [Fact]
    public void Adds_a_metadata_document_when_the_archive_has_none()
    {
        using var temp = new TempDir();

        string source = new CbzBuilder()
            .WithoutComicInfo()
            .WriteTo(temp.File("comic.cbt"), ContainerKind.Tar);

        string target = temp.File("saved.cbt");

        using (TarContainer container = TarContainer.Open(source))
        {
            var format = new CbzFormat(FormatId.Cbt);
            BookMetadata metadata = format.Read(container, null);
            metadata.Title = "The Doll's House";
            format.Write(container, metadata, target);
        }

        Assert.Equal("The Doll's House", Read(target).Title);

        // Appended, so the pages keep the order that is their reading order.
        using TarContainer saved = TarContainer.Open(target);
        Assert.Equal(ComicInfoDocument.DefaultEntryName, saved.Entries[saved.Entries.Count - 1].Name);
    }

    /// <summary>
    /// A name too long for the 100-byte field, split across the ustar prefix.
    /// </summary>
    [Fact]
    public void Reads_a_name_split_across_the_ustar_prefix()
    {
        using var temp = new TempDir();

        const string LongName =
            "the-sandman-volume-two-the-dolls-house/scanned-at-600-dpi-by-a-very-patient-person/"
            + "chapter-one/01.png";

        Assert.True(LongName.Length > 100);

        string path = new RawTarBuilder()
            .WithFile(LongName, PngBuilder.OnePixel)
            .WriteTo(temp.File("comic.cbt"));

        using TarContainer container = TarContainer.Open(path);

        Assert.Equal(LongName, container.Entries.Single().Name);
    }

    /// <summary>The same, as GNU tar spells it: an <c>L</c> block carrying the name.</summary>
    [Fact]
    public void Reads_a_gnu_long_name()
    {
        using var temp = new TempDir();

        string longName = new string('a', 120) + ".png";

        string source = new RawTarBuilder()
            .WithGnuLongNamedFile(longName, PngBuilder.OnePixel)
            .WithFile("02.png", PngBuilder.OnePixel)
            .WithFile("03.png", PngBuilder.OnePixel)
            .WithFile(ComicInfoDocument.DefaultEntryName, CbzBuilder.DefaultComicInfo)
            .WriteTo(temp.File("comic.cbt"));

        using TarContainer container = TarContainer.Open(source);

        Assert.Equal(longName, container.Entries[0].Name);
    }

    /// <summary>
    /// CBT-F001: a name this build's writer cannot express. SharpCompress never fills
    /// the ustar prefix field, so anything over 100 bytes is refused rather than written
    /// under a name that is not the one it arrived with.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Saving_refuses_a_name_too_long_for_the_header(bool splittable)
    {
        using var temp = new TempDir();

        // Splittable or not makes no difference: the prefix field is never used.
        string longName = splittable
            ? "the-sandman/the-dolls-house/scanned-at-600-dpi-by-a-patient-person/"
                + new string('a', 40) + ".png"
            : new string('a', 120) + ".png";

        Assert.True(longName.Length > 100);

        string source = new RawTarBuilder()
            .WithGnuLongNamedFile(longName, PngBuilder.OnePixel)
            .WithFile("02.png", PngBuilder.OnePixel)
            .WithFile("03.png", PngBuilder.OnePixel)
            .WithFile(ComicInfoDocument.DefaultEntryName, CbzBuilder.DefaultComicInfo)
            .WriteTo(temp.File("comic.cbt"));

        string target = temp.File("saved.cbt");

        BookFormatException error = Assert.Throws<BookFormatException>(
            () => Write(source, target, m => m.Title = "Season of Mists"));

        Assert.Contains(longName, error.Message, StringComparison.Ordinal);
        Assert.Contains(
            Log.Entries, e => e.Message.StartsWith("CBT-F001:", StringComparison.Ordinal));

        // The refusal happens before the source is touched.
        Assert.Equal(longName, Contents(source)[0].Name);
    }

    [Fact]
    public void Entry_content_reads_back_intact()
    {
        using var temp = new TempDir();

        byte[] content = Encoding.UTF8.GetBytes(new string('x', 1500));

        string path = new RawTarBuilder()
            .WithFile("01.png", PngBuilder.OnePixel)
            .WithFile("notes.txt", content)
            .WriteTo(temp.File("comic.cbt"));

        using TarContainer container = TarContainer.Open(path);
        ContainerEntry entry = container.Entries.Single(e => e.Name == "notes.txt");

        using Stream stream = container.OpenRead(entry);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        // Spans three blocks, so the padding of the last one must not leak in.
        Assert.Equal(content, buffer.ToArray());
    }

    /// <summary>
    /// The checksum is TAR's only structural check, and the only thing standing
    /// between a mis-sniffed file and being read as though it held entries.
    /// </summary>
    [Fact]
    public void A_file_that_is_not_a_tar_is_refused()
    {
        using var temp = new TempDir();

        byte[] rubbish = new byte[2048];
        for (int i = 0; i < rubbish.Length; i++)
        {
            rubbish[i] = (byte)(i % 251);
        }

        string path = temp.File("not-a.cbt");
        File.WriteAllBytes(path, rubbish);

        Assert.Throws<BookFormatException>(() => TarContainer.Open(path));
    }

    [Fact]
    public void A_tar_is_detected_as_a_comic_archive()
    {
        using var temp = new TempDir();
        string path = RealisticArchive().WriteTo(temp.File("comic.cbt"));

        DetectedFormat detected = BookFormats.Identify(path);

        Assert.Equal(FormatId.Cbt, detected.Format);
        Assert.Equal(ContainerKind.Tar, detected.Container);
        Assert.True(detected.ExtensionAgrees);
    }

    /// <summary>GEN-W002: the extension says one thing and the bytes another.</summary>
    [Fact]
    public void A_tar_named_cbz_is_reported()
    {
        using var temp = new TempDir();
        string path = RealisticArchive().WriteTo(temp.File("comic.cbz"));

        Log.Clear();
        Book book = Book.Load(path);

        Assert.Equal(FormatId.Cbt, book.Detected.Format);
        Assert.Equal(FormatId.Cbz, book.Detected.ClaimedByExtension);
        Assert.False(book.Detected.ExtensionAgrees);

        Assert.Contains(
            Log.Entries,
            e => e.Message.StartsWith("GEN-W002:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_format_is_registered_and_writable()
    {
        IBookFormat? format = BookFormats.For(FormatId.Cbt);

        Assert.NotNull(format);
        Assert.True(format!.Capabilities.CanWrite);
    }

    /// <summary>The whole path a user takes: open, save, everything still there.</summary>
    [Fact]
    public void A_book_round_trips_through_load_and_save()
    {
        using var temp = new TempDir();
        string path = RealisticArchive().WriteTo(temp.File("comic.cbt"));
        var before = Contents(path);

        Book book = Book.Load(path);

        Assert.Equal(FormatId.Cbt, book.Detected.Format);
        Assert.Equal("The Doll's House", book.Metadata.Title);
        Assert.True(book.CanSave);

        book.Save(keepBackup: false);

        Assert.Equal(before, Contents(path));
        Assert.Equal("The Doll's House", Book.Load(path).Metadata.Title);
    }

    [Fact]
    public void A_book_saves_an_edit()
    {
        using var temp = new TempDir();
        string path = RealisticArchive().WriteTo(temp.File("comic.cbt"));

        Book book = Book.Load(path);
        book.Metadata.Title = "Season of Mists";
        book.Save(keepBackup: false);

        Assert.Equal("Season of Mists", Book.Load(path).Metadata.Title);
    }
}
