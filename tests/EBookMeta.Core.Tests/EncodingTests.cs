using System.Text;
using EBookMeta.Documents;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Tests that a document's real encoding is determined from its bytes.
/// </summary>
/// <remarks>
/// Loading through <c>XDocument</c> would hide the difference between "declared
/// UTF-8 and is UTF-8" and "declared UTF-8, is really Windows-1252, and the
/// parser substituted replacement characters". Telling the user which one they
/// have is the point of rule EPUB-E050.
/// </remarks>
public sealed class EncodingTests
{
    [Fact]
    public void Utf8_bom_is_detected_and_stripped_on_decode()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><a/>")];

        XmlEncodingInfo info = XmlEncodingDetector.Detect(bytes);

        Assert.True(info.HasByteOrderMark);
        Assert.Equal(3, info.ByteOrderMarkLength);
        Assert.True(info.DeclarationMatchesBytes);
        Assert.StartsWith("<?xml", XmlEncodingDetector.Decode(bytes, info));
    }

    [Fact]
    public void Declared_utf8_that_really_is_utf8_is_accepted()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><a>café</a>");

        XmlEncodingInfo info = XmlEncodingDetector.Detect(bytes);

        Assert.True(info.DeclarationMatchesBytes);
        Assert.Equal("UTF-8", info.DeclaredName);
    }

    /// <summary>
    /// The <c>latin1-declared-utf8</c> fixture case. 0xE9 is 'é' in Latin-1 and
    /// an invalid lone lead byte in UTF-8.
    /// </summary>
    [Fact]
    public void Latin1_bytes_declared_as_utf8_are_caught()
    {
        byte[] bytes =
        [
            .. Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><a>caf"),
            0xE9,
            .. Encoding.ASCII.GetBytes("</a>"),
        ];

        XmlEncodingInfo info = XmlEncodingDetector.Detect(bytes);

        Assert.False(info.DeclarationMatchesBytes);
        Assert.NotNull(info.Mismatch);
    }

    /// <summary>
    /// .NET Framework has every legacy code page in the box, unlike .NET Core
    /// where this needs an extra encoding provider. So a Windows-1252 EPUB is
    /// decoded correctly rather than merely diagnosed — which is one of the
    /// reasons this project targets net48.
    /// </summary>
    [Fact]
    public void Windows1252_is_available_and_decodes_correctly()
    {
        byte[] bytes =
        [
            .. Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"windows-1252\"?><a>"),
            0x93, // left double quotation mark in cp1252; undefined in Latin-1
            .. Encoding.ASCII.GetBytes("</a>"),
        ];

        XmlEncodingInfo info = XmlEncodingDetector.Detect(bytes);

        Assert.Equal(1252, info.Encoding.CodePage);
        Assert.True(info.DeclarationMatchesBytes);
        Assert.Contains('“', XmlEncodingDetector.Decode(bytes, info));
    }

    [Fact]
    public void No_declaration_defaults_to_utf8()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("<a>plain</a>");

        XmlEncodingInfo info = XmlEncodingDetector.Detect(bytes);

        Assert.Equal(65001, info.Encoding.CodePage);
        Assert.True(info.DeclarationMatchesBytes);
    }

    [Fact]
    public void No_declaration_and_invalid_utf8_is_reported()
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes("<a>caf"), 0xE9, .. Encoding.ASCII.GetBytes("</a>")];

        XmlEncodingInfo info = XmlEncodingDetector.Detect(bytes);

        Assert.False(info.DeclarationMatchesBytes);
    }

    [Fact]
    public void Unknown_declared_encoding_falls_back_without_throwing()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"klingon-1\"?><a/>");

        XmlEncodingInfo info = XmlEncodingDetector.Detect(bytes);

        Assert.False(info.DeclarationMatchesBytes);
        Assert.Equal("klingon-1", info.DeclaredName);
        Assert.Equal(65001, info.Encoding.CodePage);
    }

    /// <summary>
    /// UTF-32 LE and UTF-16 LE both begin FF FE, so testing in the wrong order
    /// would misidentify every UTF-32 LE document.
    /// </summary>
    [Fact]
    public void Utf32_le_bom_is_not_mistaken_for_utf16()
    {
        byte[] bytes = [0xFF, 0xFE, 0x00, 0x00, .. new UTF32Encoding(false, false).GetBytes("<a/>")];

        Assert.Equal(12000, XmlEncodingDetector.Detect(bytes).Encoding.CodePage);
    }

    [Fact]
    public void Declaration_contradicting_the_bom_is_reported()
    {
        byte[] bytes =
        [
            0xEF, 0xBB, 0xBF,
            .. Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"windows-1252\"?><a/>"),
        ];

        XmlEncodingInfo info = XmlEncodingDetector.Detect(bytes);

        // The BOM wins, because it is unambiguous — but the contradiction is
        // still worth telling the user about rather than quietly resolving.
        Assert.Equal(65001, info.Encoding.CodePage);
        Assert.False(info.DeclarationMatchesBytes);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\" encoding='UTF-8'?><a/>", "UTF-8")]
    [InlineData("<?xml version=\"1.0\" encoding = \"UTF-8\" ?><a/>", "UTF-8")]
    [InlineData("<?xml version=\"1.0\"?><a/>", null)]
    public void Declaration_parsing_tolerates_spacing_and_quoting(string xml, string? expected)
    {
        Assert.Equal(expected, XmlEncodingDetector.Detect(Encoding.UTF8.GetBytes(xml)).DeclaredName);
    }
}
