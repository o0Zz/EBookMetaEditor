using EBookMeta.Formats;

namespace EBookMeta;

/// <summary>
/// The registry of <see cref="IBookFormat"/> implementations: what this build
/// can read and write.
/// </summary>
/// <seealso cref="BookContainers" />
public static class BookFormats
{
    private static readonly Dictionary<FormatId, IBookFormat> Registered = [];

    static BookFormats()
    {
        Register(new EpubFormat());
        Register(new CbzFormat(FormatId.Cbz));

        // The same implementation twice more: a CBT is a CBZ in a TAR and a CBR a CBZ
        // in a RAR. Only the container differs, and only CBR's save.
        Register(new CbzFormat(FormatId.Cbt));
        Register(new CbzFormat(FormatId.Cbr));

        // And again for FictionBook, which is one document either bare or zipped.
        Register(new Fb2Format(FormatId.Fb2));
        Register(new Fb2Format(FormatId.Fb2Zip));

        // And once more for the MOBI family: MOBI and PRC on one id, AZW and AZW3
        // on the other, all of them a PalmDB with an EXTH block.
        Register(new MobiFormat(FormatId.Mobi));
        Register(new MobiFormat(FormatId.Azw3));
    }

    /// <summary>Every registered format.</summary>
    public static IReadOnlyCollection<IBookFormat> All => Registered.Values;

    /// <summary>Registers a format, replacing any previous one for its id.</summary>
    /// <param name="format">The format to register.</param>
    public static void Register(IBookFormat format)
    {
        Throw.IfNull(format);
        Registered[format.Id] = format;
    }

    /// <summary>The implementation for a format, if this build has one.</summary>
    /// <param name="format">The format to look up.</param>
    /// <returns>
    /// The implementation, or <see langword="null"/> when the format is
    /// unsupported.
    /// </returns>
    public static IBookFormat? For(FormatId format) =>
        Registered.TryGetValue(format, out IBookFormat? registered) ? registered : null;

    /// <summary>Whether this build can open a format.</summary>
    /// <param name="format">The format to test.</param>
    /// <returns><see langword="true"/> when an implementation is registered for it.</returns>
    public static bool IsSupported(FormatId format) => Registered.ContainsKey(format);

    private static ReadOnlySpan<byte> PdfMagic => "%PDF-"u8;

    /// <summary>
    /// Offers a file to every registered format and returns it open, claimed by
    /// whichever one recognised it most confidently.
    /// </summary>
    /// <param name="path">The file to open.</param>
    /// <param name="detected">What the content turned out to be, always set.</param>
    /// <returns>
    /// The open source, which the caller disposes, or <see langword="null"/> when no
    /// format claimed the file. <paramref name="detected"/> still describes it, so
    /// the caller can say what the file actually is rather than merely refusing it.
    /// </returns>
    /// <exception cref="BookIoException">The file could not be read.</exception>
    public static BookSource? TryOpen(string path, out DetectedFormat detected)
    {
        Throw.IfNullOrEmpty(path);

        BookSource source = BookSource.Open(path);
        FormatId claimed = FromExtension(path);

        try
        {
            // The answers no registered format is there to give: recognised but not
            // openable. Naming one costs a magic-number comparison; supporting it
            // would cost a container and a metadata document.
            FormatId? unsupported = source.ContainerKind switch
            {
                ContainerKind.SevenZip => FormatId.Cb7,
                _ => source.HeadStartsWith(PdfMagic) ? FormatId.Pdf : null,
            };

            if (unsupported is { } id)
            {
                detected = Describe(id, source, claimed, source.ContainerDetail);
                source.Dispose();
                return null;
            }

            FormatClaim? best = null;

            foreach (IBookFormat format in Registered.Values)
            {
                FormatClaim? claim = format.TryOpen(source);

                if (claim is not null && (best is null || claim.Confidence > best.Confidence))
                {
                    best = claim;
                }
            }

            if (best is not null)
            {
                detected = Describe(best.Format, source, claimed, best.Detail);
                Log.Debug($"'{path}' opened as {detected.Format.DisplayName()} ({best.Detail}).");
                return source;
            }

            // A ZIP nothing claimed is still a ZIP, and saying so is more useful
            // than "unknown" — it is the shape a mis-extensioned archive takes.
            detected = source.ContainerKind == ContainerKind.Zip
                ? Describe(FormatId.UnknownZip, source, claimed, "ZIP with no recognised metadata")
                : Describe(FormatId.Unknown, source, claimed, source.ContainerDetail);
        }
        catch
        {
            source.Dispose();
            throw;
        }

        source.Dispose();
        return null;
    }

    /// <summary>Works out what a file is without keeping it open.</summary>
    /// <param name="path">The file to inspect.</param>
    /// <returns>What the content turned out to be, and whether the extension agreed.</returns>
    /// <exception cref="BookIoException">The file could not be read.</exception>
    public static DetectedFormat Identify(string path)
    {
        using BookSource? source = TryOpen(path, out DetectedFormat detected);
        return detected;
    }

    /// <summary>Maps a file extension to the format it claims to be.</summary>
    /// <param name="path">A file name or path.</param>
    /// <returns>
    /// The claimed format, or <see cref="FormatId.Unknown"/> for an extension
    /// EBookMetaEditor does not handle.
    /// </returns>
    public static FormatId FromExtension(string path)
    {
        Throw.IfNull(path);

        string ext = Path.GetExtension(path).ToLowerInvariant();

        // .fb2.zip needs the compound extension to be distinguished from a plain
        // zip, so the stem is checked before the registry is asked — otherwise
        // ".zip" would match nothing and a FictionBook archive would claim nothing.
        if (ext == ".zip" &&
            Path.GetExtension(Path.GetFileNameWithoutExtension(path))
                .Equals(".fb2", StringComparison.OrdinalIgnoreCase))
        {
            return FormatId.Fb2Zip;
        }

        foreach (IBookFormat format in Registered.Values)
        {
            foreach (string candidate in format.Extensions)
            {
                if (ext.Equals(candidate, StringComparison.Ordinal))
                {
                    return format.Id;
                }
            }
        }

        return ext switch
        {
            ".cb7" => FormatId.Cb7,
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

    private static DetectedFormat Describe(
        FormatId format, BookSource source, FormatId claimedByExtension, string? detail) =>
        new()
        {
            Format = format,
            Container = format == FormatId.Unknown ? ContainerKind.Unknown : source.ContainerKind,
            ClaimedByExtension = claimedByExtension,
            Detail = detail,
        };
}

/// <summary>What a file turned out to be, and whether its name agreed.</summary>
public sealed record DetectedFormat
{
    /// <summary>The format the content indicates.</summary>
    public required FormatId Format { get; init; }

    /// <summary>The container the content indicates.</summary>
    public required ContainerKind Container { get; init; }

    /// <summary>
    /// The format the file extension claims, or <see cref="FormatId.Unknown"/>
    /// for an unrecognised extension.
    /// </summary>
    public FormatId ClaimedByExtension { get; init; }

    /// <summary>
    /// A short note on how the decision was reached, for the log — "RAR 5
    /// archive", "first entry is ComicInfo.xml".
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>Whether the extension is consistent with the content.</summary>
    public bool ExtensionAgrees =>
        ClaimedByExtension == FormatId.Unknown ||
        BookFormats.IsAcceptableSubstitute(ClaimedByExtension, Format);

    /// <summary>Returns a short description, for diagnostics.</summary>
    public override string ToString() =>
        Detail is null
            ? Format.DisplayName()
            : $"{Format.DisplayName()} ({Detail})";
}
