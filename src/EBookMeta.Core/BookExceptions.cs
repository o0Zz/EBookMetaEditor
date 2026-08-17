
namespace EBookMeta;


/// <summary>
/// Thrown when a file's structure cannot be understood well enough to continue:
/// a truncated container, an unreadable central directory, a PDB header whose
/// record offsets do not agree with the file length.
/// </summary>
public class BookFormatException : Exception
{
    /// <summary>Initialises a new instance describing a structural problem.</summary>
    /// <param name="message">
    /// A description of the problem, naming the file or entry it was found in.
    /// </param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public BookFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a file is recognised but this build cannot edit it.</summary>
public class UnsupportedFormatException : BookFormatException
{
    /// <summary>Initialises a new instance for a detected but unhandled format.</summary>
    /// <param name="detected">What the content turned out to be.</param>
    /// <param name="path">The file that could not be opened.</param>
    public UnsupportedFormatException(DetectedFormat detected, string? path)
        : base(
            $"'{path}' is {detected.Format.DisplayName()}, "
            + "which this build cannot edit.")
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
public class BookIoException : Exception
{
    /// <summary>Initialises a new instance describing a failure.</summary>
    /// <param name="message">
    /// A description of the failure, naming the path it was attempted against.
    /// </param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public BookIoException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
