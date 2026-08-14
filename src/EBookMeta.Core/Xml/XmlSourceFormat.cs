using System.Xml.Linq;

namespace EBookMeta.Xml;

/// <summary>
/// Everything about how an XML document was written that its parsed tree does not
/// record, captured so a save can put it all back.
/// </summary>
internal sealed class XmlSourceFormat
{
    private XmlSourceFormat(
        XmlEncodingInfo encoding,
        string? declarationText,
        string prologSeparator,
        string epilogue,
        bool selfClosingHasSpace,
        string newLine)
    {
        Encoding = encoding;
        DeclarationText = declarationText;
        PrologSeparator = prologSeparator;
        Epilogue = epilogue;
        SelfClosingHasSpace = selfClosingHasSpace;
        NewLine = newLine;
    }

    /// <summary>What the bytes said about their own encoding.</summary>
    internal XmlEncodingInfo Encoding { get; }

    /// <summary>
    /// The XML declaration exactly as it appeared, or <see langword="null"/> if
    /// the document had none.
    /// </summary>
    internal string? DeclarationText { get; }

    /// <summary>Characters between the XML declaration and the rest of the document.</summary>
    internal string PrologSeparator { get; }

    /// <summary>Characters after the root element — usually a trailing newline.</summary>
    internal string Epilogue { get; }

    /// <summary>
    /// Whether the source wrote empty elements as <c>&lt;x /&gt;</c> rather than
    /// <c>&lt;x/&gt;</c>.
    /// </summary>
    internal bool SelfClosingHasSpace { get; }

    /// <summary>The line ending the source used.</summary>
    internal string NewLine { get; }

    /// <summary>Captures the formatting of a document from its decoded text.</summary>
    /// <param name="text">The document text, with any byte order mark removed.</param>
    /// <param name="encoding">The encoding the document was read as.</param>
    /// <returns>The formatting to restore on save.</returns>
    internal static XmlSourceFormat Detect(string text, XmlEncodingInfo encoding)
    {
        Throw.IfNull(text);
        Throw.IfNull(encoding);

        string? declaration = ExtractDeclaration(text);
        string prologSeparator = string.Empty;

        if (declaration is not null)
        {
            string rest = text.Substring(declaration.Length);
            prologSeparator = rest.Substring(0, rest.Length - rest.TrimStart().Length);
        }

        return new XmlSourceFormat(
            encoding,
            declaration,
            prologSeparator,
            text.Substring(text.TrimEnd().Length),
            DetectSelfClosingStyle(text),
            text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n");
    }

    /// <summary>
    /// The formatting to use for a document being created from nothing.
    /// </summary>
    /// <param name="encoding">The encoding to write in.</param>
    /// <param name="declarationText">The declaration to emit.</param>
    /// <param name="newLine">The line ending to use.</param>
    /// <returns>Formatting for a new document.</returns>
    internal static XmlSourceFormat ForNewDocument(
        XmlEncodingInfo encoding, string declarationText, string newLine)
    {
        Throw.IfNull(encoding);
        Throw.IfNullOrEmpty(declarationText);
        Throw.IfNullOrEmpty(newLine);

        return new XmlSourceFormat(
            encoding, declarationText, newLine, newLine, selfClosingHasSpace: false, newLine);
    }

    /// <summary>Serialises a document back to bytes in its original shape.</summary>
    /// <param name="root">The root element, or null for an empty document.</param>
    /// <returns>The complete document bytes.</returns>
    internal byte[] Compose(XElement? root)
    {
        string body = root is null ? string.Empty : XmlExactWriter.Write(root, SelfClosingHasSpace);

        if (NewLine != "\n")
        {
            // Restore the source's line endings, which XML parsing normalised
            // away. The body contains only LF at this point, so this cannot
            // double up existing CRLFs.
            body = body.Replace("\n", NewLine);
        }

        string text = DeclarationText is null
            ? body + Epilogue
            : DeclarationText + PrologSeparator + body + Epilogue;

        // Shared with the repair path, which has to re-encode the same document
        // after a surgical edit and must preserve the BOM identically.
        return XmlEncodingDetector.Encode(text, Encoding);
    }

    /// <summary>
    /// Captures the XML declaration as literal text.
    /// </summary>
    private static string? ExtractDeclaration(string text)
    {
        int start = text.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0 || text.Substring(0, start).TrimStart().Length > 0)
        {
            return null;
        }

        int end = text.IndexOf("?>", start, StringComparison.Ordinal);
        return end < 0 ? null : text.Substring(start, end + 2 - start);
    }

    /// <summary>
    /// Decides whether the document writes empty elements with a space before
    /// the slash, by counting how it does it.
    /// </summary>
    private static bool DetectSelfClosingStyle(string text)
    {
        int withSpace = 0;
        int without = 0;

        for (int i = 1; i + 1 < text.Length; i++)
        {
            if (text[i] != '/' || text[i + 1] != '>')
            {
                continue;
            }

            if (text[i - 1] == ' ')
            {
                withSpace++;
            }
            else
            {
                without++;
            }
        }

        // A document with no empty elements at all gets the majority-of-nothing
        // default, which is the compact form the corpus overwhelmingly uses.
        return withSpace > without;
    }
}
