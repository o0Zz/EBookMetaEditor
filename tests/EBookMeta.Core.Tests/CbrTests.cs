using System.Text;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// CBR: the comic metadata document this build already understands, in a RAR.
/// </summary>
public sealed class CbrTests : IDisposable
{
    /// <summary>Cleared per test so a rule assertion sees only this test's entries.</summary>
    public CbrTests()
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
    /// would fail on a machine that has WinRAR installed.
    /// </summary>
    private static void NoArchiver() => RarContainer.Locator = () => null;

    /// <summary>Points the container at the stand-in archiver.</summary>
    private static void UseStandIn() => RarContainer.Locator = () => StandInArchiver.Path();

    /// <summary>Three pages and a document, as a tagged comic in the wild would be.</summary>
    private static RarBuilder Comic() =>
        new RarBuilder()
            .WithFile("01.png", PngBuilder.OnePixel)
            .WithFile("02.png", PngBuilder.OnePixel)
            .WithFile("03.png", PngBuilder.OnePixel)
            .WithFile(ComicInfoDocument.DefaultEntryName, CbzBuilder.DefaultComicInfo);

    private static BookMetadata Read(string path)
    {
        using RarContainer container = RarContainer.Open(path);
        return new CbzFormat(FormatId.Cbr).Read(container);
    }

    private static bool Logged(string ruleId) =>
        Log.Entries.Any(e => e.Message.StartsWith(ruleId + ":", StringComparison.Ordinal));

    // ---- reading -----------------------------------------------------------

    [Fact]
    public void Reads_the_metadata_document()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cbr"));

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
        string path = Comic().WriteTo(temp.File("comic.cbr"));

        using RarContainer container = RarContainer.Open(path);

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

        string path = new RarBuilder()
            .WithFile("01.png", PngBuilder.OnePixel)
            .WithFile("notes.txt", content)
            .WriteTo(temp.File("comic.cbr"));

        using RarContainer container = RarContainer.Open(path);
        ContainerEntry entry = container.Entries.Single(e => e.Name == "notes.txt");

        Assert.Equal(content.Length, entry.Length);
        Assert.Equal(content, container.ReadAllBytes(entry));
    }

    /// <summary>
    /// RAR records a Windows path with backslashes, and everything above the
    /// container is written against forward slashes.
    /// </summary>
    [Fact]
    public void A_windows_path_is_normalised_to_forward_slashes()
    {
        using var temp = new TempDir();

        string path = new RarBuilder()
            .WithFile(@"the-dolls-house\01.png", PngBuilder.OnePixel)
            .WithFile(@"the-dolls-house\ComicInfo.xml", CbzBuilder.MinimalComicInfo)
            .WriteTo(temp.File("comic.cbr"));

        using RarContainer container = RarContainer.Open(path);

        Assert.Equal(
            ["the-dolls-house/01.png", "the-dolls-house/ComicInfo.xml"],
            container.Entries.Select(e => e.Name));
    }

    [Fact]
    public void An_untagged_comic_reads_as_empty_rather_than_failing()
    {
        using var temp = new TempDir();

        string path = new RarBuilder()
            .WithFile("01.png", PngBuilder.OnePixel)
            .WithFile("02.png", PngBuilder.OnePixel)
            .WriteTo(temp.File("comic.cbr"));

        Assert.Null(Read(path).Title);
    }

    [Fact]
    public void The_cover_is_the_first_page()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cbr"));

        using RarContainer container = RarContainer.Open(path);
        BookMetadata metadata = new CbzFormat(FormatId.Cbr).Read(container);

        Assert.Equal("01.png", metadata.Cover?.SourceEntryName);
        Assert.Equal("image/png", metadata.Cover?.MediaType);
        Assert.Equal(PngBuilder.OnePixel, metadata.Cover?.Data);
    }

    // ---- detection ---------------------------------------------------------

    [Fact]
    public void A_rar_is_detected_as_a_comic_archive()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cbr"));

        DetectedFormat detected = BookFormats.Identify(path);

        Assert.Equal(FormatId.Cbr, detected.Format);
        Assert.Equal(ContainerKind.Rar, detected.Container);
        Assert.True(detected.ExtensionAgrees);
    }

    /// <summary>
    /// The case that used to be a refusal: a RAR wearing a <c>.cbz</c> extension.
    /// </summary>
    [Fact]
    public void A_rar_named_cbz_opens_and_is_still_reported()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("rar-disguised-as-cbz.cbz"));

        Book book = Book.Load(path);

        Assert.Equal(FormatId.Cbr, book.Detected.Format);
        Assert.Equal(FormatId.Cbz, book.Detected.ClaimedByExtension);
        Assert.False(book.Detected.ExtensionAgrees);
        Assert.Equal("The Doll's House", book.Metadata.Title);

        Assert.True(Logged("GEN-W002"));
    }

    [Fact]
    public void The_format_is_registered()
    {
        IBookFormat? format = BookFormats.For(FormatId.Cbr);

        Assert.NotNull(format);
        Assert.Equal(FormatId.Cbr, format!.Id);
        Assert.Contains(".cbr", format.Extensions);
    }

    // ---- writing -----------------------------------------------------------

    /// <summary>The container says what it cannot do; the format does not.</summary>
    [Fact]
    public void The_format_can_write_even_though_the_container_may_not()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cbr"));

        Assert.True(BookFormats.For(FormatId.Cbr)!.Capabilities.CanWrite);

        using RarContainer container = RarContainer.Open(path);
        Assert.False(container.IsWritable);
    }

    /// <summary>
    /// The container is writable exactly when the host has given it an archiver,
    /// and no further than that.
    /// </summary>
    [Fact]
    public void Writability_follows_whether_an_archiver_was_found()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cbr"));

        using RarContainer container = RarContainer.Open(path);

        Assert.False(container.IsWritable);

        RarContainer.Locator = () => @"Z:\nothing\here\rar.exe";
        Assert.True(container.IsWritable);
    }

    /// <summary>CBR-F002, and hard invariant 1 with it.</summary>
    [Fact]
    public void Saving_is_refused_when_no_archiver_is_configured()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cbr"));
        byte[] before = File.ReadAllBytes(path);

        Book book = Book.Load(path);

        // The edit is accepted. Nothing before the save has any reason to object.
        Assert.True(book.CanSave);
        book.Metadata.Title = "Season of Mists";

        BookFormatException ex = Assert.Throws<BookFormatException>(() => book.Save(keepBackup: false));

        Assert.Contains("RAR", ex.Message, StringComparison.Ordinal);
        Assert.True(Logged("CBR-F002"));

        // The original, and no debris beside it.
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Rebuild_is_refused_before_it_writes_anything()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cbr"));
        string target = temp.File("saved.cbr");

        using RarContainer container = RarContainer.Open(path);

        Assert.Throws<BookFormatException>(() =>
            container.Rebuild([PendingEntry.FromBytes("x.txt", [1, 2, 3])], target));

        Assert.False(File.Exists(target));
    }

    // ---- writing through an external archiver -------------------------------

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
        string path = Comic().WriteTo(temp.File("comic.cbr"));
        byte[] before = File.ReadAllBytes(path);

        if (kind == "missing")
        {
            RarContainer.Locator = () => Path.Combine(temp.Path, "no-such-archiver.exe");
        }
        else
        {
            string notAProgram = temp.File("not-a-program.exe");
            File.WriteAllText(notAProgram, "this is not a Win32 application");
            RarContainer.Locator = () => notAProgram;
        }

        Book book = Book.Load(path);
        book.Metadata.Title = "Season of Mists";

        Assert.Throws<BookIoException>(() => book.Save(keepBackup: false));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(Directory.Exists(path + ".tmp.stage"));
    }

    /// <summary>The switches handed to the archiver, asserted without one installed.</summary>
    [Theory]
    [InlineData(true, "-m0")]
    [InlineData(false, "-m3")]
    public void The_command_line_asks_for_what_the_source_used(bool stored, string expected)
    {
        string arguments = RarContainer.Archiver.Arguments(@"C:\books\comic.cbr.tmp", stored);

        Assert.StartsWith("a ", arguments, StringComparison.Ordinal);
        Assert.Contains(expected, arguments, StringComparison.Ordinal);

        // Everything after -- is a path, so a page named like a switch cannot
        // become one, and the names arrive through a list file rather than the
        // command line, which three hundred of them would overflow.
        Assert.Contains(@"-- ""C:\books\comic.cbr.tmp"" @""", arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole save. The stand-in writes a manifest of what it was handed instead of
    /// compressing, so that manifest is the assertion.
    /// </summary>
    [Fact]
    public void A_save_hands_every_entry_to_the_archiver_and_replaces_the_file()
    {
        using var temp = new TempDir();

        string path = new RarBuilder()
            .WithFile("01.png", PngBuilder.OnePixel)
            .WithFile(@"sub\02.png", PngBuilder.OnePixel)
            .WithFile(ComicInfoDocument.DefaultEntryName, CbzBuilder.DefaultComicInfo)
            .WriteTo(temp.File("comic.cbr"));

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
    /// A comic whose pages sit in a folder, which is how they are packed in the wild.
    /// The folder marker must not reach the archiver as a file.
    /// </summary>
    [Fact]
    public void A_save_of_a_comic_whose_pages_sit_in_a_folder_skips_the_folder_marker()
    {
        using var temp = new TempDir();

        string path = new RarBuilder()
            .WithFile(@"My Comic T06\01.png", PngBuilder.OnePixel)
            .WithFile(@"My Comic T06\02.png", PngBuilder.OnePixel)
            .WithDirectory(@"My Comic T06")
            .WriteTo(temp.File("comic.cbr"));

        UseStandIn();

        Book book = Book.Load(path);
        book.Metadata.Title = "Season of Mists";
        book.Save(keepBackup: false);

        string[] manifest = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();

        Assert.Equal(
            [@"My Comic T06\01.png=67", @"My Comic T06\02.png=67"],
            manifest.Where(line => line.EndsWith(".png=67", StringComparison.Ordinal)));

        Assert.Contains(manifest, line => line.StartsWith("ComicInfo.xml=", StringComparison.Ordinal));

        // Two pages and the document; no fourth entry for the folder marker.
        Assert.Equal(3, manifest.Length);
        Assert.DoesNotContain(manifest, line => line.Contains("MISSING"));

        Assert.False(Directory.Exists(path + ".tmp.stage"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    /// <summary>The container reports the folder marker rather than flattening it.</summary>
    [Fact]
    public void A_directory_entry_is_reported_as_one()
    {
        using var temp = new TempDir();

        string path = new RarBuilder()
            .WithFile(@"My Comic T06\01.png", PngBuilder.OnePixel)
            .WithDirectory(@"My Comic T06")
            .WriteTo(temp.File("comic.cbr"));

        using RarContainer container = RarContainer.Open(path);

        ContainerEntry folder = container.Entries.Single(e => e.IsDirectory);

        // No trailing separator, which is why staging cannot spot it from the name.
        Assert.Equal("My Comic T06", folder.Name);
        Assert.Equal(2, container.Entries.Count);
    }

    /// <summary>
    /// An archiver that runs and reports failure leaves the user's file alone.
    /// </summary>
    [Fact]
    public void An_archiver_that_returns_a_failure_leaves_the_original_alone()
    {
        using var temp = new TempDir();
        string path = Comic().WriteTo(temp.File("comic.cbr"));
        byte[] before = File.ReadAllBytes(path);

        UseStandIn();
        Environment.SetEnvironmentVariable(StandInArchiver.ExitCodeVariable, "3");

        try
        {
            Book book = Book.Load(path);
            book.Metadata.Title = "Season of Mists";

            Assert.Throws<BookIoException>(() => book.Save(keepBackup: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StandInArchiver.ExitCodeVariable, null);
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(Directory.Exists(path + ".tmp.stage"));
    }

    // ---- refusals ----------------------------------------------------------

    /// <summary>
    /// CBR-F001. A solid archive is one compression stream across every file, so an
    /// entry cannot be served on its own and the metadata document cannot be reached.
    /// </summary>
    [Fact]
    public void A_solid_archive_is_refused()
    {
        using var temp = new TempDir();
        string path = Comic().Solid().WriteTo(temp.File("solid.cbr"));

        BookFormatException ex =
            Assert.Throws<BookFormatException>(() => RarContainer.Open(path));

        Assert.Contains("solid", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Logged("CBR-F001"));
    }

    /// <summary>CBR-F001, the other half: nowhere to ask for a password.</summary>
    [Fact]
    public void An_encrypted_archive_is_refused()
    {
        using var temp = new TempDir();
        string path = Comic().Encrypted().WriteTo(temp.File("encrypted.cbr"));

        Assert.Throws<BookFormatException>(() => RarContainer.Open(path));
    }

    /// <summary>
    /// The magic number alone is not an archive, and a file that has only that must
    /// fail as a damaged RAR rather than crash out of the dependency.
    /// </summary>
    [Fact]
    public void A_damaged_rar_is_refused_as_a_format_error()
    {
        using var temp = new TempDir();
        string path = temp.File("damaged.cbr");

        File.WriteAllBytes(path, [.. Encoding.ASCII.GetBytes("Rar!\x1a\x07\x00"), .. new byte[64]]);

        // Claimed, because a damaged file is still its own format's file, and then
        // refused on the way in rather than reported as something unsupported.
        Assert.Equal(FormatId.Cbr, BookFormats.Identify(path).Format);
        Assert.Throws<BookFormatException>(() => Book.Load(path));
    }
}
