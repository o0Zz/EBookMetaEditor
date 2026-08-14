using EBookMeta.Formats;

namespace EBookMeta;

/// <summary>
/// The registry of format handlers: what this build can read and write.
/// </summary>
public static class BookFormats
{
    private static readonly Dictionary<FormatId, IFormatHandler> Handlers = [];

    static BookFormats()
    {
        Register(new EpubHandler());
        Register(new CbzHandler());
    }

    /// <summary>Every registered handler.</summary>
    public static IReadOnlyCollection<IFormatHandler> All => Handlers.Values;

    /// <summary>Registers a handler, replacing any previous one for its format.</summary>
    /// <param name="handler">The handler to register.</param>
    public static void Register(IFormatHandler handler)
    {
        Throw.IfNull(handler);
        Handlers[handler.Id] = handler;
    }

    /// <summary>The handler for a format, if this build has one.</summary>
    /// <param name="format">The format to look up.</param>
    /// <returns>The handler, or <see langword="null"/> when the format is unsupported.</returns>
    public static IFormatHandler? For(FormatId format) =>
        Handlers.TryGetValue(format, out IFormatHandler? handler) ? handler : null;

    /// <summary>Whether this build can open a format.</summary>
    /// <param name="format">The format to test.</param>
    /// <returns><see langword="true"/> when a handler is registered for it.</returns>
    public static bool IsSupported(FormatId format) => Handlers.ContainsKey(format);

    /// <summary>
    /// Works out what a file is and which handler should open it.
    /// </summary>
    /// <param name="path">The file to inspect.</param>
    /// <param name="detected">What the content turned out to be, always set.</param>
    /// <returns>
    /// The handler, or <see langword="null"/> when the format was recognised but
    /// is not supported. <paramref name="detected"/> still describes it, so the
    /// caller can say what the file actually is rather than merely refusing it.
    /// </returns>
    /// <exception cref="BookIoException">The file could not be read.</exception>
    public static IFormatHandler? Resolve(string path, out DetectedFormat detected)
    {
        Throw.IfNullOrEmpty(path);

        detected = FormatDetector.Detect(path);
        return For(detected.Format);
    }
}
