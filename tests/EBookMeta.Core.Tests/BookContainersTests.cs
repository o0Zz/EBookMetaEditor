using System.Text;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// The container registry: what this build recognises by its leading bytes, and
/// what it can open once it has.
/// </summary>
public sealed class BookContainersTests
{
    /// <summary>Every magic number the sniff answers to, and where it sits.</summary>
    public static TheoryData<int, byte[], ContainerKind> Signatures =>
        new()
        {
            { 0, [0x50, 0x4B, 0x03, 0x04], ContainerKind.Zip },
            { 0, [0x50, 0x4B, 0x05, 0x06], ContainerKind.Zip },
            { 0, [0x50, 0x4B, 0x07, 0x08], ContainerKind.Zip },
            { 0, [.. Encoding.ASCII.GetBytes("Rar!"), 0x1A, 0x07, 0x00], ContainerKind.Rar },
            { 0, [.. Encoding.ASCII.GetBytes("Rar!"), 0x1A, 0x07, 0x01, 0x00], ContainerKind.Rar },
            { 0, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], ContainerKind.SevenZip },
            { 257, Encoding.ASCII.GetBytes("ustar"), ContainerKind.Tar },
            { 60, Encoding.ASCII.GetBytes("BOOKMOBI"), ContainerKind.PalmDb },
            { 60, Encoding.ASCII.GetBytes("TEXtREAd"), ContainerKind.PalmDb },
        };

    private static byte[] Head(int offset, byte[] magic)
    {
        byte[] head = new byte[Math.Max(offset + magic.Length, 512)];
        Array.Copy(magic, 0, head, offset, magic.Length);
        return head;
    }

    [Theory]
    [MemberData(nameof(Signatures))]
    public void A_magic_number_names_its_container(int offset, byte[] magic, ContainerKind expected)
    {
        (ContainerKind kind, _) = BookContainers.Sniff(Head(offset, magic));

        Assert.Equal(expected, kind);
    }

    /// <summary>A file with no marker is a raw one, which is what a bare FB2 is.</summary>
    [Fact]
    public void A_file_with_no_marker_is_raw()
    {
        byte[] xml = Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><FictionBook>");

        Assert.Equal(ContainerKind.Raw, BookContainers.Sniff(xml).Kind);
        Assert.Equal(ContainerKind.Raw, BookContainers.Sniff([]).Kind);
    }

    /// <summary>
    /// A signature deeper in the file must not be read out of a head that stops
    /// short of it.
    /// </summary>
    [Fact]
    public void A_short_head_matches_nothing_it_cannot_reach()
    {
        Assert.Equal(ContainerKind.Raw, BookContainers.Sniff(new byte[64]).Kind);
    }

    /// <summary>
    /// Everything the sniff can answer has something to open it with, so no path
    /// through detection can end in "recognised but unopenable".
    /// </summary>
    [Fact]
    public void Every_registered_container_can_be_opened_by_kind()
    {
        Assert.NotEmpty(BookContainers.All);

        foreach (ContainerFormat container in BookContainers.All)
        {
            Assert.Equal(container.Kind, BookContainers.For(container.Kind)?.Kind);
        }
    }

    [Fact]
    public void An_unregistered_kind_is_refused_rather_than_guessed_at()
    {
        Assert.Null(BookContainers.For(ContainerKind.Unknown));
        Assert.Throws<NotSupportedException>(
            () => BookContainers.Open("nothing.bin", ContainerKind.Unknown));
    }
}
