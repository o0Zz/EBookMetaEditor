using System.Text;
using System.Xml.Linq;
using EBookMeta.Documents;
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
        Assert.IsType<NamespaceRepairResult>(NamespaceRepair.Repair(Utf8(opf)));

    // --- detection -------------------------------------------------------

    [Fact]
    public void StrictParseRejectsAnUndeclaredPrefix()
    {
        // The premise of the whole feature: this document cannot be opened at
        // all, so the diagnosis cannot come from the strict parser.
        BookFormatException ex = Assert.Throws<BookFormatException>(
            () => OpfDocument.Parse(Utf8(EpubBuilder.Epub2OpfUndeclaredOpfPrefix)));

        Assert.Contains("not well-formed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateReportsThePrefixWithItsPosition()
    {
        Finding finding = Assert.Single(
            NamespaceRepair.Validate(Utf8(EpubBuilder.Epub2OpfUndeclaredOpfPrefix), "OEBPS/content.opf"));

        Assert.Equal("EPUB-W070", finding.RuleId);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.True(finding.HasAutofix);
        Assert.Equal("OEBPS/content.opf", finding.Location);
        Assert.Equal(4, finding.Line);
        Assert.Equal(15, finding.Column);
        Assert.Contains("opf", finding.Message, StringComparison.Ordinal);
        Assert.Contains("opf:file-as", finding.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredPrefixesAreNotReported()
    {
        Assert.Empty(NamespaceRepair.Validate(Utf8(EpubBuilder.Epub2Opf)));
        Assert.Null(NamespaceRepair.Repair(Utf8(EpubBuilder.Epub2Opf)));
        Assert.Null(NamespaceRepair.Repair(Utf8(EpubBuilder.Epub3Opf)));
    }

    [Fact]
    public void SpecBoundPrefixesAreNotReported()
    {
        // xml: and xmlns: are bound by the XML specification. Reporting them
        // would be a false positive on a perfectly correct document.
        const string opf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title xml:lang="en">Neverwhere</dc:title>
              </metadata>
            </package>
            """;

        Assert.Empty(NamespaceRepair.Validate(Utf8(opf)));
        Assert.Null(NamespaceRepair.Repair(Utf8(opf)));
    }

    [Fact]
    public void OnlySpecificationBackedPrefixesAreKnown()
    {
        Assert.True(NamespaceRepair.IsKnownPrefix("opf"));
        Assert.True(NamespaceRepair.IsKnownPrefix("dc"));
        Assert.False(NamespaceRepair.IsKnownPrefix("acme"));
        Assert.False(NamespaceRepair.IsKnownPrefix("calibre"));
    }

    // --- repair ----------------------------------------------------------

    [Fact]
    public void RepairMakesTheDocumentParseable()
    {
        NamespaceRepairResult result = Repair(EpubBuilder.Epub2OpfUndeclaredOpfPrefix);

        Assert.True(result.IsComplete);
        Assert.Null(result.RemainingError);
        Assert.Equal(["opf"], result.Added);
        Assert.Empty(result.Skipped);

        // The repaired bytes are a document the strict parser accepts...
        OpfDocument repaired = OpfDocument.Parse(result.RepairedBytes);

        // ...and the metadata the prefix was carrying survived intact.
        Model.BookMetadata metadata = repaired.ReadMetadata();
        Assert.Equal("Neverwhere", metadata.Title);
        Model.Creator creator = Assert.Single(metadata.Creators);
        Assert.Equal("Neil Gaiman", creator.Name);
        Assert.Equal("Gaiman, Neil", creator.SortName);
    }

    [Fact]
    public void RepairChangesExactlyOneLine()
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

    [Fact]
    public void TheDeclarationLandsOnTheRootElement()
    {
        NamespaceRepairResult result = Repair(EpubBuilder.Epub2OpfUndeclaredOpfPrefix);

        XDocument document = XDocument.Parse(Text(result.RepairedBytes));
        XElement root = Assert.IsType<XElement>(document.Root);

        Assert.Equal("package", root.Name.LocalName);
        Assert.Equal("http://www.idpf.org/2007/opf", root.GetNamespaceOfPrefix("opf")?.NamespaceName);
    }

    [Fact]
    public void RepairDoesNotMutateTheInputBytes()
    {
        byte[] source = Utf8(EpubBuilder.Epub2OpfUndeclaredOpfPrefix);
        byte[] copy = source.ToArray();

        NamespaceRepairResult result = Assert.IsType<NamespaceRepairResult>(NamespaceRepair.Repair(source));

        Assert.Equal(copy, source);
        Assert.NotEqual(copy, result.RepairedBytes);
    }

    // --- the "report, never guess" boundary ------------------------------

    [Fact]
    public void AnUnknownPrefixIsReportedButNotRepaired()
    {
        NamespaceRepairResult result = Repair(EpubBuilder.OpfUnknownPrefix);

        Assert.False(result.IsComplete);
        Assert.False(result.HasChanges);
        Assert.Equal(["acme"], result.Skipped);

        // Nothing was invented, so the bytes come back as they went in.
        Assert.Equal(EpubBuilder.OpfUnknownPrefix, Text(result.RepairedBytes));
    }

    [Fact]
    public void AnUnknownPrefixIsNotAdvertisedAsAutofixable()
    {
        Finding finding = Assert.Single(NamespaceRepair.Validate(Utf8(EpubBuilder.OpfUnknownPrefix)));

        Assert.Equal("EPUB-W070", finding.RuleId);
        Assert.False(finding.HasAutofix);
        Assert.Contains("cannot be repaired automatically", finding.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownPrefixesAreFixedWhileUnknownOnesAreReported()
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

        // dc and opf are recoverable; acme is not, so the result is honest about
        // being partial rather than reporting success.
        Assert.True(result.HasChanges);
        Assert.False(result.IsComplete);
        Assert.Equal(["dc", "opf"], result.Added);
        Assert.Equal(["acme"], result.Skipped);

        string repaired = Text(result.RepairedBytes);
        Assert.Contains(@"xmlns:dc=""http://purl.org/dc/elements/1.1/""", repaired, StringComparison.Ordinal);
        Assert.Contains(@"xmlns:opf=""http://www.idpf.org/2007/opf""", repaired, StringComparison.Ordinal);
        Assert.DoesNotContain("xmlns:acme", repaired, StringComparison.Ordinal);
    }

    // --- byte fidelity ---------------------------------------------------

    [Fact]
    public void CrLfLineEndingsSurvive()
    {
        string opf = EpubBuilder.Epub2OpfUndeclaredOpfPrefix.Replace("\n", "\r\n");

        NamespaceRepairResult result = Repair(opf);
        string repaired = Text(result.RepairedBytes);

        Assert.True(result.IsComplete);
        Assert.Equal(
            opf.Split(new[] { "\r\n" }, StringSplitOptions.None).Length,
            repaired.Split(new[] { "\r\n" }, StringSplitOptions.None).Length);
        Assert.DoesNotContain(repaired.Replace("\r\n", string.Empty), "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void AByteOrderMarkSurvives()
    {
        byte[] withBom = new UTF8Encoding(true).GetPreamble()
            .Concat(Utf8(EpubBuilder.Epub2OpfUndeclaredOpfPrefix))
            .ToArray();

        NamespaceRepairResult result = Assert.IsType<NamespaceRepairResult>(NamespaceRepair.Repair(withBom));

        Assert.True(result.IsComplete);
        Assert.Equal(new UTF8Encoding(true).GetPreamble(), result.RepairedBytes.Take(3));
    }

    [Fact]
    public void ANonUtf8EncodingSurvives()
    {
        // A declared windows-1252 document must come back as windows-1252 bytes,
        // not silently promoted to UTF-8 by the repair.
        string opf = EpubBuilder.Epub2OpfUndeclaredOpfPrefix
            .Replace("encoding=\"UTF-8\"", "encoding=\"windows-1252\"")
            .Replace("Neil Gaiman", "Neil Gaimån");

        Encoding cp1252 = Encoding.GetEncoding(1252);
        NamespaceRepairResult result =
            Assert.IsType<NamespaceRepairResult>(NamespaceRepair.Repair(cp1252.GetBytes(opf)));

        Assert.True(result.IsComplete);
        Assert.Contains("Gaimån", cp1252.GetString(result.RepairedBytes), StringComparison.Ordinal);

        // 0xE5 is 'å' in windows-1252; in UTF-8 it would have become two bytes.
        Assert.Contains((byte)0xE5, result.RepairedBytes);
    }

    // --- start-tag scanning edge cases -----------------------------------

    [Fact]
    public void AGreaterThanInsideAnAttributeValueDoesNotConfuseTheScan()
    {
        const string opf = """
            <?xml version="1.0"?>
            <package xmlns="http://www.idpf.org/2007/opf" note="a &gt; b" version="2.0">
              <metadata><dc:title>X</dc:title></metadata>
            </package>
            """;

        NamespaceRepairResult result = Repair(opf);

        Assert.True(result.IsComplete);
        Assert.Contains(@"note=""a &gt; b""", Text(result.RepairedBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void ADoctypeInternalSubsetDoesNotConfuseTheScan()
    {
        // The '>' inside the ENTITY declaration must not be mistaken for the end
        // of the doctype, or the root element is never found.
        const string opf = """
            <?xml version="1.0"?>
            <!DOCTYPE package [<!ENTITY nb "&#160;">]>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata><dc:title>X</dc:title></metadata>
            </package>
            """;

        NamespaceRepairResult result = Repair(opf);

        Assert.True(result.IsComplete);
        Assert.Contains(@"<package xmlns=""http://www.idpf.org/2007/opf"" version=""2.0"" xmlns:dc=",
            Text(result.RepairedBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void CommentsBeforeTheRootAreSkipped()
    {
        const string opf = """
            <?xml version="1.0"?>
            <!-- generated by something <package> -->
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata><dc:title>X</dc:title></metadata>
            </package>
            """;

        NamespaceRepairResult result = Repair(opf);

        Assert.True(result.IsComplete);
        Assert.Contains("<!-- generated by something <package> -->", Text(result.RepairedBytes), StringComparison.Ordinal);
        Assert.Contains(@"<package xmlns=""http://www.idpf.org/2007/opf"" version=""2.0"" xmlns:dc=",
            Text(result.RepairedBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void ASelfClosingRootGetsTheDeclarationBeforeTheSlash()
    {
        NamespaceRepairResult result = Repair("""<root dc:x="1"/>""");
        string repaired = Text(result.RepairedBytes);

        Assert.True(result.IsComplete);
        Assert.EndsWith("/>", repaired, StringComparison.Ordinal);
        Assert.Contains(@"xmlns:dc=""http://purl.org/dc/elements/1.1/""/>", repaired, StringComparison.Ordinal);
    }

    // --- a document broken beyond namespaces ------------------------------

    [Fact]
    public void ADocumentWithOtherDamageIsNotClaimedAsRepaired()
    {
        // An unclosed <metadata> as well as the undeclared prefix. A document
        // that still fails after recovery is reported, not guessed at further.
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
}
