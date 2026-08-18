using System.Text;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// CB7: the comic metadata document this build already understands, in a 7z.
/// </summary>
public sealed class Cb7Tests : IDisposable
{
    /// <summary>Cleared per test so a rule assertion sees only this test's entries.</summary>
    public Cb7Tests()
    {
        Log.Clear();
        NoArchiver();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Log.Clear();
        NoArchiver();
    }

    /// <summary>
    /// Says "this machine has no archiver" and means it — otherwise every refusal test
    /// would fail on a machine that has 7-Zip installed.
    /// </summary>
    private static void NoArchiver() => SevenZipContainer.Locator = () => null;

    /// <summary>Points the container at the stand-in archiver.</summary>
    private static void UseStandIn() => SevenZipContainer.Locator = () => StandInArchiver.Path();

    /// <summary>Three pages and a document, as a tagged comic in the wild would be.</summary>
    private static SevenZipBuilder Comic() =>
        new SevenZipBuilder()
            .WithFile("01.png", PngBuilder.OnePixel)
            .WithFile("02.png", PngBuilder.OnePixel)
            .WithFile("03.png", PngBuilder.OnePixel)
            .WithFile(ComicInfoDocument.DefaultEntryName, CbzBuilder.DefaultComicInfo);

    private static BookMetadata Read(string path)
    {
        using SevenZipContainer container = SevenZipContainer.Open(path);
        return new CbzFormat(FormatId.Cb7).Read(container);
    }

    private static bool Logged(string ruleId) =>
        Log.Entries.Any(e => e.Message.StartsWith(ruleId + ":", StringComparison.Ordinal));

    // ---- reading -----------------------------------------------------------

    [Fact]
    public void Reads_the_metadata_document()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cb7"));

        BookMetadata metadata = Read(path);

        Assert.Equal("The Doll's House", metadata.Title);
        Assert.Equal("The Sandman", metadata.Series?.Name);
        Assert.Equal(2.5m, metadata.Series?.Index);
        Assert.Contains(metadata.Creators, c => c.Name == "Neil Gaiman");
        Assert.Equal("DC Comics", metadata.Publisher);
    }

    [Fact]
    public void Entry_order_is_preserved()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cb7"));

        using SevenZipContainer container = SevenZipContainer.Open(path);

        Assert.Equal(
            ["01.png", "02.png", "03.png", ComicInfoDocument.DefaultEntryName],
            container.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Entry_content_reads_back_intact()
    {
        using var temp = new TempDir();

        // Longer than any buffer boundary the reader is likely to have, so a short
        // read would show up here rather than in a one-pixel PNG.
        byte[] content = Encoding.UTF8.GetBytes(new string('x', 9000));

        string path = new SevenZipBuilder()
            .WithFile("01.png", PngBuilder.OnePixel)
            .WithFile("notes.txt", content)
            .WriteTo(temp.File("comic.cb7"));

        using SevenZipContainer container = SevenZipContainer.Open(path);
        ContainerEntry entry = container.Entries.Single(e => e.Name == "notes.txt");

        Assert.Equal(content.Length, entry.Length);
        Assert.Equal(content, container.ReadAllBytes(entry));
    }

    /// <summary>
    /// One compression block across every file, which is what 7-Zip packs by default.
    /// Unlike a solid RAR it is read rather than refused, so this is the shape most
    /// comics in the wild actually have.
    /// </summary>
    [Fact]
    public void A_solid_archive_reads_every_entry()
    {
        using var temp = new TempDir();
        string path = Comic().Solid().WriteTo(temp.File("solid.cb7"));

        using SevenZipContainer container = SevenZipContainer.Open(path);

        Assert.Equal(
            ["01.png", "02.png", "03.png", ComicInfoDocument.DefaultEntryName],
            container.Entries.Select(e => e.Name));

        // The last entry of the block, which is the one a naive reader loses.
        Assert.Equal(
            CbzBuilder.DefaultComicInfo,
            Encoding.UTF8.GetString(container.ReadAllBytes(container.Entries[3])));

        Assert.Equal(PngBuilder.OnePixel, container.ReadAllBytes(container.Entries[1]));
    }

    /// <summary>
    /// 7z records a Windows path with backslashes, and everything above the container
    /// is written against forward slashes.
    /// </summary>
    [Fact]
    public void A_windows_path_is_normalised_to_forward_slashes()
    {
        using var temp = new TempDir();

        string path = new SevenZipBuilder()
            .WithFile(@"the-dolls-house\01.png", PngBuilder.OnePixel)
            .WithFile(@"the-dolls-house\ComicInfo.xml", CbzBuilder.MinimalComicInfo)
            .WriteTo(temp.File("comic.cb7"));

        using SevenZipContainer container = SevenZipContainer.Open(path);

        Assert.Equal(
            ["the-dolls-house/01.png", "the-dolls-house/ComicInfo.xml"],
            container.Entries.Select(e => e.Name));
    }

    [Fact]
    public void An_untagged_comic_reads_as_empty_rather_than_failing()
    {
        using var temp = new TempDir();

        string path = new SevenZipBuilder()
            .WithFile("01.png", PngBuilder.OnePixel)
            .WithFile("02.png", PngBuilder.OnePixel)
            .WriteTo(temp.File("comic.cb7"));

        Assert.Null(Read(path).Title);
    }

    [Fact]
    public void The_cover_is_the_first_page()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cb7"));

        using SevenZipContainer container = SevenZipContainer.Open(path);
        BookMetadata metadata = new CbzFormat(FormatId.Cb7).Read(container);

        Assert.Equal("01.png", metadata.Cover?.SourceEntryName);
        Assert.Equal("image/png", metadata.Cover?.MediaType);
        Assert.Equal(PngBuilder.OnePixel, metadata.Cover?.Data);
    }

    /// <summary>The container reports the folder marker rather than flattening it.</summary>
    [Fact]
    public void A_directory_entry_is_reported_as_one()
    {
        using var temp = new TempDir();

        string path = new SevenZipBuilder()
            .WithFile(@"My Comic T06\01.png", PngBuilder.OnePixel)
            .WithDirectory(@"My Comic T06")
            .WriteTo(temp.File("comic.cb7"));

        using SevenZipContainer container = SevenZipContainer.Open(path);

        ContainerEntry folder = container.Entries.Single(e => e.IsDirectory);

        // No trailing separator, which is why staging cannot spot it from the name.
        Assert.Equal("My Comic T06", folder.Name);
        Assert.Equal(2, container.Entries.Count);
    }

    // ---- detection ---------------------------------------------------------

    [Fact]
    public void A_7z_is_detected_as_a_comic_archive()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cb7"));

        DetectedFormat detected = BookFormats.Identify(path);

        Assert.Equal(FormatId.Cb7, detected.Format);
        Assert.Equal(ContainerKind.SevenZip, detected.Container);
        Assert.True(detected.ExtensionAgrees);
    }

    /// <summary>
    /// The case that used to be a refusal: a 7z wearing a <c>.cbz</c> extension.
    /// </summary>
    [Fact]
    public void A_7z_named_cbz_opens_and_is_still_reported()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("7z-disguised-as-cbz.cbz"));

        Book book = Book.Load(path);

        Assert.Equal(FormatId.Cb7, book.Detected.Format);
        Assert.Equal(FormatId.Cbz, book.Detected.ClaimedByExtension);
        Assert.False(book.Detected.ExtensionAgrees);
        Assert.Equal("The Doll's House", book.Metadata.Title);

        Assert.True(Logged("GEN-W002"));
    }

    [Fact]
    public void The_format_is_registered()
    {
        IBookFormat? format = BookFormats.For(FormatId.Cb7);

        Assert.NotNull(format);
        Assert.Equal(FormatId.Cb7, format!.Id);
        Assert.Contains(".cb7", format.Extensions);
    }

    // ---- writing -----------------------------------------------------------

    /// <summary>The container says what it cannot do; the format does not.</summary>
    [Fact]
    public void The_format_can_write_even_though_the_container_may_not()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cb7"));

        Assert.True(BookFormats.For(FormatId.Cb7)!.Capabilities.CanWrite);

        using SevenZipContainer container = SevenZipContainer.Open(path);
        Assert.False(container.IsWritable);
    }

    /// <summary>CB7-F002, and hard invariant 1 with it.</summary>
    [Fact]
    public void Saving_is_refused_when_no_archiver_is_configured()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cb7"));
        byte[] before = File.ReadAllBytes(path);

        Book book = Book.Load(path);

        // The edit is accepted. Nothing before the save has any reason to object.
        Assert.True(book.CanSave);
        book.Metadata.Title = "Season of Mists";

        BookFormatException ex = Assert.Throws<BookFormatException>(() => book.Save(keepBackup: false));

        Assert.Contains("7z", ex.Message, StringComparison.Ordinal);
        Assert.True(Logged("CB7-F002"));

        // The original, and no debris beside it.
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".bak"));
    }

    /// <summary>The switches handed to the archiver, asserted without one installed.</summary>
    [Theory]
    [InlineData(true, "-mx0")]
    [InlineData(false, "-mx5")]
    public void The_command_line_asks_for_what_the_source_used(bool stored, string expected)
    {
        string arguments = SevenZipContainer.Archiver.Arguments(@"C:\books\comic.cb7.tmp", stored);

        Assert.Contains("-t7z", arguments, StringComparison.Ordinal);
        Assert.Contains(expected, arguments, StringComparison.Ordinal);

        // The list file is UTF-16, so the archiver has to be told to read it that way.
        Assert.Contains("-scsUTF-16LE", arguments, StringComparison.Ordinal);

        // And no -- separator, unlike RAR: 7-Zip's stops list-file parsing too, which
        // would turn the list file into the name of a file to add.
        Assert.DoesNotContain(" -- ", arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole save. The stand-in writes a manifest of what it was handed instead of
    /// compressing, so that manifest is the assertion.
    /// </summary>
    [Fact]
    public void A_save_hands_every_entry_to_the_archiver_and_replaces_the_file()
    {
        using var temp = new TempDir();

        string path = new SevenZipBuilder()
            .WithFile("01.png", PngBuilder.OnePixel)
            .WithFile(@"sub\02.png", PngBuilder.OnePixel)
            .WithFile(ComicInfoDocument.DefaultEntryName, CbzBuilder.DefaultComicInfo)
            .WriteTo(temp.File("comic.cb7"));

        UseStandIn();

        Book book = Book.Load(path);
        book.Metadata.Title = "Season of Mists";
        book.Save(keepBackup: false);

        string[] manifest = File.ReadAllLines(path);

        Assert.Equal(
            ["01.png=67", @"sub\02.png=67"],
            manifest.Where(line => line.EndsWith(".png=67", StringComparison.Ordinal)));

        // The metadata document was staged too, and carries the edit rather than
        // whatever was on disk.
        Assert.Contains(manifest, line => line.StartsWith("ComicInfo.xml=", StringComparison.Ordinal));
        Assert.DoesNotContain(manifest, line => line.Contains("MISSING"));

        // Three entries and no fourth: the list file the archiver read must not
        // have found its way into the archive alongside them.
        Assert.Equal(3, manifest.Count(line => line.Length > 0));

        Assert.False(Directory.Exists(path + ".tmp.stage"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    /// <summary>
    /// Everything that can go wrong with running someone else's program comes out
    /// as one answer: the save failed.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("not-a-program")]
    public void A_failing_archiver_fails_the_save_generically(string kind)
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cb7"));
        byte[] before = File.ReadAllBytes(path);

        if (kind == "missing")
        {
            SevenZipContainer.Locator = () => Path.Combine(temp.Path, "no-such-archiver.exe");
        }
        else
        {
            string notAProgram = temp.File("not-a-program.exe");
            File.WriteAllText(notAProgram, "this is not a Win32 application");
            SevenZipContainer.Locator = () => notAProgram;
        }

        Book book = Book.Load(path);
        book.Metadata.Title = "Season of Mists";

        Assert.Throws<BookIoException>(() => book.Save(keepBackup: false));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(Directory.Exists(path + ".tmp.stage"));
    }

    // ---- refusals ----------------------------------------------------------

    /// <summary>CB7-F001: nowhere to ask for a password.</summary>
    [Fact]
    public void An_encrypted_archive_is_refused()
    {
        using var temp = new TempDir();
        string path = Comic().Encrypted().WriteTo(temp.File("encrypted.cb7"));

        BookFormatException ex =
            Assert.Throws<BookFormatException>(() => SevenZipContainer.Open(path));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Logged("CB7-F001"));
    }

    /// <summary>
    /// The magic number alone is not an archive, and a file that has only that must
    /// fail as a damaged 7z rather than crash out of the dependency.
    /// </summary>
    [Fact]
    public void A_damaged_7z_is_refused_as_a_format_error()
    {
        using var temp = new TempDir();
        string path = temp.File("damaged.cb7");

        File.WriteAllBytes(path, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, .. new byte[64]]);

        // Claimed, because a damaged file is still its own format's file, and then
        // refused on the way in rather than reported as something unsupported.
        Assert.Equal(FormatId.Cb7, BookFormats.Identify(path).Format);
        Assert.Throws<BookFormatException>(() => Book.Load(path));
    }
}
