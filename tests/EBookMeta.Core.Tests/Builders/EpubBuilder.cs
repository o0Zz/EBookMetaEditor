using System.Text;
using EBookMeta.Containers;

namespace EBookMeta.Tests.Builders;

/// <summary>
/// Generates synthetic EPUB files for the corpus.
/// </summary>
internal sealed class EpubBuilder
{
    private readonly List<Entry> _extraEntries = [];
    private string _opfPath = "OEBPS/content.opf";
    private string? _opfText;
    private byte[]? _opfBytes;
    private bool _includeMimetype = true;
    private bool _mimetypeStored = true;
    private bool _mimetypeFirst = true;
    private string _mimetypeContent = "application/epub+zip";
    private string? _containerXml = DefaultContainerXml;
    private bool _includeCoverImage = true;

    private sealed record Entry(string Name, byte[] Content, bool Stored);

    /// <summary>Uses the given OPF text instead of the default EPUB 3 one.</summary>
    internal EpubBuilder WithOpf(string opf)
    {
        _opfText = opf;
        return this;
    }

    /// <summary>Uses raw OPF bytes, for encoding fixtures.</summary>
    internal EpubBuilder WithOpfBytes(byte[] opf)
    {
        _opfBytes = opf;
        return this;
    }

    /// <summary>Omits the <c>mimetype</c> entry entirely (EPUB-E040).</summary>
    internal EpubBuilder WithoutMimetype()
    {
        _includeMimetype = false;
        return this;
    }

    /// <summary>Deflates <c>mimetype</c> instead of storing it (EPUB-E040).</summary>
    internal EpubBuilder WithCompressedMimetype()
    {
        _mimetypeStored = false;
        return this;
    }

    /// <summary>Writes <c>mimetype</c> somewhere other than first (EPUB-E040).</summary>
    internal EpubBuilder WithMimetypeNotFirst()
    {
        _mimetypeFirst = false;
        return this;
    }

    /// <summary>Gives <c>mimetype</c> the wrong content (EPUB-E040).</summary>
    internal EpubBuilder WithMimetypeContent(string content)
    {
        _mimetypeContent = content;
        return this;
    }

    /// <summary>Omits or replaces <c>META-INF/container.xml</c> (EPUB-F002).</summary>
    internal EpubBuilder WithContainerXml(string? xml)
    {
        _containerXml = xml;
        return this;
    }

    /// <summary>Changes where the package document lives.</summary>
    internal EpubBuilder WithOpfPath(string path)
    {
        _opfPath = path;
        return this;
    }

    /// <summary>Omits the cover image entry, leaving the manifest pointing at nothing.</summary>
    internal EpubBuilder WithoutCoverImage()
    {
        _includeCoverImage = false;
        return this;
    }

    /// <summary>Adds an arbitrary entry.</summary>
    internal EpubBuilder WithEntry(string name, byte[] content, bool stored = false)
    {
        _extraEntries.Add(new Entry(name, content, stored));
        return this;
    }

    /// <summary>Adds a text entry.</summary>
    internal EpubBuilder WithEntry(string name, string content) =>
        WithEntry(name, Encoding.UTF8.GetBytes(content));

    /// <summary>Builds the archive and writes it to a file.</summary>
    internal string WriteTo(string path)
    {
        ZipContainer.Create(BuildEntries(), path);
        return path;
    }

    /// <summary>Builds the archive in memory.</summary>
    internal byte[] Build()
    {
        string temp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ebookmeta-build-" + Guid.NewGuid().ToString("n") + ".zip");

        try
        {
            ZipContainer.Create(BuildEntries(), temp);
            return File.ReadAllBytes(temp);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private List<PendingEntry> BuildEntries()
    {
        var entries = new List<PendingEntry>();

        if (_includeMimetype && _mimetypeFirst)
        {
            entries.Add(Mimetype());
        }

        if (_containerXml is not null)
        {
            entries.Add(Deflated("META-INF/container.xml", Encoding.UTF8.GetBytes(_containerXml)));
        }

        entries.Add(Deflated(_opfPath, _opfBytes ?? Encoding.UTF8.GetBytes(_opfText ?? Epub3Opf)));

        entries.Add(Deflated(
            "OEBPS/text/chapter1.xhtml",
            Encoding.UTF8.GetBytes("<html><body><p>x</p></body></html>")));

        if (_includeCoverImage)
        {
            entries.Add(Deflated("OEBPS/images/cover.png", PngBuilder.OnePixel));
        }

        foreach (Entry entry in _extraEntries)
        {
            entries.Add(PendingEntry.FromBytes(
                entry.Name,
                entry.Content,
                entry.Stored ? ZipCompressionMethods.Stored : ZipCompressionMethods.Deflate,
                FixedTimestamp));
        }

        if (_includeMimetype && !_mimetypeFirst)
        {
            entries.Add(Mimetype());
        }

        return entries;
    }

    private PendingEntry Mimetype() => PendingEntry.FromBytes(
        "mimetype",
        Encoding.UTF8.GetBytes(_mimetypeContent),
        _mimetypeStored ? ZipCompressionMethods.Stored : ZipCompressionMethods.Deflate,
        FixedTimestamp);

    private static PendingEntry Deflated(string name, byte[] content) =>
        PendingEntry.FromBytes(name, content, ZipCompressionMethods.Deflate, FixedTimestamp);

    /// <summary>
    /// A fixed timestamp, so a fixture built twice is byte-identical and golden
    /// tests do not fail at midnight.
    /// </summary>
    private static readonly DateTimeOffset FixedTimestamp =
        new(2013, 6, 20, 12, 0, 0, TimeSpan.Zero);

    private const string DefaultContainerXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
        </container>
        """;

    /// <summary>
    /// A valid EPUB 3 package document exercising the refinement-based
    /// conventions: <c>file-as</c>, <c>role</c>, and a collection with a
    /// fractional group position.
    /// </summary>
    internal const string Epub3Opf = """
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title id="t1">The Ocean at the End of the Lane</dc:title>
            <meta refines="#t1" property="file-as">Ocean at the End of the Lane, The</meta>
            <dc:creator id="c1">Neil Gaiman</dc:creator>
            <meta refines="#c1" property="file-as">Gaiman, Neil</meta>
            <meta refines="#c1" property="role" scheme="marc:relators">aut</meta>
            <dc:contributor id="c2">Dave McKean</dc:contributor>
            <meta refines="#c2" property="role" scheme="marc:relators">ill</meta>
            <dc:identifier id="pub-id">urn:isbn:9781472200341</dc:identifier>
            <dc:language>en-GB</dc:language>
            <dc:publisher>Headline</dc:publisher>
            <dc:date>2013</dc:date>
            <dc:subject>Fantasy</dc:subject>
            <dc:subject>Childhood</dc:subject>
            <meta property="belongs-to-collection" id="col1">Sandman Universe</meta>
            <meta refines="#col1" property="collection-type">series</meta>
            <meta refines="#col1" property="group-position">2.5</meta>
            <meta property="custom:mood">wistful</meta>
          </metadata>
          <manifest>
            <item id="ch1" href="text/chapter1.xhtml" media-type="application/xhtml+xml"/>
            <item id="cover-img" href="images/cover.png" media-type="image/png" properties="cover-image"/>
          </manifest>
          <spine><itemref idref="ch1"/></spine>
        </package>
        """;

    /// <summary>
    /// A valid EPUB 2 package document using the attribute-based conventions:
    /// <c>opf:file-as</c>, <c>opf:role</c>, <c>calibre:series</c> and
    /// <c>meta name="cover"</c>.
    /// </summary>
    internal const string Epub2Opf = """
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="uuid">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">
            <dc:title opf:file-as="Neverwhere">Neverwhere</dc:title>
            <dc:creator opf:file-as="Gaiman, Neil" opf:role="aut">Neil Gaiman</dc:creator>
            <dc:identifier id="uuid" opf:scheme="UUID">urn:uuid:1234</dc:identifier>
            <dc:language>en</dc:language>
            <meta name="calibre:series" content="London Below"/>
            <meta name="calibre:series_index" content="1"/>
            <meta name="cover" content="cover-img"/>
          </metadata>
          <manifest>
            <item id="ch1" href="text/chapter1.xhtml" media-type="application/xhtml+xml"/>
            <item id="cover-img" href="images/cover.png" media-type="image/png"/>
          </manifest>
          <spine><itemref idref="ch1"/></spine>
        </package>
        """;

    /// <summary>
    /// <see cref="Epub2Opf"/> with the <c>xmlns:opf</c> declaration removed, so
    /// the <c>opf:file-as</c>, <c>opf:role</c> and <c>opf:scheme</c> attributes
    /// use a prefix that is never declared (EPUB-W070).
    /// </summary>
    internal const string Epub2OpfUndeclaredOpfPrefix = """
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="uuid">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title opf:file-as="Neverwhere">Neverwhere</dc:title>
            <dc:creator opf:file-as="Gaiman, Neil" opf:role="aut">Neil Gaiman</dc:creator>
            <dc:identifier id="uuid" opf:scheme="UUID">urn:uuid:1234</dc:identifier>
            <dc:language>en</dc:language>
            <meta name="cover" content="cover-img"/>
          </metadata>
          <manifest>
            <item id="ch1" href="text/chapter1.xhtml" media-type="application/xhtml+xml"/>
            <item id="cover-img" href="images/cover.png" media-type="image/png"/>
          </manifest>
          <spine><itemref idref="ch1"/></spine>
        </package>
        """;

    /// <summary>
    /// Uses a prefix no specification defines, so it is detectable but must not
    /// be repaired — the "report, never guess" boundary.
    /// </summary>
    internal const string OpfUnknownPrefix = """
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="uuid">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title acme:sort="Neverwhere">Neverwhere</dc:title>
            <dc:identifier id="uuid">urn:uuid:1234</dc:identifier>
            <dc:language>en</dc:language>
          </metadata>
          <manifest><item id="ch1" href="text/chapter1.xhtml" media-type="application/xhtml+xml"/></manifest>
          <spine><itemref idref="ch1"/></spine>
        </package>
        """;
}
