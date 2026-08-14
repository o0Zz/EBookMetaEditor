namespace EBookMeta.Formats;

/// <summary>
/// The fields of <see cref="Model.BookMetadata"/>, as a flag set, so a format
/// can declare which of them it is able to store.
/// </summary>
/// <remarks>
/// Adding a field to <see cref="Model.BookMetadata"/> means adding a flag here
/// and updating every handler's capabilities. That friction is intentional: the
/// UI disables inputs a format cannot store, and a field with no declaration
/// would be a box the user can type into whose contents get silently discarded.
/// </remarks>
[Flags]
public enum MetadataField
{
    /// <summary>No fields.</summary>
    None = 0,

    /// <summary>Title.</summary>
    Title = 1 << 0,

    /// <summary>Sort title.</summary>
    SortTitle = 1 << 1,

    /// <summary>Creator names.</summary>
    Creators = 1 << 2,

    /// <summary>Per-creator sort names.</summary>
    CreatorSortNames = 1 << 3,

    /// <summary>Per-creator roles.</summary>
    CreatorRoles = 1 << 4,

    /// <summary>Series name.</summary>
    Series = 1 << 5,

    /// <summary>Position within the series.</summary>
    SeriesIndex = 1 << 6,

    /// <summary>Description or synopsis.</summary>
    Description = 1 << 7,

    /// <summary>Publisher.</summary>
    Publisher = 1 << 8,

    /// <summary>Publication date.</summary>
    PublicationDate = 1 << 9,

    /// <summary>Modification date.</summary>
    ModificationDate = 1 << 10,

    /// <summary>Language.</summary>
    Language = 1 << 11,

    /// <summary>Subjects, genres or tags.</summary>
    Subjects = 1 << 12,

    /// <summary>Scheme-qualified identifiers.</summary>
    Identifiers = 1 << 13,

    /// <summary>Rights statement.</summary>
    Rights = 1 << 14,

    /// <summary>Cover image.</summary>
    Cover = 1 << 15,

    /// <summary>Everything.</summary>
    All = (1 << 16) - 1,
}

/// <summary>
/// What a format handler can do with a file: which fields it can read, which it
/// can write, and whether it can write at all.
/// </summary>
/// <remarks>
/// Read by the UI to decide which inputs to enable, and by the CLI to reject an
/// edit that could not be honoured, so that a user never supplies a value that
/// is quietly dropped on save.
/// </remarks>
public sealed record FormatCapabilities
{
    /// <summary>The format these capabilities describe.</summary>
    public required FormatId Format { get; init; }

    /// <summary>Fields this format can be read for.</summary>
    public required MetadataField ReadableFields { get; init; }

    /// <summary>
    /// Fields this format can store on write. <see cref="MetadataField.None"/>
    /// when <see cref="CanWrite"/> is <see langword="false"/>.
    /// </summary>
    public MetadataField WritableFields { get; init; } = MetadataField.None;

    /// <summary>Whether this format can be written at all.</summary>
    public bool CanWrite => WritableFields != MetadataField.None;

    /// <summary>
    /// Why the format cannot be written, in words suitable for showing a user.
    /// <see langword="null"/> when it can be.
    /// </summary>
    /// <remarks>
    /// Exists so the UI can explain rather than just grey out. "Read-only" with
    /// no reason invites the user to assume a missing feature; CBR is read-only
    /// because RAR compression is proprietary and cannot be legally
    /// reimplemented, which is a different thing and worth saying.
    /// </remarks>
    public string? WriteBlockedReason { get; init; }

    /// <summary>
    /// A format this file can be converted to in order to become editable, or
    /// <see langword="null"/> if none.
    /// </summary>
    /// <remarks>
    /// Set for CBR, whose only correct edit path is convert-to-CBZ-then-tag,
    /// surfaced as an explicit user choice by rule GEN-W004. The original is
    /// always kept.
    /// </remarks>
    public FormatId? ConversionTarget { get; init; }

    /// <summary>
    /// Whether the format writes slowly enough to warrant telling the user
    /// before starting — CB7, which recompresses with LZMA.
    /// </summary>
    public bool WriteIsSlow { get; init; }

    /// <summary>Returns whether every field in <paramref name="fields"/> can be written.</summary>
    /// <param name="fields">The fields an edit would touch.</param>
    /// <returns><see langword="true"/> if all of them are writable.</returns>
    public bool CanWriteAll(MetadataField fields) => (WritableFields & fields) == fields;

    /// <summary>Returns the subset of <paramref name="fields"/> this format would discard.</summary>
    /// <param name="fields">The fields an edit would touch.</param>
    /// <returns>The fields that cannot be stored.</returns>
    public MetadataField UnsupportedIn(MetadataField fields) => fields & ~WritableFields;
}
