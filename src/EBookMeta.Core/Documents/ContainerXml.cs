using System.Xml.Linq;
using EBookMeta.Containers;

namespace EBookMeta.Documents;

/// <summary>
/// <c>META-INF/container.xml</c> — the file that says where an EPUB's package
/// document lives.
/// </summary>
public sealed class ContainerXml
{
    /// <summary>The entry name, which the EPUB specification fixes.</summary>
    public const string EntryName = "META-INF/container.xml";

    private static readonly XNamespace Ns = "urn:oasis:names:tc:opendocument:xmlns:container";

    private ContainerXml(IReadOnlyList<string> rootfilePaths)
    {
        RootfilePaths = rootfilePaths;
    }

    /// <summary>
    /// The <c>full-path</c> of every declared rootfile, in document order.
    /// </summary>
    public IReadOnlyList<string> RootfilePaths { get; }

    /// <summary>
    /// The package document path to edit, or <see langword="null"/> if none was
    /// declared.
    /// </summary>
    public string? PrimaryRootfilePath => RootfilePaths.Count > 0 ? RootfilePaths[0] : null;

    /// <summary>Parses <c>META-INF/container.xml</c> from its bytes.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>The parsed container description.</returns>
    /// <exception cref="BookFormatException">
    /// The document is not well-formed. Surfaced as EPUB-F002.
    /// </exception>
    public static ContainerXml Parse(ReadOnlySpan<byte> bytes)
    {
        XmlEncodingInfo encoding = XmlEncodingDetector.Detect(bytes);
        string text = XmlEncodingDetector.Decode(bytes, encoding);

        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new BookFormatException(
                $"'{EntryName}' is not well-formed XML: {ex.Message}", EntryName, ex);
        }

        // Match the rootfile elements namespace-agnostically. The container
        // namespace is fixed by spec, but files that omit or misspell it are
        // still readable and refusing them would help nobody.
        List<string> paths = [.. document
            .Descendants()
            .Where(e => e.Name.LocalName == "rootfile")
            .Select(e => (string?)e.Attribute("full-path"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)];

        return new ContainerXml(paths);
    }

    /// <summary>Reads and parses <c>META-INF/container.xml</c> from a container.</summary>
    /// <param name="container">The open EPUB container.</param>
    /// <returns>The parsed container description.</returns>
    /// <exception cref="BookFormatException">
    /// The entry is missing or not well-formed. Surfaced as EPUB-F002.
    /// </exception>
    public static ContainerXml Read(IContainer container)
    {
        Throw.IfNull(container);

        ContainerEntry? entry = container.Entries.FirstOrDefault(
            e => e.Name.Equals(EntryName, StringComparison.Ordinal));

        // Some producers get the casing wrong. Accept it on read and report it,
        // rather than declaring the book unopenable over a capital letter.
        entry ??= container.Entries.FirstOrDefault(
            e => e.Name.Equals(EntryName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new BookFormatException($"'{EntryName}' is missing.", EntryName);
        }

        return Parse(container.ReadAllBytes(entry));
    }

}
