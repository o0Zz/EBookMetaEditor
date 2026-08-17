namespace EBookMeta.Model;

/// <summary>
/// Whether a contributor is a primary creator of the work or a secondary
/// contributor to it.
/// </summary>
public enum CreatorKind
{
    /// <summary>A primary creator — <c>dc:creator</c> in an OPF.</summary>
    Creator = 0,

    /// <summary>A secondary contributor — <c>dc:contributor</c> in an OPF.</summary>
    Contributor = 1,
}

/// <summary>
/// A person or organisation credited on the work, with the sort form and role
/// as the source expressed them.
/// </summary>
public sealed record Creator
{
    /// <summary>The display name, as it should appear to a reader.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The sort form — "Gaiman, Neil" for "Neil Gaiman". <see langword="null"/>
    /// when the source did not supply one; do not synthesise it on read, since
    /// guessing wrongly on names that do not follow Western given-family order
    /// is worse than leaving it empty.
    /// </summary>
    public string? SortName { get; init; }

    /// <summary>
    /// The MARC relator code this creator's role maps to, such as <c>aut</c>
    /// or <c>ill</c>. <see langword="null"/> when the source gave no role, or
    /// gave one with no sensible relator equivalent.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// The role exactly as the source expressed it — <c>Penciller</c>,
    /// <c>Colorist</c>, or an <c>opf:role</c> attribute's raw value. Preferred
    /// over <see cref="Role"/> when writing back to the originating format.
    /// </summary>
    public string? NativeRole { get; init; }

    /// <summary>Whether this is a primary creator or a secondary contributor.</summary>
    public CreatorKind Kind { get; init; } = CreatorKind.Creator;

    /// <summary>Returns the display name, for diagnostics.</summary>
    public override string ToString() => Name;
}
