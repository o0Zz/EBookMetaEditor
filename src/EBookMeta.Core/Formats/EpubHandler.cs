using EBookMeta.Containers;
using EBookMeta.Documents;
using EBookMeta.Model;

namespace EBookMeta.Formats;

/// <summary>
/// Reads, validates and writes EPUB 2 and EPUB 3 metadata.
/// </summary>
/// <remarks>
/// Opens only what it needs: <c>META-INF/container.xml</c> to find the package
/// document, then the package document itself. The rest of the archive is left
/// alone, because cold launch to a populated window has a 400 ms budget and a
/// book with 500 manifest entries must not be walked to show its title.
/// </remarks>
public sealed class EpubHandler : IFormatHandler
{
    /// <inheritdoc />
    public FormatId Id => FormatId.Epub;

    /// <inheritdoc />
    public FormatCapabilities Capabilities { get; } = new()
    {
        Format = FormatId.Epub,

        // EPUB expresses everything in the model, which is why it is phase 1:
        // it exercises the whole surface.
        ReadableFields = MetadataField.All,
        WritableFields = MetadataField.All,
    };

    /// <summary>
    /// Opens the package document referenced by <c>META-INF/container.xml</c>.
    /// </summary>
    /// <param name="container">The open EPUB container.</param>
    /// <returns>The parsed package document and the entry it came from.</returns>
    /// <exception cref="BookFormatException">
    /// The container file or the package document is missing or malformed.
    /// </exception>
    public static OpfDocument OpenPackageDocument(IContainer container)
    {
        Throw.IfNull(container);

        ContainerXml containerXml = ContainerXml.Read(container);
        string? opfPath = containerXml.PrimaryRootfilePath;

        if (opfPath is null)
        {
            throw new BookFormatException(
                $"'{ContainerXml.EntryName}' declares no rootfile.", ContainerXml.EntryName);
        }

        ContainerEntry? entry = FindEntry(container, opfPath);
        if (entry is null)
        {
            throw new BookFormatException(
                $"'{ContainerXml.EntryName}' points at '{opfPath}', which is not in the archive.",
                ContainerXml.EntryName);
        }

        byte[] bytes = ReadAllBytes(container, entry);

        try
        {
            return OpfDocument.Parse(bytes, entry.Name);
        }
        catch (BookFormatException)
        {
            // Recoverable damage is corrected here, on the way in, so every
            // caller downstream gets a document that parses. Read shows the user
            // correct metadata, Write serialises a correct tree, and the
            // correction reaches the disk only when they save — there is no
            // repair-specific write path, and therefore no way for a repair to
            // rewrite a file on its own.
            //
            // Only a repair that fully succeeds is used. A partial fix would
            // hand back a document that still does not parse, so the original
            // error is the more useful answer.
            NamespaceRepairResult? repair = NamespaceRepair.Repair(bytes, entry.Name);

            if (repair is not { IsComplete: true, HasChanges: true })
            {
                if (repair?.Skipped.Count > 0)
                {
                    Log.Error(
                        $"'{entry.Name}' cannot be repaired: prefixes {string.Join(", ", repair.Skipped)} "
                        + "are undeclared and no known namespace applies to them.");
                }

                throw;
            }

            Log.Warning(
                $"'{entry.Name}' was missing namespace declarations and has been repaired in memory: "
                + $"added xmlns for {string.Join(", ", repair.Added)}. Save to keep the correction.");

            return OpfDocument.Parse(repair.RepairedBytes, entry.Name);
        }
    }

    /// <summary>
    /// Locates the package document and returns its bytes without parsing them.
    /// </summary>
    /// <param name="container">The EPUB container.</param>
    /// <returns>The entry name and its raw bytes.</returns>
    /// <exception cref="BookFormatException">
    /// <c>container.xml</c> is missing, declares no rootfile, or points at an
    /// entry that is not in the archive.
    /// </exception>
    /// <remarks>
    /// The repair path needs this. <see cref="OpenPackageDocument"/> parses, and
    /// therefore throws on exactly the documents a repair exists to fix — so the
    /// bytes of a package document that will not parse are unreachable through
    /// it. Resolving the rootfile only requires <c>container.xml</c>, which is a
    /// separate document and usually intact.
    /// </remarks>
    public static RawPackageDocument ReadRawPackageDocument(IContainer container)
    {
        Throw.IfNull(container);

        ContainerXml containerXml = ContainerXml.Read(container);
        string? opfPath = containerXml.PrimaryRootfilePath;

        if (opfPath is null)
        {
            throw new BookFormatException(
                $"'{ContainerXml.EntryName}' declares no rootfile.", ContainerXml.EntryName);
        }

        ContainerEntry? entry = FindEntry(container, opfPath);
        if (entry is null)
        {
            throw new BookFormatException(
                $"'{ContainerXml.EntryName}' points at '{opfPath}', which is not in the archive.",
                ContainerXml.EntryName);
        }

        return new RawPackageDocument
        {
            EntryName = entry.Name,
            Bytes = ReadAllBytes(container, entry),
        };
    }

    /// <inheritdoc />
    public BookMetadata Read(IContainer container, ReadOptions? options = null)
    {
        Throw.IfNull(container);

        options ??= ReadOptions.Default;

        OpfDocument opf = OpenPackageDocument(container);
        BookMetadata metadata = opf.ReadMetadata();

        if (options.IncludeCover)
        {
            ReadCover(container, opf, metadata);
        }

        Log.Info(
            $"Read EPUB {opf.Version ?? "(unversioned)"} metadata from '{opf.EntryName}': "
            + $"title={Describe(metadata.Title)}, creators={metadata.Creators.Count}, "
            + $"cover={(metadata.Cover is null ? "none" : metadata.Cover.MediaType)}.");

        return metadata;
    }

    private static string Describe(string? value) =>
        value is null ? "(none)" : $"\"{value}\"";

    /// <inheritdoc />
    public void Write(IContainer container, BookMetadata metadata, string targetPath)
    {
        Throw.IfNull(container);
        Throw.IfNull(metadata);
        Throw.IfNullOrEmpty(targetPath);

        OpfDocument opf = OpenPackageDocument(container);
        opf.ApplyMetadata(metadata);

        byte[]? coverBytes = ApplyCover(opf, metadata, out string? coverEntryName);
        byte[] opfBytes = opf.Serialize();

        // Every entry is copied through byte for byte except the two we mean to
        // change. Content files, stylesheets and page images never go near a
        // parser, and order and per-entry compression method are carried over,
        // so an unedited save reproduces the original exactly.
        var entries = new List<PendingEntry>(container.Entries.Count);

        foreach (ContainerEntry entry in container.Entries)
        {
            if (entry.Name.Equals(opf.EntryName, StringComparison.Ordinal))
            {
                entries.Add(PendingEntry.FromBytes(
                    entry.Name, opfBytes, entry.CompressionMethod, entry.LastModified));
            }
            else if (coverBytes is not null &&
                     entry.Name.Equals(coverEntryName, StringComparison.Ordinal))
            {
                entries.Add(PendingEntry.FromBytes(
                    entry.Name, coverBytes, entry.CompressionMethod, entry.LastModified));
            }
            else
            {
                entries.Add(PendingEntry.CopyOf(container, entry));
            }
        }

        container.Rebuild(entries, targetPath);

        Log.Info(
            $"Wrote {entries.Count} entries, replacing '{opf.EntryName}'"
            + (coverEntryName is null ? "." : $" and '{coverEntryName}'."));
    }

    /// <summary>
    /// Declares the cover in both conventions and reports replacement image
    /// bytes, when the user supplied a new image.
    /// </summary>
    /// <returns>
    /// The new image bytes to substitute, or <see langword="null"/> when the
    /// cover image itself is unchanged.
    /// </returns>
    /// <remarks>
    /// Replacing the bytes of the existing entry rather than adding a new one
    /// keeps the manifest, the spine and every href pointing where they already
    /// pointed. Adding an entry would mean rewriting references, which is a much
    /// larger change to a file the user only wanted a new cover on.
    /// </remarks>
    private static byte[]? ApplyCover(OpfDocument opf, BookMetadata metadata, out string? coverEntryName)
    {
        coverEntryName = null;

        if (metadata.Cover is not { } cover)
        {
            return null;
        }

        string? manifestId = cover.SourceManifestId
            ?? opf.Manifest.FirstOrDefault(i => i.IsCoverImage)?.Id;

        // Only rewrite the declarations if they do not already say what we are
        // about to say. Otherwise opening an EPUB 3 and saving it unchanged
        // would add the EPUB 2 <meta name="cover"> form, altering a file the
        // user never edited.
        if (manifestId is not null && !opf.CoverIsAlreadyDeclaredAs(manifestId))
        {
            opf.ApplyCoverDeclaration(manifestId);
        }

        coverEntryName = cover.SourceEntryName;
        return cover.SourceEntryName is null ? null : cover.Data;
    }

    /// <inheritdoc />
    public IEnumerable<Finding> Validate(IContainer container, BookMetadata metadata)
    {
        Throw.IfNull(container);
        Throw.IfNull(metadata);

        var findings = new List<Finding>();

        // Read from the bytes on disk, not from the loaded document: the loaded
        // one has already had its namespace declarations corrected, so it has
        // nothing left to report. The point is to say what the user's file says.
        try
        {
            RawPackageDocument raw = ReadRawPackageDocument(container);
            findings.AddRange(NamespaceRepair.Validate(raw.Bytes, raw.EntryName));
        }
        catch (BookFormatException)
        {
            // container.xml is unreadable, which is its own finding once the rule
            // engine lands. Nothing to say about namespaces in the meantime.
        }

        // The remaining rules land with the rule engine, which will drive them
        // rather than this method.
        return findings;
    }

    /// <summary>
    /// Resolves the cover image through both declaration conventions.
    /// </summary>
    /// <remarks>
    /// EPUB 2 names the cover with <c>&lt;meta name="cover" content="id"&gt;</c>
    /// and EPUB 3 with <c>properties="cover-image"</c> on the manifest item.
    /// Files carry one, the other, or both, and disagreement between them is
    /// what rule EPUB-W032 reports. The EPUB 3 form is preferred when both are
    /// present and point at different items, since it is the one modern readers
    /// honour.
    /// </remarks>
    private static void ReadCover(IContainer container, OpfDocument opf, BookMetadata metadata)
    {
        ManifestItem? item = opf.Manifest.FirstOrDefault(i => i.IsCoverImage);

        if (item is null)
        {
            string? coverId = opf.Metadata?
                .Elements()
                .Where(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == "cover")
                .Select(e => (string?)e.Attribute("content"))
                .FirstOrDefault(v => v is not null);

            if (coverId is not null)
            {
                item = opf.Manifest.FirstOrDefault(i => string.Equals(i.Id, coverId, StringComparison.Ordinal));
            }
        }

        if (item is null || item.Href.Length == 0)
        {
            return;
        }

        string resolved = ResolveHref(opf.EntryName, item.Href);
        ContainerEntry? entry = FindEntry(container, resolved);

        if (entry is null)
        {
            // A cover declaration pointing at nothing is EPUB-E030's business.
            // Reading is not the place to complain, only to not crash.
            return;
        }

        metadata.Cover = new CoverImage
        {
            Data = ReadAllBytes(container, entry),
            MediaType = item.MediaType ?? GuessMediaType(entry.Name),
            SourceEntryName = entry.Name,
            SourceManifestId = item.Id,
        };
    }

    /// <summary>
    /// Resolves a manifest href against the package document's own directory.
    /// </summary>
    /// <remarks>
    /// Hrefs are URL-encoded, so a file called <c>my cover.jpg</c> appears as
    /// <c>my%20cover.jpg</c> and will not match any entry name until decoded.
    /// Traversal is not resolved here: an href containing <c>..</c> is left as
    /// found so rule GEN-E003 can report it rather than the reader silently
    /// following it out of the archive.
    /// </remarks>
    internal static string ResolveHref(string opfEntryName, string href)
    {
        string decoded = Uri.UnescapeDataString(href);

        int fragment = decoded.IndexOf('#');
        if (fragment >= 0)
        {
            decoded = decoded.Substring(0, fragment);
        }

        int lastSlash = opfEntryName.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return decoded;
        }

        string directory = opfEntryName.Substring(0, lastSlash + 1);
        return decoded.StartsWith('/') ? decoded.TrimStart('/') : directory + decoded;
    }

    private static ContainerEntry? FindEntry(IContainer container, string name)
    {
        foreach (ContainerEntry entry in container.Entries)
        {
            if (entry.Name.Equals(name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        // ZIP entry names are case-sensitive, but Windows-authored archives are
        // frequently inconsistent about it. Accept a case-insensitive match on
        // the second pass so a book still opens; the mismatch is reported
        // separately rather than being made fatal here.
        foreach (ContainerEntry entry in container.Entries)
        {
            if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static byte[] ReadAllBytes(IContainer container, ContainerEntry entry)
    {
        using Stream stream = container.OpenRead(entry);
        using var buffer = new MemoryStream(entry.Length > 0 && entry.Length < int.MaxValue
            ? (int)entry.Length
            : 0);

        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string GuessMediaType(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }
}
