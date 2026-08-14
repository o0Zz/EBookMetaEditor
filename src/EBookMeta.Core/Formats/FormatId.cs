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

/// <summary>Helpers for describing and recognising formats.</summary>
public static class FormatIds
{
    /// <summary>Returns the conventional display name for a format.</summary>
    /// <param name="id">The format.</param>
    /// <returns>A short human-readable name.</returns>
    public static string ToDisplayName(FormatId id) => id switch
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

    /// <summary>
    /// Maps a file extension to the format it claims to be.
    /// </summary>
    /// <param name="path">A file name or path.</param>
    /// <returns>
    /// The claimed format, or <see cref="FormatId.Unknown"/> for an extension
    /// EBookMetaEditor does not handle.
    /// </returns>
    public static FormatId FromExtension(string path)
    {
        Throw.IfNull(path);

        string ext = Path.GetExtension(path).ToLowerInvariant();

        // .fb2.zip needs the compound extension to be distinguished from a
        // plain zip, so check the stem too.
        if (ext == ".zip" &&
            Path.GetExtension(Path.GetFileNameWithoutExtension(path)).Equals(".fb2", StringComparison.OrdinalIgnoreCase))
        {
            return FormatId.Fb2Zip;
        }

        return ext switch
        {
            ".epub" => FormatId.Epub,
            ".cbz" => FormatId.Cbz,
            ".cbr" => FormatId.Cbr,
            ".cb7" => FormatId.Cb7,
            ".cbt" => FormatId.Cbt,
            ".mobi" or ".prc" => FormatId.Mobi,
            ".azw" or ".azw3" => FormatId.Azw3,
            ".fb2" => FormatId.Fb2,
            ".pdf" => FormatId.Pdf,
            _ => FormatId.Unknown,
        };
    }

    /// <summary>
    /// Whether two formats are close enough that an extension naming one and
    /// content matching the other is not worth reporting.
    /// </summary>
    /// <param name="claimed">The format the extension claims.</param>
    /// <param name="actual">The format the content indicates.</param>
    /// <returns><see langword="true"/> when the pairing is unremarkable.</returns>
    public static bool IsAcceptableSubstitute(FormatId claimed, FormatId actual) =>
        claimed == actual ||
        (claimed is FormatId.Mobi or FormatId.Azw3 && actual is FormatId.Mobi or FormatId.Azw3);
}
