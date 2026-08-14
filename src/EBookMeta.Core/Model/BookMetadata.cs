namespace EBookMeta.Model;

/// <summary>
/// The format-neutral metadata EBookMetaEditor reads, shows and writes — the common
/// 80% that every supported format can express something close to.
/// </summary>
/// <remarks>
/// <para>
/// Mutable by design. This is the object an editor binds to and the user types
/// into, so <c>with</c>-expression immutability would buy nothing and cost
/// clarity. The leaf types it holds — <see cref="Creator"/>,
/// <see cref="Identifier"/>, <see cref="SeriesInfo"/> — are records, because
/// those are values that get replaced rather than edited in place.
/// </para>
/// <para>
/// Adding a field here means updating every <c>IFormatHandler</c>'s
/// <c>FormatCapabilities</c>. That friction is intentional: the UI disables
/// inputs a format cannot store, so a field with no capability declaration
/// would be a box the user can type into whose contents are silently discarded
/// on save.
/// </para>
/// </remarks>
public sealed class BookMetadata
{
    /// <summary>The work's title.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// The sort form of the title — "Hobbit, The". <see langword="null"/> when
    /// the source gave none; do not synthesise one by stripping articles, since
    /// the correct article set is language-dependent.
    /// </summary>
    public string? SortTitle { get; set; }

    /// <summary>
    /// Everyone credited, in source order, primary creators and secondary
    /// contributors alike. Order is preserved because it is meaningful — the
    /// first <c>dc:creator</c> is conventionally the one a reader sees.
    /// </summary>
    public IList<Creator> Creators { get; } = [];

    /// <summary>The series this book belongs to, if any.</summary>
    public SeriesInfo? Series { get; set; }

    /// <summary>The blurb or synopsis. May contain markup in some formats.</summary>
    public string? Description { get; set; }

    /// <summary>The publisher's name.</summary>
    public string? Publisher { get; set; }

    /// <summary>The publication date.</summary>
    public BookDate? PublicationDate { get; set; }

    /// <summary>
    /// The date the work was created, where the source distinguishes it from
    /// publication.
    /// </summary>
    public BookDate? CreationDate { get; set; }

    /// <summary>
    /// The last-modified date — EPUB 3's <c>dcterms:modified</c>.
    /// </summary>
    public BookDate? ModificationDate { get; set; }

    /// <summary>
    /// The language, as a BCP 47 tag such as <c>en</c>, <c>fr-CA</c> or
    /// <c>zh-Hant</c>. Stored as the source wrote it; rule EPUB-W014 warns when
    /// it is not plausibly well-formed rather than correcting it.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>Subjects, genres, keywords and tags, in source order.</summary>
    public IList<string> Subjects { get; } = [];

    /// <summary>Scheme-qualified identifiers, in source order.</summary>
    public IList<Identifier> Identifiers { get; } = [];

    /// <summary>The rights or copyright statement.</summary>
    public string? Rights { get; set; }

    /// <summary>The cover image, as bytes plus a media type.</summary>
    public CoverImage? Cover { get; set; }

    /// <summary>
    /// Metadata found in the source that does not map onto any field above.
    /// </summary>
    /// <remarks>
    /// See <see cref="UnmappedField"/> for the important distinction: for XML
    /// formats this is informational and preservation happens by leaving the
    /// tree alone, while for record-based binary formats these bytes are the
    /// only surviving copy and must be written back.
    /// </remarks>
    public IList<UnmappedField> UnmappedFields { get; } = [];

    /// <summary>
    /// The primary creators, in source order — the subset of
    /// <see cref="Creators"/> that are not secondary contributors.
    /// </summary>
    public IEnumerable<Creator> PrimaryCreators =>
        Creators.Where(c => c.Kind == CreatorKind.Creator);

    /// <summary>
    /// The unique identifier, when the source designated one.
    /// </summary>
    public Identifier? UniqueIdentifier =>
        Identifiers.FirstOrDefault(i => i.IsUnique);

    /// <summary>Returns the title and lead creator, for diagnostics.</summary>
    public override string ToString()
    {
        string title = Title ?? "(untitled)";
        Creator? lead = PrimaryCreators.FirstOrDefault() ?? Creators.FirstOrDefault();
        return lead is null ? title : $"{title} — {lead.Name}";
    }
}
