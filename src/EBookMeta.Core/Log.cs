using System.Globalization;
using System.Text;

namespace EBookMeta;

/// <summary>How much the reader of a log line is expected to care.</summary>
public enum LogLevel
{
    /// <summary>Detail useful when something has gone wrong and not before.</summary>
    Debug = 0,

    /// <summary>Normal progress: a file opened, a file saved.</summary>
    Info = 1,

    /// <summary>Something was odd but was handled — including anything repaired.</summary>
    Warning = 2,

    /// <summary>Something failed.</summary>
    Error = 3,
}

/// <summary>One line in the log.</summary>
/// <param name="Time">When it happened, local time.</param>
/// <param name="Level">How much to care.</param>
/// <param name="Message">What happened.</param>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    /// <summary>Formats the entry as one fixed-width line.</summary>
    /// <returns><c>HH:mm:ss.fff  LEVEL  message</c>.</returns>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0:HH:mm:ss.fff}  {1,-7}  {2}",
            Time,
            Level.ToString().ToUpperInvariant(),
            Message);
}

/// <summary>
/// The session log: what the application did, in order.
/// </summary>
public static class Log
{
    /// <summary>
    /// The most entries kept in memory. A session is short, so this only exists
    /// so that a runaway loop cannot exhaust memory before anyone notices.
    /// </summary>
    public const int Capacity = 5000;

    private static readonly object Gate = new();
    private static readonly List<LogEntry> Lines = [];
    private static string? _filePath;
    private static int _flushed;
    private static bool _fileFailed;

    /// <summary>Raised for each entry, on the thread that logged it.</summary>
    public static event Action<LogEntry>? Written;

    /// <summary>
    /// Where to write the log when something goes wrong, or <see langword="null"/>
    /// to keep it in memory only.
    /// </summary>
    public static string? FilePath
    {
        get => _filePath;
        set
        {
            lock (Gate)
            {
                _filePath = value;
                _flushed = 0;
                _fileFailed = false;
                FileWritten = false;
            }
        }
    }

    /// <summary>
    /// Whether to write every entry to <see cref="FilePath"/> rather than waiting
    /// for a warning.
    /// </summary>
    public static bool AlwaysWriteToFile { get; set; }

    /// <summary>Whether anything has been written to <see cref="FilePath"/> this session.</summary>
    public static bool FileWritten { get; private set; }

    /// <summary>The entries so far, oldest first.</summary>
    public static IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (Gate)
            {
                return Lines.ToArray();
            }
        }
    }

    /// <summary>Logs detail that only matters when diagnosing a problem.</summary>
    /// <param name="message">What happened.</param>
    public static void Debug(string message) => Write(LogLevel.Debug, message);

    /// <summary>Logs normal progress.</summary>
    /// <param name="message">What happened.</param>
    public static void Info(string message) => Write(LogLevel.Info, message);

    /// <summary>Logs something odd that was handled, including a repair.</summary>
    /// <param name="message">What happened.</param>
    public static void Warning(string message) => Write(LogLevel.Warning, message);

    /// <summary>Logs a failure.</summary>
    /// <param name="message">What happened.</param>
    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>Logs a failure and the exception behind it.</summary>
    /// <param name="message">What was being attempted.</param>
    /// <param name="exception">Why it failed.</param>
    public static void Error(string message, Exception exception)
    {
        Throw.IfNull(exception);
        Write(LogLevel.Error, $"{message} — {exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>Logs a validation finding at a level matching its severity.</summary>
    /// <param name="finding">The finding to record.</param>
    public static void Finding(Finding finding)
    {
        Throw.IfNull(finding);

        LogLevel level = finding.Severity switch
        {
            Severity.Fatal or Severity.Error => LogLevel.Error,
            Severity.Warning => LogLevel.Warning,
            _ => LogLevel.Info,
        };

        Write(level, finding.ToString());
    }

    /// <summary>Renders an optional field's value for a log line.</summary>
    /// <param name="value">The value, which may be absent.</param>
    /// <returns>The value in quotes, or <c>(none)</c>.</returns>
    /// <remarks>
    /// Quoted rather than bare so an empty string and a missing one read
    /// differently in the log, which is the distinction most metadata bug reports
    /// turn on. Shared by all four formats so their "Read ..." lines stay
    /// comparable to each other.
    /// </remarks>
    public static string Describe(string? value) =>
        value is null ? "(none)" : $"\"{value}\"";

    /// <summary>Formats the whole session as text.</summary>
    /// <returns>One line per entry, separated by <see cref="Environment.NewLine"/>.</returns>
    public static string Format()
    {
        var builder = new StringBuilder(4096);

        foreach (LogEntry entry in Entries)
        {
            builder.Append(entry).Append(Environment.NewLine);
        }

        return builder.ToString();
    }

    /// <summary>Discards every entry. For tests, and for a "clear" command.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Lines.Clear();
            _flushed = 0;
            _fileFailed = false;
            FileWritten = false;
        }
    }

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message ?? string.Empty);

        lock (Gate)
        {
            if (Lines.Count >= Capacity)
            {
                // Drop the oldest half rather than one at a time, so trimming is
                // not an O(n) memmove on every subsequent line.
                Lines.RemoveRange(0, Capacity / 2);
                _flushed = Math.Max(0, _flushed - (Capacity / 2));
            }

            Lines.Add(entry);

            // A warning starts the file; from then on everything goes to it. The
            // lines after a problem are as much of the story as the problem, so
            // flushing only on warnings would drop the half that explains it.
            if (_filePath is not null && (AlwaysWriteToFile || FileWritten || level >= LogLevel.Warning))
            {
                AppendPending();
            }
        }

        // Outside the lock: a handler that logs would otherwise deadlock.
        Written?.Invoke(entry);
    }

    /// <summary>
    /// Appends everything not yet written. Caller holds <see cref="Gate"/>.
    /// </summary>
    private static string? AppendPending()
    {
        if (_filePath is null || _fileFailed || _flushed >= Lines.Count)
        {
            return null;
        }

        try
        {
            var text = new StringBuilder();
            for (int i = _flushed; i < Lines.Count; i++)
            {
                text.Append(Lines[i]).Append(Environment.NewLine);
            }

            File.AppendAllText(_filePath, text.ToString(), new UTF8Encoding(false));
            _flushed = Lines.Count;
            FileWritten = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A log that cannot be written must not become the problem it was
            // meant to report. Give up on the file for the rest of the session
            // and keep logging to memory.
            _fileFailed = true;
            return $"The log could not be written to {_filePath}: {ex.Message}";
        }
    }
}
