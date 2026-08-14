using System.Text;
using EBookMeta.Containers;

namespace EBookMeta.Tests.Builders;

/// <summary>
/// Generates synthetic comic archives for the corpus.
/// </summary>
/// <remarks>
/// <para>
/// Same contract as <see cref="EpubBuilder"/>: defaults produce a valid CBZ, so a
/// fixture for one specific defect is one call away from correct rather than
/// assembled from scratch, which keeps a "broken" fixture broken in exactly one
/// way.
/// </para>
/// <para>
/// Pages are 1×1 PNGs. A comic's real content is irrelevant to metadata editing
/// and committing real page images would be both pointless and a copyright
/// problem, so the corpus is three pixels pretending to be a comic.
/// </para>
/// </remarks>
internal sealed class CbzBuilder
{
    private readonly List<Entry> _extraEntries = [];
    private string[] _pageNames = ["01.png", "02.png", "03.png"];
    private string? _comicInfoText = DefaultComicInfo;
    private byte[]? _comicInfoBytes;
    private string _comicInfoPath = "ComicInfo.xml";
    private bool _comicInfoFirst = true;
    private string? _cometXml;

    private sealed record Entry(string Name, byte[] Content, bool Stored);

    /// <summary>Uses the given <c>ComicInfo.xml</c> instead of the default.</summary>
    /// <param name="xml">The complete metadata document.</param>
    /// <returns>This builder.</returns>
    internal CbzBuilder WithComicInfo(string xml)
    {
        _comicInfoText = xml;
        return this;
    }

    /// <summary>Uses raw <c>ComicInfo.xml</c> bytes, for encoding fixtures.</summary>
    /// <param name="xml">The complete document bytes.</param>
    /// <returns>This builder.</returns>
    internal CbzBuilder WithComicInfoBytes(byte[] xml)
    {
        _comicInfoBytes = xml;
        return this;
    }

    /// <summary>Omits the metadata document entirely (CBZ-W010).</summary>
    /// <returns>This builder.</returns>
    internal CbzBuilder WithoutComicInfo()
    {
        _comicInfoText = null;
        _comicInfoBytes = null;
        return this;
    }

    /// <summary>Puts the metadata document somewhere other than the root (CBZ-E011).</summary>
    /// <param name="path">The entry name to write it as.</param>
    /// <returns>This builder.</returns>
    internal CbzBuilder WithComicInfoAt(string path)
    {
        _comicInfoPath = path;
        return this;
    }

    /// <summary>Writes the metadata document after the pages rather than before.</summary>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Both layouts are common in the wild, and where the entry sits decides
    /// whether <see cref="EBookMeta.Formats.FormatDetector"/> settles the format
    /// from the first local header or has to read the central directory.
    /// </remarks>
    internal CbzBuilder WithComicInfoLast()
    {
        _comicInfoFirst = false;
        return this;
    }

    /// <summary>Adds a CoMet document alongside <c>ComicInfo.xml</c> (CBZ-W012).</summary>
    /// <param name="xml">The CoMet document, or the default when null.</param>
    /// <returns>This builder.</returns>
    internal CbzBuilder WithCoMet(string? xml = null)
    {
        _cometXml = xml ?? DefaultCoMet;
        return this;
    }

    /// <summary>Replaces the page images with the given names, in order.</summary>
    /// <param name="names">The page entry names.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Drives the page rules: a different count from <c>PageCount</c> is
    /// CBZ-E020, and names that do not sort into reading order are CBZ-W022.
    /// </remarks>
    internal CbzBuilder WithPages(params string[] names)
    {
        _pageNames = names;
        return this;
    }

    /// <summary>Adds an arbitrary entry.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="content">The content.</param>
    /// <param name="stored">Whether to store rather than deflate it.</param>
    /// <returns>This builder.</returns>
    internal CbzBuilder WithEntry(string name, byte[] content, bool stored = false)
    {
        _extraEntries.Add(new Entry(name, content, stored));
        return this;
    }

    /// <summary>Adds a text entry.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="content">The content.</param>
    /// <returns>This builder.</returns>
    internal CbzBuilder WithEntry(string name, string content) =>
        WithEntry(name, Encoding.UTF8.GetBytes(content));

    /// <summary>Builds the archive and writes it to a file.</summary>
    /// <param name="path">Where to write.</param>
    /// <returns>The path written.</returns>
    /// <remarks>
    /// Through <see cref="ZipContainer.Create"/>, the same writer the product
    /// saves with, for the reason given on <see cref="EpubBuilder.WriteTo"/>.
    /// </remarks>
    internal string WriteTo(string path)
    {
        ZipContainer.Create(BuildEntries(), path);
        return path;
    }

    /// <summary>Builds the archive in memory.</summary>
    /// <returns>The complete CBZ bytes.</returns>
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

    /// <summary>
    /// Appends an archive-level comment to a finished ZIP file.
    /// </summary>
    /// <param name="path">The archive to patch.</param>
    /// <param name="comment">The comment to store.</param>
    /// <remarks>
    /// <para>
    /// Comic archives sometimes carry a ComicBookLover JSON blob here, and
    /// <c>CbzHandler</c> refuses to write such a file because
    /// <see cref="ZipContainer.Rebuild"/> cannot reproduce the comment. Testing
    /// that refusal needs a fixture that has one.
    /// </para>
    /// <para>
    /// Patched in rather than written by the ZIP writer because no writer here can
    /// produce one: <c>System.IO.Compression</c> on net48 has no comment API at
    /// all, and <see cref="ZipContainer.Create"/> deliberately does not add one.
    /// The end-of-central-directory record is fixed-size apart from the comment,
    /// so setting its length field and appending the bytes is the whole job.
    /// </para>
    /// </remarks>
    internal static void AddArchiveComment(string path, string comment)
    {
        byte[] archive = File.ReadAllBytes(path);
        byte[] bytes = Encoding.UTF8.GetBytes(comment);

        int eocd = FindEndOfCentralDirectory(archive);
        if (eocd < 0)
        {
            throw new InvalidDataException($"'{path}' has no end-of-central-directory record.");
        }

        // Offset 20 in the record is the two-byte comment length; the comment
        // itself is everything after the record.
        archive[eocd + 20] = (byte)(bytes.Length & 0xFF);
        archive[eocd + 21] = (byte)((bytes.Length >> 8) & 0xFF);

        using FileStream output = File.Create(path);
        output.Write(archive, 0, eocd + 22);
        output.Write(bytes, 0, bytes.Length);
    }

    private static int FindEndOfCentralDirectory(byte[] archive)
    {
        // Scanned from the end rather than assumed to be the last 22 bytes, so
        // this keeps working on an archive that already has a comment.
        for (int i = archive.Length - 22; i >= 0; i--)
        {
            if (archive[i] == 0x50 && archive[i + 1] == 0x4B &&
                archive[i + 2] == 0x05 && archive[i + 3] == 0x06)
            {
                return i;
            }
        }

        return -1;
    }

    private List<PendingEntry> BuildEntries()
    {
        var entries = new List<PendingEntry>();

        if (_comicInfoFirst)
        {
            AddComicInfo(entries);
        }

        foreach (string name in _pageNames)
        {
            entries.Add(Deflated(name, PngBuilder.OnePixel));
        }

        if (_cometXml is not null)
        {
            entries.Add(Deflated("comet.xml", Encoding.UTF8.GetBytes(_cometXml)));
        }

        foreach (Entry entry in _extraEntries)
        {
            entries.Add(PendingEntry.FromBytes(
                entry.Name,
                entry.Content,
                entry.Stored ? ZipCompressionMethods.Stored : ZipCompressionMethods.Deflate,
                FixedTimestamp));
        }

        if (!_comicInfoFirst)
        {
            AddComicInfo(entries);
        }

        return entries;
    }

    private void AddComicInfo(List<PendingEntry> entries)
    {
        if (_comicInfoBytes is null && _comicInfoText is null)
        {
            return;
        }

        entries.Add(Deflated(
            _comicInfoPath, _comicInfoBytes ?? Encoding.UTF8.GetBytes(_comicInfoText!)));
    }

    private static PendingEntry Deflated(string name, byte[] content) =>
        PendingEntry.FromBytes(name, content, ZipCompressionMethods.Deflate, FixedTimestamp);

    /// <summary>
    /// A fixed timestamp, so a fixture built twice is byte-identical and golden
    /// tests do not fail at midnight.
    /// </summary>
    private static readonly DateTimeOffset FixedTimestamp =
        new(2013, 6, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A valid ComicRack document with elements in schema order, including two
    /// this build maps onto no model field (<c>Notes</c>, <c>AgeRating</c>) so
    /// their survival across a write is testable.
    /// </summary>
    internal const string DefaultComicInfo = """
        <?xml version="1.0" encoding="utf-8"?>
        <ComicInfo xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <Title>The Doll's House</Title>
          <Series>The Sandman</Series>
          <Number>2.5</Number>
          <Count>75</Count>
          <Volume>1989</Volume>
          <Summary>A short summary.</Summary>
          <Notes>Tagged with ComicTagger 1.3.2</Notes>
          <Year>1989</Year>
          <Month>3</Month>
          <Day>7</Day>
          <Writer>Neil Gaiman</Writer>
          <Penciller>Mike Dringenberg, Malcolm Jones III</Penciller>
          <CoverArtist>Dave McKean</CoverArtist>
          <Publisher>DC Comics</Publisher>
          <Genre>Fantasy, Horror</Genre>
          <PageCount>3</PageCount>
          <LanguageISO>en</LanguageISO>
          <AgeRating>Mature 17+</AgeRating>
          <Pages>
            <Page Image="0" Type="FrontCover"/>
            <Page Image="1"/>
            <Page Image="2"/>
          </Pages>
        </ComicInfo>
        """;

    /// <summary>
    /// The minimum ComicRack document: a series and nothing else. The realistic
    /// shape of a file tagged by a script rather than an application.
    /// </summary>
    internal const string MinimalComicInfo = """
        <?xml version="1.0" encoding="utf-8"?>
        <ComicInfo>
          <Series>The Sandman</Series>
        </ComicInfo>
        """;

    /// <summary>A CoMet document that disagrees with <see cref="DefaultComicInfo"/> (CBZ-W012).</summary>
    internal const string DefaultCoMet = """
        <?xml version="1.0" encoding="utf-8"?>
        <comet xmlns:comet="http://www.denvog.com/comet/">
          <title>A Different Title</title>
          <series>Something Else</series>
        </comet>
        """;
}
