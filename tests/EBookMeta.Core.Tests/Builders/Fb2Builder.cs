using System.Text;
using EBookMeta.Containers;

namespace EBookMeta.Tests.Builders;

/// <summary>
/// Generates synthetic FictionBook documents for the corpus.
/// </summary>
internal sealed class Fb2Builder
{
    private string _description = DefaultDescription;
    private string _body = DefaultBody;
    private string _binaries = DefaultBinary;
    private string _declaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";
    private string _rootAttributes =
        " xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\""
        + " xmlns:l=\"http://www.w3.org/1999/xlink\"";

    private Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private string _newLine = "\n";

    /// <summary>Uses the given <c>&lt;description&gt;</c> instead of the default.</summary>
    internal Fb2Builder WithDescription(string xml)
    {
        _description = xml;
        return this;
    }

    /// <summary>Omits the description entirely (FB2-F002).</summary>
    internal Fb2Builder WithoutDescription()
    {
        _description = string.Empty;
        return this;
    }

    /// <summary>Replaces the root element's attributes, for namespace fixtures.</summary>
    internal Fb2Builder WithRootAttributes(string attributes)
    {
        _rootAttributes = attributes;
        return this;
    }

    /// <summary>Omits the base64 image the cover page points at.</summary>
    internal Fb2Builder WithoutBinaries()
    {
        _binaries = string.Empty;
        return this;
    }

    /// <summary>Uses a body large enough to notice if a save rewrote it.</summary>
    internal Fb2Builder WithLargeBody(int paragraphs)
    {
        var builder = new StringBuilder("  <body>\n");

        for (int i = 0; i < paragraphs; i++)
        {
            builder.Append("    <p>Paragraph ")
                .Append(i)
                .Append(" with &amp; an entity and <emphasis>markup</emphasis>.</p>\n");
        }

        _body = builder.Append("  </body>").ToString();
        return this;
    }

    /// <summary>Writes the document in a non-UTF-8 encoding.</summary>
    internal Fb2Builder WithEncoding(Encoding encoding, string declaredName)
    {
        _encoding = encoding;
        _declaration = $"<?xml version=\"1.0\" encoding=\"{declaredName}\"?>";
        return this;
    }

    /// <summary>Writes the document with CRLF line endings.</summary>
    internal Fb2Builder WithWindowsLineEndings()
    {
        _newLine = "\r\n";
        return this;
    }

    /// <summary>Builds the document bytes.</summary>
    internal byte[] Build()
    {
        var builder = new StringBuilder();
        builder.Append(_declaration).Append('\n');
        builder.Append("<FictionBook").Append(_rootAttributes).Append(">\n");

        if (_description.Length > 0)
        {
            builder.Append(_description).Append('\n');
        }

        builder.Append(_body).Append('\n');

        if (_binaries.Length > 0)
        {
            builder.Append(_binaries).Append('\n');
        }

        builder.Append("</FictionBook>\n");

        string text = builder.ToString();

        if (_newLine != "\n")
        {
            text = text.Replace("\n", _newLine);
        }

        byte[] content = _encoding.GetBytes(text);
        byte[] preamble = _encoding.GetPreamble();

        if (preamble.Length == 0)
        {
            return content;
        }

        byte[] result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result, 0);
        content.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>Builds the document and writes it to a file.</summary>
    internal string WriteTo(string path)
    {
        File.WriteAllBytes(path, Build());
        return path;
    }

    /// <summary>Builds the document inside a ZIP, as an <c>.fb2.zip</c>.</summary>
    internal string WriteZipTo(string path, string entryName = "book.fb2")
    {
        ZipContainer.Create(
            [PendingEntry.FromBytes(entryName, Build(), ZipCompressionMethods.Deflate, Timestamp)],
            path);

        return path;
    }

    private static readonly DateTimeOffset Timestamp =
        new(2013, 6, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A description with every field this build maps, plus a document-info that
    /// maps onto nothing so its survival across a write is testable.
    /// </summary>
    internal const string DefaultDescription = """
          <description>
            <title-info>
              <genre>sf</genre>
              <author>
                <first-name>Neil</first-name>
                <last-name>Gaiman</last-name>
              </author>
              <book-title>The Doll's House</book-title>
              <annotation>
                <p>A short summary.</p>
              </annotation>
              <keywords>fantasy, horror</keywords>
              <date value="1989-03-07">1989</date>
              <coverpage>
                <image l:href="#cover.png"/>
              </coverpage>
              <lang>en</lang>
              <sequence name="The Sandman" number="2"/>
            </title-info>
            <document-info>
              <author>
                <nickname>scanner</nickname>
              </author>
              <program-used>FB Tools</program-used>
              <date value="2005-01-01">2005</date>
              <id>abc-123</id>
              <version>1.1</version>
            </document-info>
            <publish-info>
              <book-name>The Doll's House</book-name>
              <publisher>DC Comics</publisher>
              <city>New York</city>
              <year>1989</year>
              <isbn>978-1-4012-8477-1</isbn>
            </publish-info>
          </description>
        """;

    /// <summary>The minimum a reader would accept: a title, a language, an author.</summary>
    internal const string MinimalDescription = """
          <description>
            <title-info>
              <author>
                <last-name>Gaiman</last-name>
              </author>
              <book-title>The Doll's House</book-title>
              <lang>en</lang>
            </title-info>
          </description>
        """;

    private const string DefaultBody = """
          <body>
            <section>
              <p>Once upon a time.</p>
            </section>
          </body>
        """;

    /// <summary>A 1x1 PNG, base64-encoded the way FB2 embeds images.</summary>
    private static readonly string DefaultBinary =
        "  <binary id=\"cover.png\" content-type=\"image/png\">"
        + Convert.ToBase64String(PngBuilder.OnePixel)
        + "</binary>";
}
