namespace EBookMeta.Formats;

/// <summary>
/// A file format EBookMetaEditor recognises.
/// </summary>
public enum FormatId
{
    /// <summary>Not recognised.</summary>
    Unknown = 0,

    /// <summary>EPUB 2 or 3. ZIP + OPF package document.</summary>
    Epub,

    /// <summary>Comic archive, ZIP. <c>ComicInfo.xml</c>.</summary>
    Cbz,

    /// <summary>Comic archive, RAR. Read-only by necessity.</summary>
    Cbr,

    /// <summary>Comic archive, 7z.</summary>
    Cb7,

    /// <summary>Comic archive, TAR.</summary>
    Cbt,

    /// <summary>MOBI or PRC. PalmDB + EXTH records.</summary>
    Mobi,

    /// <summary>AZW or AZW3 (KF8). PalmDB + EXTH records, possibly two sets.</summary>
    Azw3,

    /// <summary>FictionBook 2, uncompressed XML.</summary>
    Fb2,

    /// <summary>FictionBook 2 inside a ZIP.</summary>
    Fb2Zip,

    /// <summary>PDF. Info dictionary + XMP.</summary>
    Pdf,

    /// <summary>
    /// A ZIP that is none of the above — recognised as an archive, but not as
    /// anything EBookMetaEditor edits.
    /// </summary>
    UnknownZip,
}

/// <summary>
/// The physical container a format is stored in, independent of the metadata
/// document inside it.
/// </summary>
public enum ContainerKind
{
    /// <summary>Not recognised.</summary>
    Unknown = 0,

    /// <summary>A single unarchived file.</summary>
    Raw,

    /// <summary>ZIP.</summary>
    Zip,

    /// <summary>RAR, versions 4 and 5. Readable, never writable.</summary>
    Rar,

    /// <summary>7z.</summary>
    SevenZip,

    /// <summary>TAR.</summary>
    Tar,

    /// <summary>PalmDB, the record container behind MOBI and AZW.</summary>
    PalmDb,
}

/// <summary>Describes a <see cref="FormatId"/>.</summary>
public static class FormatIdExtensions
{
    /// <summary>The conventional display name for a format.</summary>
    /// <param name="id">The format.</param>
    /// <returns>A short human-readable name.</returns>
    public static string DisplayName(this FormatId id) => id switch
    {
        FormatId.Epub => "EPUB",
        FormatId.Cbz => "CBZ",
        FormatId.Cbr => "CBR",
        FormatId.Cb7 => "CB7",
        FormatId.Cbt => "CBT",
        FormatId.Mobi => "MOBI",
        FormatId.Azw3 => "AZW3",
        FormatId.Fb2 => "FB2",
        FormatId.Fb2Zip => "FB2.ZIP",
        FormatId.Pdf => "PDF",
        FormatId.UnknownZip => "ZIP",
        _ => "unknown",
    };
}
