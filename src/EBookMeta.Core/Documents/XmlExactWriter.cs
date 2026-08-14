using System.Text;
using System.Xml.Linq;

namespace EBookMeta.Documents;

/// <summary>
/// Serialises an <see cref="XElement"/> tree back to text as close to the
/// original characters as the tree allows.
/// </summary>
/// <remarks>
/// <para>
/// Neither <c>XElement.ToString</c> nor <c>XmlWriter</c> is faithful enough for
/// this project, and the two ways they diverge both matter.
/// </para>
/// <para>
/// <b>Empty elements.</b> Both always emit <c>&lt;x /&gt;</c> with a space, with
/// no setting to change it, while most EPUBs write <c>&lt;x/&gt;</c>. Left
/// alone, a one-word title fix rewrites every manifest item in the file,
/// which is a whole-file change from an edit the user made to one field.
/// </para>
/// <para>
/// <b>Namespace prefixes.</b> When a namespace is bound to both the default and
/// an explicit prefix — extremely common in EPUB 2, where
/// <c>xmlns:opf</c> repeats the package's own default namespace — XLinq re-picks
/// the explicit prefix, rewriting <c>&lt;metadata&gt;</c> as
/// <c>&lt;opf:metadata&gt;</c>. That is semantically identical and cosmetically
/// disastrous, and it violates the rule against inventing prefixes. Here an
/// element whose namespace matches the in-scope default is written unprefixed,
/// which reproduces what the author wrote.
/// </para>
/// </remarks>
internal static class XmlExactWriter
{
    /// <summary>Serialises an element and its descendants.</summary>
    /// <param name="root">The element to write.</param>
    /// <param name="selfClosingSpace">
    /// Whether to write <c>&lt;x /&gt;</c> rather than <c>&lt;x/&gt;</c>.
    /// </param>
    /// <returns>The serialised XML.</returns>
    internal static string Write(XElement root, bool selfClosingSpace)
    {
        var builder = new StringBuilder(1024);
        WriteNode(builder, root, selfClosingSpace);
        return builder.ToString();
    }

    private static void WriteNode(StringBuilder builder, XNode node, bool selfClosingSpace)
    {
        switch (node)
        {
            case XElement element:
                WriteElement(builder, element, selfClosingSpace);
                break;

            case XText text:
                // CDATA is a subclass of XText and must keep its wrapper.
                if (text is XCData cdata)
                {
                    builder.Append("<![CDATA[").Append(cdata.Value).Append("]]>");
                }
                else
                {
                    EscapeText(builder, text.Value);
                }

                break;

            case XComment comment:
                builder.Append("<!--").Append(comment.Value).Append("-->");
                break;

            case XProcessingInstruction pi:
                builder.Append("<?").Append(pi.Target);
                if (pi.Data.Length > 0)
                {
                    builder.Append(' ').Append(pi.Data);
                }

                builder.Append("?>");
                break;

            case XDocumentType doctype:
                builder.Append("<!DOCTYPE ").Append(doctype.Name).Append('>');
                break;
        }
    }

    private static void WriteElement(StringBuilder builder, XElement element, bool selfClosingSpace)
    {
        string name = QualifiedName(element);

        builder.Append('<').Append(name);

        foreach (XAttribute attribute in element.Attributes())
        {
            builder.Append(' ').Append(AttributeName(element, attribute)).Append("=\"");
            EscapeAttribute(builder, attribute.Value);
            builder.Append('"');
        }

        if (element.IsEmpty)
        {
            builder.Append(selfClosingSpace ? " />" : "/>");
            return;
        }

        builder.Append('>');

        foreach (XNode child in element.Nodes())
        {
            WriteNode(builder, child, selfClosingSpace);
        }

        builder.Append("</").Append(name).Append('>');
    }

    /// <summary>
    /// Chooses the prefix an element should be written with.
    /// </summary>
    /// <remarks>
    /// Prefers no prefix whenever the element's namespace is the one in scope as
    /// the default. This is the rule that keeps <c>&lt;metadata&gt;</c> from
    /// becoming <c>&lt;opf:metadata&gt;</c> in files that redundantly bind
    /// <c>xmlns:opf</c> to the package namespace.
    /// </remarks>
    private static string QualifiedName(XElement element)
    {
        XNamespace ns = element.Name.Namespace;

        if (ns == XNamespace.None || DefaultNamespaceInScope(element) == ns)
        {
            return element.Name.LocalName;
        }

        string? prefix = element.GetPrefixOfNamespace(ns);
        return string.IsNullOrEmpty(prefix)
            ? element.Name.LocalName
            : prefix + ":" + element.Name.LocalName;
    }

    private static string AttributeName(XElement owner, XAttribute attribute)
    {
        if (attribute.IsNamespaceDeclaration)
        {
            // A default declaration is the attribute literally named "xmlns";
            // a prefixed one carries the prefix as its local name.
            return attribute.Name.Namespace == XNamespace.None
                ? "xmlns"
                : "xmlns:" + attribute.Name.LocalName;
        }

        XNamespace ns = attribute.Name.Namespace;
        if (ns == XNamespace.None)
        {
            // Unprefixed attributes are never in the default namespace, so this
            // must stay bare rather than picking up the element's prefix.
            return attribute.Name.LocalName;
        }

        string? prefix = owner.GetPrefixOfNamespace(ns);
        return string.IsNullOrEmpty(prefix)
            ? attribute.Name.LocalName
            : prefix + ":" + attribute.Name.LocalName;
    }

    private static XNamespace? DefaultNamespaceInScope(XElement element)
    {
        for (XElement? e = element; e is not null; e = e.Parent)
        {
            XAttribute? declaration = e.Attribute("xmlns");
            if (declaration is not null)
            {
                return declaration.Value.Length == 0
                    ? XNamespace.None
                    : XNamespace.Get(declaration.Value);
            }
        }

        return null;
    }

    private static void EscapeText(StringBuilder builder, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                default: builder.Append(c); break;
            }
        }
    }

    private static void EscapeAttribute(StringBuilder builder, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;

                // Line breaks and tabs inside an attribute would otherwise be
                // normalised to spaces by a conforming parser on the way back
                // in, silently changing the value.
                case '\t': builder.Append("&#x9;"); break;
                case '\n': builder.Append("&#xA;"); break;
                case '\r': builder.Append("&#xD;"); break;

                default: builder.Append(c); break;
            }
        }
    }
}
