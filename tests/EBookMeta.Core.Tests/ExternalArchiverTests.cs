using System.Text;
using EBookMeta.Containers;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// The machinery the two containers that cannot compress themselves share: finding
/// somebody else's archiver, and putting entries on disk for it. Written once here
/// rather than once per container.
/// </summary>
public sealed class ExternalArchiverTests
{
    /// <summary>Every archiver this build knows how to run.</summary>
    public static TheoryData<string> Archivers => ["rar", "7z"];

    private static ExternalArchiver Named(string name) =>
        name == "rar" ? RarContainer.Archiver : SevenZipContainer.Archiver;

    // ---- finding an archiver ------------------------------------------------

    /// <summary>
    /// The per-directory probe both halves of the search are built on. The registry and
    /// <c>PATH</c> themselves are not testable — they answer from the machine.
    /// </summary>
    [Theory]
    [MemberData(nameof(Archivers))]
    public void A_directory_holding_the_archiver_is_the_answer(string name)
    {
        using var temp = new TempDir();
        ExternalArchiver archiver = Named(name);

        string holding = Directory.CreateDirectory(Path.Combine(temp.Path, "with space")).FullName;
        string empty = Directory.CreateDirectory(Path.Combine(temp.Path, "elsewhere")).FullName;
        string tool = Path.Combine(holding, archiver.ExecutableName);
        File.WriteAllBytes(tool, [0x4D, 0x5A]);

        Assert.Equal(tool, archiver.In(holding));
        Assert.Null(archiver.In(empty));

        // PATH and the registry both hand out quoted entries.
        Assert.Equal(tool, archiver.In($"\"{holding}\""));
    }

    /// <summary>Walking a junk-filled search path must not be the thing that throws.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\does\not\exist")]
    [InlineData("this|is<not>a*path")]
    public void A_junk_directory_finds_nothing_and_does_not_throw(string? directory)
    {
        Assert.Null(RarContainer.Archiver.In(directory));
        Assert.Null(SevenZipContainer.Archiver.In(directory));
    }

    /// <summary>
    /// The real search does not throw, whatever this machine has. Asserts nothing about
    /// the answer: a locked-down registry must yield null, not an exception.
    /// </summary>
    [Theory]
    [MemberData(nameof(Archivers))]
    public void The_real_search_does_not_throw(string name)
    {
        string? found = Named(name).Search();

        Assert.True(found is null || File.Exists(found));
    }

    /// <summary>
    /// What both command lines have in common: the add verb, a quoted target, and the
    /// entry names in a list file rather than on a command line that three hundred
    /// pages would overflow. Where they differ — WinRAR's <c>--</c> separator, which
    /// 7-Zip does not take — is asserted per format.
    /// </summary>
    [Theory]
    [MemberData(nameof(Archivers))]
    public void The_command_line_adds_a_quoted_target_from_a_list_file(string name)
    {
        string arguments = Named(name).Arguments(@"C:\books\comic.tmp", stored: false);

        Assert.StartsWith("a ", arguments, StringComparison.Ordinal);
        Assert.Contains(@"""C:\books\comic.tmp""", arguments, StringComparison.Ordinal);
        Assert.EndsWith($"@\"{ExternalArchiver.ListFileName}\"", arguments, StringComparison.Ordinal);
    }

    // ---- staging ------------------------------------------------------------

    /// <summary>
    /// This is the only place in Core that puts archive entries on disk, so the layout
    /// it produces is worth asserting directly.
    /// </summary>
    [Fact]
    public void Staging_writes_every_entry_under_its_own_relative_name()
    {
        using var temp = new TempDir();
        string staging = Path.Combine(temp.Path, "stage");

        List<string> names = ExternalArchiver.Stage(
            [
                PendingEntry.FromBytes("01.png", PngBuilder.OnePixel),
                PendingEntry.FromBytes("sub/02.png", PngBuilder.OnePixel),
                PendingEntry.FromBytes("ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo />")),
            ],
            staging);

        // Order is the archive's reading order and survives staging.
        Assert.Equal([@"01.png", @"sub\02.png", @"ComicInfo.xml"], names);

        Assert.Equal(PngBuilder.OnePixel, File.ReadAllBytes(Path.Combine(staging, "sub", "02.png")));
        Assert.Equal("<ComicInfo />", File.ReadAllText(Path.Combine(staging, "ComicInfo.xml")));
    }

    /// <summary>Hard invariant 4, at the one point where it stops being advisory.</summary>
    [Theory]
    [InlineData("../escaped.png")]
    [InlineData(@"..\escaped.png")]
    [InlineData("/rooted.png")]
    [InlineData(@"C:\rooted.png")]
    [InlineData("sub/../../escaped.png")]
    public void Staging_refuses_a_name_that_leaves_the_archive(string name)
    {
        using var temp = new TempDir();
        string staging = Path.Combine(temp.Path, "stage");

        Assert.Throws<BookFormatException>(() => ExternalArchiver.Stage(
            [
                PendingEntry.FromBytes(name, [1, 2, 3]),
                PendingEntry.FromBytes("01.png", PngBuilder.OnePixel),
            ],
            staging));

        Assert.Empty(Directory.Exists(staging)
            ? Directory.GetFiles(staging, "*", SearchOption.AllDirectories)
            : []);
    }

    /// <summary>
    /// A directory entry is staged as a directory and never handed to the archiver.
    /// The shape that broke a real save: pages nested under a marker for their folder.
    /// </summary>
    [Fact]
    public void Staging_makes_a_directory_of_a_directory_entry_and_does_not_list_it()
    {
        using var temp = new TempDir();
        string staging = Path.Combine(temp.Path, "stage");

        var folder = new ContainerEntry
        {
            Name = "comic",
            Index = 0,
            Length = 0,
            IsDirectory = true,
        };

        List<string> names = ExternalArchiver.Stage(
            [
                PendingEntry.FromBytes("comic/01.png", PngBuilder.OnePixel),

                // After its own pages, which is where RAR puts it.
                PendingEntry.Replacing(folder, []),

                // The ZIP spelling: a trailing separator and no source entry to ask.
                PendingEntry.FromBytes("extra/", []),
            ],
            staging);

        Assert.Equal([@"comic\01.png"], names);
        Assert.True(Directory.Exists(Path.Combine(staging, "comic")));
        Assert.True(Directory.Exists(Path.Combine(staging, "extra")));
        Assert.True(File.Exists(Path.Combine(staging, "comic", "01.png")));
    }

    /// <summary>An archive may legally repeat a name; a directory may not.</summary>
    [Fact]
    public void Staging_refuses_a_name_that_appears_twice()
    {
        using var temp = new TempDir();
        string staging = Path.Combine(temp.Path, "stage");

        Assert.Throws<BookFormatException>(() => ExternalArchiver.Stage(
            [
                PendingEntry.FromBytes("01.png", PngBuilder.OnePixel),
                PendingEntry.FromBytes("01.png", PngBuilder.OnePixel),
            ],
            staging));
    }
}
