using System.Text;
using EBookMeta.Containers;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Tests for the ZIP container, concentrating on the invariants whose violation
/// corrupts a user's library: entry order, per-entry compression method, and
/// byte-identical rebuild.
/// </summary>
public sealed class ContainerTests
{
    [Fact]
    public void Entries_are_in_archive_order_with_mimetype_first()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));

        using ZipContainer container = ZipContainer.Open(path);

        Assert.Equal("mimetype", container.Entries[0].Name);
        Assert.Equal("META-INF/container.xml", container.Entries[1].Name);
        Assert.Equal("OEBPS/content.opf", container.Entries[2].Name);
    }

    /// <summary>
    /// The reason <c>ZipCentralDirectory</c> exists. <c>ZipArchiveEntry</c> does not
    /// expose the compression method, and an EPUB whose <c>mimetype</c> gets
    /// deflated on save is rejected by readers — so the method has to be read from
    /// the archive structure, not guessed from sizes.
    /// </summary>
    [Fact]
    public void Compression_method_is_read_per_entry_not_inferred()
    {
        using var temp = new TempDir();

        // Random-ish bytes: deflate cannot compress them, so compressed and
        // uncompressed lengths land close together and a
        // CompressedLength == Length heuristic would misread both entries.
        byte[] incompressible = new byte[512];
        for (int i = 0; i < incompressible.Length; i++)
        {
            incompressible[i] = (byte)(i * 7 % 251);
        }

        string path = new EpubBuilder()
            .WithEntry("OEBPS/stored.bin", incompressible, stored: true)
            .WithEntry("OEBPS/deflated.bin", incompressible)
            .WriteTo(temp.File("mixed.epub"));

        using ZipContainer container = ZipContainer.Open(path);

        Assert.Equal(ZipCompressionMethods.Stored, container.Entries[0].CompressionMethod);
        Assert.Equal(ZipCompressionMethods.Deflate, container.Entries[2].CompressionMethod);
        Assert.True(container.AllEntriesUseReproducibleCompression);

        Assert.Equal(
            ZipCompressionMethods.Stored,
            container.Entries.Single(e => e.Name == "OEBPS/stored.bin").CompressionMethod);
        Assert.Equal(
            ZipCompressionMethods.Deflate,
            container.Entries.Single(e => e.Name == "OEBPS/deflated.bin").CompressionMethod);
    }

    /// <summary>Invariant 6, at the container level.</summary>
    [Fact]
    public void Rebuild_without_edits_is_byte_identical()
    {
        using var temp = new TempDir();
        string source = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));
        string target = temp.File("rebuilt.epub");

        byte[] before = File.ReadAllBytes(source);

        using (ZipContainer container = ZipContainer.Open(source))
        {
            container.Rebuild(
                container.Entries.Select(e => PendingEntry.CopyOf(container, e)).ToList(),
                target);
        }

        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void Rebuild_preserves_order_and_method_for_a_large_archive()
    {
        using var temp = new TempDir();

        var builder = new EpubBuilder();
        for (int i = 0; i < 500; i++)
        {
            builder.WithEntry($"OEBPS/text/page{i:D3}.xhtml", $"<html><body>{i}</body></html>");
        }

        string source = builder.WriteTo(temp.File("large.epub"));
        string target = temp.File("large-rebuilt.epub");

        string[] namesBefore;
        ushort[] methodsBefore;

        using (ZipContainer container = ZipContainer.Open(source))
        {
            namesBefore = container.Entries.Select(e => e.Name).ToArray();
            methodsBefore = container.Entries.Select(e => e.CompressionMethod).ToArray();
            container.Rebuild(
                container.Entries.Select(e => PendingEntry.CopyOf(container, e)).ToList(),
                target);
        }

        using ZipContainer rebuilt = ZipContainer.Open(target);

        Assert.Equal(namesBefore, rebuilt.Entries.Select(e => e.Name).ToArray());
        Assert.Equal(methodsBefore, rebuilt.Entries.Select(e => e.CompressionMethod).ToArray());
    }

    [Fact]
    public void Entry_content_round_trips_exactly()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid-epub3.epub"));

        using ZipContainer container = ZipContainer.Open(path);
        using Stream stream = container.OpenRead(container.Entries[0]);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        // No trailing newline, no BOM — readers reject an EPUB that gets this
        // wrong, and it is easy to get wrong by writing the file with an editor.
        Assert.Equal("application/epub+zip", Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>
    /// GEN-F001: refuse rather than guess. Rebuilding a file we cannot parse
    /// structurally would risk writing back something worse than we found.
    /// </summary>
    [Fact]
    public void An_archive_that_cannot_be_parsed_is_refused()
    {
        using var temp = new TempDir();

        byte[] full = new EpubBuilder().Build();
        string truncated = temp.File("truncated.epub");
        File.WriteAllBytes(truncated, full.Take(full.Length / 2).ToArray());

        Assert.Throws<BookFormatException>(() => ZipContainer.Open(truncated));

        string nonsense = temp.File("nonsense.epub");
        File.WriteAllBytes(nonsense, Encoding.UTF8.GetBytes(new string('x', 400)));

        Assert.Throws<BookFormatException>(() => ZipContainer.Open(nonsense));
    }
}
