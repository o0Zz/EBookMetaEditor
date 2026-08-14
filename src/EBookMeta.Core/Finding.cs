namespace EBookMeta;

/// <summary>How badly a finding affects the file.</summary>
/// <remarks>
/// The letter in a rule ID encodes this: <c>EPUB-F001</c> is fatal,
/// <c>EPUB-E010</c> an error, <c>EPUB-W014</c> a warning.
/// </remarks>
public enum Severity
{
    /// <summary>
    /// Informational. Reported, never blocks anything.
    /// </summary>
    Info = 0,

    /// <summary>
    /// Worth telling the user about, but the file is usable and editable — a
    /// missing cover, a language tag that does not look like BCP 47.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// The file is internally inconsistent and some reader will get it wrong —
    /// a spine entry pointing at no manifest item, a page count that disagrees
    /// with the images present. Editing is still allowed.
    /// </summary>
    Error = 2,

    /// <summary>
    /// The file cannot be edited safely: unreadable container, OPF that is not
    /// well-formed XML. Writing is refused until it is repaired, because
    /// rebuilding a file we cannot parse risks destroying content.
    /// </summary>
    Fatal = 3,
}

/// <summary>
/// One validation result: which rule fired, how bad it is, and where.
/// </summary>
/// <remarks>
/// The validator is the core value of this project, so findings are structured
/// data rather than strings. A stable <see cref="RuleId"/> lets a user script
/// against a specific check, lets the test corpus name fixtures after the rule
/// they trigger, and lets a message be reworded without breaking anything.
/// </remarks>
public sealed record Finding
{
    /// <summary>
    /// The stable rule identifier, namespaced by format — <c>GEN-W002</c>,
    /// <c>EPUB-E020</c>, <c>CBZ-E020</c>, <c>MOBI-F001</c>.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>How badly this affects the file.</summary>
    public required Severity Severity { get; init; }

    /// <summary>
    /// What is wrong, in words suitable for showing a user directly. States the
    /// problem, not the fix.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Where the problem is: a container entry name such as
    /// <c>OEBPS/content.opf</c>, or the file itself. <see langword="null"/> when
    /// it is not attributable to one place.
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// One-based line within <see cref="Location"/>, or 0 where the format has
    /// no meaningful line numbers.
    /// </summary>
    public int Line { get; init; }

    /// <summary>
    /// One-based column within <see cref="Location"/>, or 0 where the format has
    /// no meaningful columns.
    /// </summary>
    public int Column { get; init; }

    /// <summary>
    /// Whether a repair exists that could fix this automatically.
    /// </summary>
    /// <remarks>
    /// Distinguishes a problem the tool will correct on open from one it will
    /// only report. A correction is held in memory and reaches the file only
    /// through an ordinary save, so this never means "your file has been
    /// rewritten".
    /// </remarks>
    public bool HasAutofix { get; init; }

    /// <summary>
    /// Extra context for the UI or a JSON consumer — the offending value, the
    /// id that failed to resolve. Not part of <see cref="Message"/>.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>Returns "RULE-ID severity: message (location:line:col)".</summary>
    public override string ToString()
    {
        string where = Location is null
            ? string.Empty
            : Line > 0
                ? $" ({Location}:{Line}:{Column})"
                : $" ({Location})";

        return $"{RuleId} {Severity.ToString().ToLowerInvariant()}: {Message}{where}";
    }
}
