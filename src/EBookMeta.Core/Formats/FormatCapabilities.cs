namespace EBookMeta.Formats;

/// <summary>
/// The fields of <see cref="Model.BookMetadata"/>, as a flag set, so a format
/// can declare which of them it is able to store.
/// </summary>
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
/// What a format can do with a file: which fields it can read, which it
/// can write, and whether it can write at all.
/// </summary>
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

    /// <summary>Returns whether every field in <paramref name="fields"/> can be written.</summary>
    /// <param name="fields">The fields an edit would touch.</param>
    /// <returns><see langword="true"/> if all of them are writable.</returns>
    public bool CanWriteAll(MetadataField fields) => (WritableFields & fields) == fields;

    /// <summary>Returns the subset of <paramref name="fields"/> this format would discard.</summary>
    /// <param name="fields">The fields an edit would touch.</param>
    /// <returns>The fields that cannot be stored.</returns>
    public MetadataField UnsupportedIn(MetadataField fields) => fields & ~WritableFields;
}
