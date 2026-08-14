using EBookMeta.Formats;

namespace EBookMeta;


/// <summary>
/// Thrown when a file's structure cannot be understood well enough to continue:
/// a truncated container, an unreadable central directory, a PDB header whose
/// record offsets do not agree with the file length.
/// </summary>
/// <remarks>
/// This is for structural failure, not for editorial problems. Anything a user
/// could reasonably be told about and asked to fix — a missing
/// <c>dc:title</c>, a dangling spine reference, a wrong page count — is a
/// <see cref="Finding"/>, not an exception. Core never writes to the
/// console; it throws these and lets the CLI or UI decide how to present them.
/// </remarks>
public class BookFormatException : Exception
{
    /// <summary>Initialises a new instance with a default message.</summary>
    public BookFormatException()
        : base("The file's format could not be understood.")
    {
    }

    /// <summary>Initialises a new instance with the specified message.</summary>
    /// <param name="message">A description of the structural problem.</param>
    public BookFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with a message and inner exception.</summary>
    /// <param name="message">A description of the structural problem.</param>
    /// <param name="innerException">The underlying failure.</param>
    public BookFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initialises a new instance describing a problem within a named entry or
    /// document inside the file.
    /// </summary>
    /// <param name="message">A description of the structural problem.</param>
    /// <param name="path">
    /// The file, or the entry within the container, that could not be read.
    /// </param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public BookFormatException(string message, string? path, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }

    /// <summary>
    /// The file, or entry within the container, the problem was found in.
    /// <see langword="null"/> when the problem is not attributable to one.
    /// </summary>
    public string? Path { get; }
}

/// <summary>
/// Thrown when a file is recognised but this build cannot edit it.
/// </summary>
/// <remarks>
/// Distinct from a plain <see cref="BookFormatException"/> because the file is not
/// damaged and there is nothing to repair — it is simply a format with no handler,
/// most often a RAR archive wearing a <c>.cbz</c> extension. Naming it precisely is
/// the useful answer, so <see cref="Detected"/> carries the sniffer's verdict and
/// the UI can say what the file actually is instead of "unsupported file".
/// </remarks>
public class UnsupportedFormatException : BookFormatException
{
    /// <summary>Initialises a new instance for a detected but unhandled format.</summary>
    /// <param name="detected">What the content turned out to be.</param>
    /// <param name="path">The file that could not be opened.</param>
    public UnsupportedFormatException(DetectedFormat detected, string? path)
        : base(
            $"'{path}' is {FormatIds.ToDisplayName(detected.Format)}, "
            + "which this build cannot edit.",
            path)
    {
        Detected = detected;
    }

    /// <summary>What the content turned out to be.</summary>
    public DetectedFormat Detected { get; }
}

/// <summary>
/// Thrown when an operation fails for reasons outside the file's own structure:
/// the path does not exist, the file is locked by a reader application, the
/// volume is full, or an atomic replace could not complete.
/// </summary>
/// <remarks>
/// Kept distinct from <see cref="BookFormatException"/> because the two call
/// for different responses. A format problem may be repairable and is worth
/// reporting to the user in terms of their file; an I/O problem means try again,
/// close the other application, or free some space.
/// </remarks>
public class BookIoException : Exception
{
    /// <summary>Initialises a new instance with a default message.</summary>
    public BookIoException()
        : base("The file could not be read or written.")
    {
    }

    /// <summary>Initialises a new instance with the specified message.</summary>
    /// <param name="message">A description of the failure.</param>
    public BookIoException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with a message and inner exception.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="innerException">The underlying failure.</param>
    public BookIoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initialises a new instance describing a failure against a specific path.
    /// </summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="path">The path the operation was attempted against.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public BookIoException(string message, string? path, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }

    /// <summary>
    /// The path the failed operation was attempted against, when known.
    /// </summary>
    public string? Path { get; }
}
