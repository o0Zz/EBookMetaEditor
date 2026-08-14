using System.Text;
using EBookMeta.Xml;
using EBookMeta.Formats;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Covers EPUB-W070: a namespace prefix used but never declared, and the repair
/// that supplies the missing declaration.
/// </summary>
public sealed class RepairNamespaceTests
{
    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string Text(byte[] bytes) => new UTF8Encoding(false).GetString(bytes);

    private static NamespaceRepairResult Repair(string opf) =>
        Assert.IsType<NamespaceRepairResult>(EpubFormat.RepairNamespaces(Utf8(opf)));

    // --- detection -------------------------------------------------------

    [Fact]
    public void Strict_parse_rejects_an_undeclared_prefix()
    {
        // The premise of the whole feature: this document cannot be opened at all,
        // so the diagnosis cannot come from the strict parser.
        BookFormatException ex = Assert.Throws<BookFormatException>(
            () => OpfDocument.Parse(Utf8(EpubBuilder.Epub2OpfUndeclaredOpfPrefix)));

        Assert.Contains("not well-formed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repair_reports_where_the_prefix_was_first_used()
    {
        NamespaceRepairResult result = Repair(EpubBuilder.Epub2OpfUndeclaredOpfPrefix);

        Assert.Equal(4, result.Line);
        Assert.Equal(15, result.Column);
    }

    [Fact]
    public void A_document_that_needs_no_repair_is_left_alone()
    {
        Assert.Null(EpubFormat.RepairNamespaces(Utf8(EpubBuilder.Epub2Opf)));
        Assert.Null(EpubFormat.RepairNamespaces(Utf8(EpubBuilder.Epub3Opf)));

        // xml: and xmlns: are bound by the XML specification. Reporting them would
        // be a false positive on a perfectly correct document.
        const string specBound = """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title xml:lang="en">Neverwhere</dc:title>
              </metadata>
            </package>
            """;

        Assert.Null(EpubFormat.RepairNamespaces(Utf8(specBound)));
    }

    [Fact]
    public void Only_specification_backed_prefixes_are_known()
    {
        Assert.True(EpubFormat.IsKnownNamespacePrefix("opf"));
        Assert.True(EpubFormat.IsKnownNamespacePrefix("dc"));
        Assert.False(EpubFormat.IsKnownNamespacePrefix("acme"));
        Assert.False(EpubFormat.IsKnownNamespacePrefix("calibre"));
    }

    // --- repair ----------------------------------------------------------

    [Fact]
    public void Repair_makes_the_document_parseable()
    {
        NamespaceRepairResult result = Repair(EpubBuilder.Epub2OpfUndeclaredOpfPrefix);

        Assert.True(result.IsComplete);
        Assert.Null(result.RemainingError);
        Assert.Equal(["opf"], result.Added);
        Assert.Empty(result.Skipped);

        OpfDocument repaired = OpfDocument.Parse(result.RepairedBytes);

        // And the metadata the prefix was carrying survived intact.
        Model.BookMetadata metadata = repaired.ReadMetadata();
        Assert.Equal("Neverwhere", metadata.Title);
        Model.Creator creator = Assert.Single(metadata.Creators);
        Assert.Equal("Neil Gaiman", creator.Name);
        Assert.Equal("Gaiman, Neil", creator.SortName);
    }

    [Fact]
    public void Repair_changes_exactly_one_line()
    {
        NamespaceRepairResult result = Repair(EpubBuilder.Epub2OpfUndeclaredOpfPrefix);

        string[] before = EpubBuilder.Epub2OpfUndeclaredOpfPrefix.Split('\n');
        string[] after = Text(result.RepairedBytes).Split('\n');

        Assert.Equal(before.Length, after.Length);

        int[] differing = Enumerable.Range(0, before.Length)
            .Where(i => before[i] != after[i])
            .ToArray();

        // The reason the repair is an insertion rather than a reserialisation:
        // saving must not move every line of the user's file.
        Assert.Single(differing);
        Assert.Contains("<package", after[differing[0]], StringComparison.Ordinal);
        Assert.Contains(@"xmlns:opf=""http://www.idpf.org/2007/opf""", after[differing[0]], StringComparison.Ordinal);
    }

    // --- the "report, never guess" boundary ------------------------------

    [Fact]
    public void An_unknown_prefix_is_reported_but_not_repaired()
    {
        NamespaceRepairResult result = Repair(EpubBuilder.OpfUnknownPrefix);

        Assert.False(result.IsComplete);
        Assert.False(result.HasChanges);
        Assert.Equal(["acme"], result.Skipped);

        // Nothing was invented, so the bytes come back as they went in.
        Assert.Equal(EpubBuilder.OpfUnknownPrefix, Text(result.RepairedBytes));
    }

    [Fact]
    public void Known_prefixes_are_fixed_while_unknown_ones_are_reported()
    {
        const string opf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata>
                <dc:title opf:file-as="N" acme:sort="N">Neverwhere</dc:title>
              </metadata>
            </package>
            """;

        NamespaceRepairResult result = Repair(opf);

        // The result is honest about being partial rather than reporting success.
        Assert.True(result.HasChanges);
        Assert.False(result.IsComplete);
        Assert.Equal(["dc", "opf"], result.Added);
        Assert.Equal(["acme"], result.Skipped);

        string repaired = Text(result.RepairedBytes);
        Assert.Contains(@"xmlns:dc=""http://purl.org/dc/elements/1.1/""", repaired, StringComparison.Ordinal);
        Assert.Contains(@"xmlns:opf=""http://www.idpf.org/2007/opf""", repaired, StringComparison.Ordinal);
        Assert.DoesNotContain("xmlns:acme", repaired, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_with_other_damage_is_not_claimed_as_repaired()
    {
        // An unclosed <metadata> as well as the undeclared prefix. A document that
        // still fails after recovery is reported, not guessed at further.
        const string opf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata>
                <dc:title opf:file-as="N">Neverwhere</dc:title>
            </package>
            """;

        NamespaceRepairResult result = Repair(opf);

        Assert.False(result.IsComplete);
        Assert.NotNull(result.RemainingError);
    }

    // --- byte fidelity ---------------------------------------------------

    [Fact]
    public void Line_endings_and_a_byte_order_mark_survive()
    {
        string crlf = EpubBuilder.Epub2OpfUndeclaredOpfPrefix.Replace("\n", "\r\n");
        NamespaceRepairResult result = Repair(crlf);
        string repaired = Text(result.RepairedBytes);

        Assert.True(result.IsComplete);
        Assert.Equal(
            crlf.Split(["\r\n"], StringSplitOptions.None).Length,
            repaired.Split(["\r\n"], StringSplitOptions.None).Length);
        Assert.DoesNotContain(repaired.Replace("\r\n", string.Empty), "\n", StringComparison.Ordinal);

        byte[] withBom = new UTF8Encoding(true).GetPreamble()
            .Concat(Utf8(EpubBuilder.Epub2OpfUndeclaredOpfPrefix))
            .ToArray();

        NamespaceRepairResult bom = Assert.IsType<NamespaceRepairResult>(EpubFormat.RepairNamespaces(withBom));

        Assert.True(bom.IsComplete);
        Assert.Equal(new UTF8Encoding(true).GetPreamble(), bom.RepairedBytes.Take(3));
    }

    [Fact]
    public void A_non_utf8_encoding_survives()
    {
        // A declared windows-1252 document must come back as windows-1252 bytes,
        // not silently promoted to UTF-8 by the repair.
        string opf = EpubBuilder.Epub2OpfUndeclaredOpfPrefix
            .Replace("encoding=\"UTF-8\"", "encoding=\"windows-1252\"")
            .Replace("Neil Gaiman", "Neil Gaimån");

        Encoding cp1252 = Encoding.GetEncoding(1252);
        NamespaceRepairResult result =
            Assert.IsType<NamespaceRepairResult>(EpubFormat.RepairNamespaces(cp1252.GetBytes(opf)));

        Assert.True(result.IsComplete);
        Assert.Contains("Gaimån", cp1252.GetString(result.RepairedBytes), StringComparison.Ordinal);

        // 0xE5 is 'å' in windows-1252; in UTF-8 it would have become two bytes.
        Assert.Contains((byte)0xE5, result.RepairedBytes);
    }

    // --- start-tag scanning edge cases -----------------------------------

    /// <summary>
    /// Finding the root element means scanning past a '&gt;' that does not end a
    /// tag, in an attribute value, a doctype internal subset or a comment.
    /// </summary>
    [Theory]
    [InlineData("""
        <?xml version="1.0"?>
        <package xmlns="http://www.idpf.org/2007/opf" note="a &gt; b" version="2.0">
          <metadata><dc:title>X</dc:title></metadata>
        </package>
        """, "note=\"a &gt; b\"")]
    [InlineData("""
        <?xml version="1.0"?>
        <!DOCTYPE package [<!ENTITY nb "&#160;">]>
        <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
          <metadata><dc:title>X</dc:title></metadata>
        </package>
        """, """<!ENTITY nb "&#160;">""")]
    [InlineData("""
        <?xml version="1.0"?>
        <!-- generated by something <package> -->
        <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
          <metadata><dc:title>X</dc:title></metadata>
        </package>
        """, "<!-- generated by something <package> -->")]
    public void The_scan_finds_the_root_past_a_misleading_angle_bracket(string opf, string preserved)
    {
        NamespaceRepairResult result = Repair(opf);
        string repaired = Text(result.RepairedBytes);

        Assert.True(result.IsComplete);
        Assert.Contains(preserved, repaired, StringComparison.Ordinal);
        Assert.Contains(@"xmlns:dc=""http://purl.org/dc/elements/1.1/""", repaired, StringComparison.Ordinal);
    }

    [Fact]
    public void A_self_closing_root_gets_the_declaration_before_the_slash()
    {
        NamespaceRepairResult result = Repair("""<root dc:x="1"/>""");
        string repaired = Text(result.RepairedBytes);

        Assert.True(result.IsComplete);
        Assert.EndsWith("/>", repaired, StringComparison.Ordinal);
        Assert.Contains(@"xmlns:dc=""http://purl.org/dc/elements/1.1/""/>", repaired, StringComparison.Ordinal);
    }
}
