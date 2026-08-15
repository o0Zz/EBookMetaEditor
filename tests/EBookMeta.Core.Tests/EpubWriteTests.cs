using System.Text;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

public sealed class EpubWriteTests
{
    private static BookMetadata Read(string path)
    {
        using ZipContainer container = ZipContainer.Open(path);
        return new EpubFormat().Read(container);
    }

    private static void Write(string source, string target, Action<BookMetadata> edit)
    {
        using ZipContainer container = ZipContainer.Open(source);
        var format = new EpubFormat();
        BookMetadata metadata = format.Read(container);
        edit(metadata);
        format.Write(container, metadata, target);
    }

    private static string Epub2(TempDir temp) =>
        new EpubBuilder().WithOpf(EpubBuilder.Epub2Opf).WriteTo(temp.File("valid-epub2.epub"));

    /// <summary>Hard invariant 6.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saving_without_editing_is_byte_identical(bool epub2)
    {
        using var temp = new TempDir();
        string source = epub2 ? Epub2(temp) : new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));
        string target = temp.File("saved.epub");

        Write(source, target, _ => { });

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    /// <summary>
    /// Clearing a field has to reach the file. Silently keeping the old value would
    /// be the worst of the three possible behaviours: the user is told nothing and
    /// the grid then disagrees with the disk.
    /// </summary>
    [Fact]
    public void Clearing_a_field_removes_it()
    {
        using var temp = new TempDir();
        string source = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));
        string target = temp.File("saved.epub");

        Write(source, target, m =>
        {
            m.Title = null;
            m.PublicationDate = null;
        });

        BookMetadata reread = Read(target);
        Assert.Null(reread.Title);
        Assert.Null(reread.PublicationDate);
    }

    /// <summary>Hard invariant 7.</summary>
    [Fact]
    public void Mimetype_stays_first_stored_and_exact()
    {
        using var temp = new TempDir();
        string source = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));
        string target = temp.File("saved.epub");

        Write(source, target, m => m.Title = "Something Else");

        using ZipContainer container = ZipContainer.Open(target);
        ContainerEntry mimetype = container.Entries[0];

        Assert.Equal("mimetype", mimetype.Name);
        Assert.Equal(ZipCompressionMethods.Stored, mimetype.CompressionMethod);

        using Stream stream = container.OpenRead(mimetype);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        byte[] content = buffer.ToArray();
        Assert.Equal("application/epub+zip", Encoding.UTF8.GetString(content));
        Assert.Equal(20, content.Length); // no BOM, no trailing newline
    }

    /// <summary>Hard invariant 9.</summary>
    [Fact]
    public void Editing_one_field_changes_one_line_of_the_opf()
    {
        using var temp = new TempDir();
        string source = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));
        string target = temp.File("saved.epub");

        Write(source, target, m => m.Title = "A Completely Different Title");

        string[] before = ReadOpfLines(source);
        string[] after = ReadOpfLines(target);

        Assert.Equal(before.Length, after.Length);

        string[] changed = [.. after.Where((line, i) => line != before[i])];

        Assert.Single(changed);
        Assert.Contains("A Completely Different Title", changed[0], StringComparison.Ordinal);
    }

    /// <summary>Hard invariant 4.</summary>
    [Fact]
    public void Entry_order_and_compression_survive_a_write()
    {
        using var temp = new TempDir();
        string source = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));
        string target = temp.File("saved.epub");

        string[] namesBefore;
        ushort[] methodsBefore;

        using (ZipContainer container = ZipContainer.Open(source))
        {
            namesBefore = container.Entries.Select(e => e.Name).ToArray();
            methodsBefore = container.Entries.Select(e => e.CompressionMethod).ToArray();
        }

        Write(source, target, m => m.Publisher = "New Publisher");

        using ZipContainer written = ZipContainer.Open(target);

        Assert.Equal(namesBefore, written.Entries.Select(e => e.Name).ToArray());
        Assert.Equal(methodsBefore, written.Entries.Select(e => e.CompressionMethod).ToArray());
    }

    /// <summary>
    /// The "never lose a field you do not understand" invariant. An unknown
    /// <c>&lt;meta&gt;</c> survives because nothing goes near it.
    /// </summary>
    [Fact]
    public void Unrecognised_metadata_survives_a_write_verbatim()
    {
        using var temp = new TempDir();
        string source = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));
        string target = temp.File("saved.epub");

        Write(source, target, m => m.Title = "Changed");

        Assert.Contains("""<meta property="custom:mood">wistful</meta>""", ReadOpfText(target));
    }

    /// <summary>
    /// Hard invariant 8: both conventions are written regardless of declared
    /// version, because old and new readers honour different ones.
    /// </summary>
    [Fact]
    public void Series_is_written_in_both_epub2_and_epub3_conventions()
    {
        using var temp = new TempDir();
        string target = temp.File("saved.epub");

        Write(Epub2(temp), target, m => m.Series = new SeriesInfo { Name = "New Series", Index = 3.5m });

        string opf = ReadOpfText(target);

        Assert.Contains("""<meta name="calibre:series" content="New Series"/>""", opf);
        Assert.Contains("""<meta name="calibre:series_index" content="3.5"/>""", opf);
        Assert.Contains("belongs-to-collection", opf);
        Assert.Contains("group-position", opf);

        // A French locale writes 2,5 for two-and-a-half, which no reader parses.
        Assert.DoesNotContain("3,5", opf);

        SeriesInfo? reread = Read(target).Series;
        Assert.Equal("New Series", reread?.Name);
        Assert.Equal(3.5m, reread?.Index);
    }

    [Fact]
    public void Sort_name_and_role_are_written_in_both_conventions()
    {
        using var temp = new TempDir();
        string target = temp.File("saved.epub");

        Write(Epub2(temp), target, m =>
        {
            m.Creators.Clear();
            m.Creators.Add(new Creator
            {
                Name = "Terry Pratchett",
                SortName = "Pratchett, Terry",
                NativeRole = "aut",
            });
        });

        string opf = ReadOpfText(target);

        Assert.Contains("opf:file-as=\"Pratchett, Terry\"", opf, StringComparison.Ordinal);
        Assert.Contains("opf:role=\"aut\"", opf, StringComparison.Ordinal);
        Assert.Contains("property=\"file-as\"", opf, StringComparison.Ordinal);
        Assert.Contains("property=\"role\"", opf, StringComparison.Ordinal);
    }

    [Fact]
    public void Edits_survive_a_read_back()
    {
        using var temp = new TempDir();
        string source = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));
        string target = temp.File("saved.epub");

        Write(source, target, m =>
        {
            m.Title = "Edited Title";
            m.SortTitle = "Edited Title, The";
            m.Publisher = "Gollancz";
            m.Language = "fr";
            m.Description = "A new description.";
        });

        BookMetadata reread = Read(target);

        Assert.Equal("Edited Title", reread.Title);
        Assert.Equal("Edited Title, The", reread.SortTitle);
        Assert.Equal("Gollancz", reread.Publisher);
        Assert.Equal("fr", reread.Language);
        Assert.Equal("A new description.", reread.Description);
    }

    [Fact]
    public void Atomic_writer_leaves_the_original_untouched_when_writing_fails()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));
        byte[] before = File.ReadAllBytes(path);

        Assert.ThrowsAny<Exception>(() =>
            AtomicFileWriter.Write(path, _ => throw new InvalidOperationException("boom")));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"), "the temporary file should be cleaned up");
    }

    [Fact]
    public void Atomic_writer_swaps_in_the_new_file_and_keeps_a_backup_only_when_asked()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));
        byte[] original = File.ReadAllBytes(path);

        string? backup = AtomicFileWriter.Write(
            path, tmp => File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 }), keepBackup: true);

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.NotNull(backup);
        Assert.Equal(original, File.ReadAllBytes(backup!));

        Assert.Null(AtomicFileWriter.Write(
            path, tmp => File.WriteAllBytes(tmp, new byte[] { 9 }), keepBackup: false));
    }

    private static string ReadOpfText(string epubPath)
    {
        using ZipContainer container = ZipContainer.Open(epubPath);
        ContainerEntry entry = container.Entries.Single(e => e.Name.EndsWith(".opf", StringComparison.Ordinal));

        using Stream stream = container.OpenRead(entry);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string[] ReadOpfLines(string epubPath) =>
        ReadOpfText(epubPath).Replace("\r\n", "\n").Split('\n');

    private static byte[] ReadNcx(string epubPath)
    {
        using ZipContainer container = ZipContainer.Open(epubPath);
        return container.ReadAllBytes(
            container.Entries.Single(e => e.Name.EndsWith(".ncx", StringComparison.Ordinal)));
    }

    /// <summary>
    /// EPUB-W062. An EPUB 2 stores the book's identity twice — as the
    /// <c>dc:identifier</c> the package points at, and again as the NCX's
    /// <c>dtb:uid</c> — and OPF 2.0.1 requires them to match. The package is
    /// authoritative, so the NCX is brought into line rather than reported.
    /// </summary>
    [Fact]
    public void Saving_brings_a_stale_toc_identifier_back_into_line()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithOpf(EpubBuilder.Epub2Opf)
            .WithNcx("urn:uuid:stale")
            .WriteTo(temp.File("stale-uid.epub"));

        byte[] before = ReadNcx(path);
        Book.Load(path).Save(keepBackup: false);
        byte[] after = ReadNcx(path);

        string text = Encoding.UTF8.GetString(after);
        Assert.Contains("content=\"urn:uuid:1234\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("urn:uuid:stale", text, StringComparison.Ordinal);

        // A splice, not a reserialisation: only the uid moved, and it is the one
        // difference in the whole document.
        Assert.Equal(
            Encoding.UTF8.GetString(before).Replace("urn:uuid:stale", "urn:uuid:1234"),
            text);
    }

    /// <summary>
    /// A save must not rewrite an NCX that already agrees, or an EPUB 3's legacy
    /// one, which nothing requires to match. Both must come back byte-identical.
    /// </summary>
    [Theory]
    [InlineData(false, "urn:uuid:1234")]
    [InlineData(true, "urn:uuid:stale")]
    public void Saving_leaves_a_toc_it_has_no_business_touching_alone(bool epub3, string uid)
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithOpf(epub3 ? EpubBuilder.Epub3Opf : EpubBuilder.Epub2Opf)
            .WithNcx(uid)
            .WriteTo(temp.File($"toc-{epub3}.epub"));

        byte[] before = ReadNcx(path);
        Book.Load(path).Save(keepBackup: false);

        Assert.Equal(before, ReadNcx(path));
    }

    /// <summary>
    /// The correction with the most direct payoff: readers reject an EPUB outright
    /// until <c>mimetype</c> is the first entry and stored, and both defects are
    /// provable from the file rather than guessed at.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Saving_puts_the_mimetype_entry_back(bool compressed)
    {
        using var temp = new TempDir();
        EpubBuilder builder = compressed
            ? new EpubBuilder().WithCompressedMimetype()
            : new EpubBuilder().WithMimetypeNotFirst();

        string path = builder.WriteTo(temp.File($"broken-mimetype-{compressed}.epub"));

        Book book = Book.Load(path);
        book.Save(keepBackup: false);

        using ZipContainer saved = ZipContainer.Open(path);
        ContainerEntry first = saved.Entries[0];

        Assert.Equal("mimetype", first.Name);
        Assert.Equal(ZipCompressionMethods.Stored, first.CompressionMethod);
    }
}
