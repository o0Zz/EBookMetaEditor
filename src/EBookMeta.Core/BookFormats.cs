using EBookMeta.Formats;

namespace EBookMeta;

/// <summary>
/// The registry of <see cref="IBookFormat"/> implementations: what this build
/// can read and write.
/// </summary>
/// <remarks>
/// Adding a format is one implementation plus one <see cref="Register"/> call.
/// Nothing in the UI or the open path changes, because both ask the registry
/// which format to use and never name one. Detection stays outside on purpose —
/// the app must be able to say "this .cbz is really a RAR archive", which is an
/// answer no registered format could give.
/// </remarks>
/// <seealso cref="BookContainers" />
public static class BookFormats
{
    private static readonly Dictionary<FormatId, IBookFormat> Registered = [];

    static BookFormats()
    {
        Register(new EpubFormat());
        Register(new CbzFormat());
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

    /// <summary>
    /// Works out what a file is and which format should open it.
    /// </summary>
    /// <param name="path">The file to inspect.</param>
    /// <param name="detected">What the content turned out to be, always set.</param>
    /// <returns>
    /// The implementation, or <see langword="null"/> when the format was
    /// recognised but is not supported. <paramref name="detected"/> still
    /// describes it, so the caller can say what the file actually is rather than
    /// merely refusing it.
    /// </returns>
    /// <exception cref="BookIoException">The file could not be read.</exception>
    public static IBookFormat? Resolve(string path, out DetectedFormat detected)
    {
        Throw.IfNullOrEmpty(path);

        detected = FormatDetector.Detect(path);
        return For(detected.Format);
    }
}
