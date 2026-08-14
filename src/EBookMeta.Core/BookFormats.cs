using EBookMeta.Formats;

namespace EBookMeta;

/// <summary>
/// The registry of format handlers: what this build can read and write.
/// </summary>
/// <remarks>
/// <para>
/// Loading a book is two questions, and this keeps them apart because they have
/// different answers. <b>What is this file?</b> is answered by
/// <see cref="FormatDetector"/> from the bytes. <b>Who handles it?</b> is answered
/// here, by looking the answer up. Adding a format is then one
/// <see cref="Register"/> call and one <see cref="IFormatHandler"/> — nothing in
/// the UI or the open path changes.
/// </para>
/// <para>
/// Detection deliberately does <i>not</i> live in the handlers. The app has to
/// name formats it has no handler for at all — a <c>.cbz</c> that is really a RAR
/// archive is the common case, and telling the user that is the point — so
/// something has to recognise RAR without being able to open it. Asking each
/// registered handler "is this yours?" could never produce that answer.
/// </para>
/// <para>
/// Register at startup, before any file is opened. There is no locking: this is a
/// single-instance desktop app whose registrations are all made from one place.
/// </para>
/// </remarks>
public static class BookFormats
{
    private static readonly Dictionary<FormatId, IFormatHandler> Handlers = [];

    static BookFormats()
    {
        // The formats this build supports, and the whole list.
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
