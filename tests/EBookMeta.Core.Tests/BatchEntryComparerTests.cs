using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

public sealed class BatchEntryComparerTests
{
    /// <summary>A batch of readable books, one per name, already read.</summary>
    private static BatchSession Loaded(TempDir temp, params string[] names)
    {
        BatchSession session = BatchSession.Create(
            [.. names.Select(name => new EpubBuilder().WriteTo(temp.File(name)))]);

        session.Load();
        return session;
    }

    /// <summary>
    /// The file names in the order the comparer puts them.
    /// </summary>
    /// <remarks>
    /// <see cref="List{T}.Sort(IComparer{T})"/> rather than
    /// <see cref="Enumerable.OrderBy{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey}, IComparer{TKey})"/>:
    /// it is unstable, so a comparer that leaves equal rows undecided shows up here
    /// rather than in a grid.
    /// </remarks>
    private static string[] Order(BatchSession session, IComparer<BatchEntry> comparer)
    {
        List<BatchEntry> entries = [.. session.Entries];
        entries.Sort(comparer);
        return [.. entries.Select(e => e.FileName)];
    }

    private static BatchEntry Entry(BatchSession session, string fileName) =>
        session.Entries.Single(e => e.FileName == fileName);

    [Fact]
    public void File_names_sort_the_way_a_person_reads_them()
    {
        using var temp = new TempDir();
        BatchSession session = Loaded(temp, "book-10.epub", "book-1.epub", "book-2.epub");

        Assert.Equal(
            ["book-1.epub", "book-2.epub", "book-10.epub"],
            Order(session, BatchEntryComparer.ByFileName(descending: false)));

        Assert.Equal(
            ["book-10.epub", "book-2.epub", "book-1.epub"],
            Order(session, BatchEntryComparer.ByFileName(descending: true)));
    }

    [Fact]
    public void A_blank_sorts_last_whichever_way_the_column_points()
    {
        using var temp = new TempDir();
        BatchSession session = Loaded(temp, "a.epub", "b.epub", "c.epub");

        Entry(session, "a.epub").Apply(MetadataField.Publisher, "Vertigo");
        Entry(session, "b.epub").Apply(MetadataField.Publisher, string.Empty);
        Entry(session, "c.epub").Apply(MetadataField.Publisher, "Angoulême");

        // A row that has not been read yet reads blank in every field, so a
        // descending sort that stacked the unread rows on top would hide the
        // answer the click asked for.
        Assert.Equal(
            ["c.epub", "a.epub", "b.epub"],
            Order(session, BatchEntryComparer.ByField(MetadataField.Publisher, descending: false)));

        Assert.Equal(
            ["a.epub", "c.epub", "b.epub"],
            Order(session, BatchEntryComparer.ByField(MetadataField.Publisher, descending: true)));
    }

    [Fact]
    public void An_unread_row_sorts_last_as_well()
    {
        using var temp = new TempDir();

        string read = new EpubBuilder().WriteTo(temp.File("z-read.epub"));
        string pending = new EpubBuilder().WriteTo(temp.File("a-pending.epub"));

        BatchSession session = BatchSession.Create([read]);
        session.Load();

        // Added after the load, so one row has a title and the other has not been
        // read yet — the state the grid is in while a batch is still loading.
        session.Add([pending]);
        Assert.Equal(BatchEntryStatus.Pending, Entry(session, "a-pending.epub").Status);

        string[] expected = ["z-read.epub", "a-pending.epub"];

        Assert.Equal(
            expected, Order(session, BatchEntryComparer.ByField(MetadataField.Title, descending: false)));
        Assert.Equal(
            expected, Order(session, BatchEntryComparer.ByField(MetadataField.Title, descending: true)));
    }

    [Fact]
    public void A_series_index_sorts_as_a_number()
    {
        using var temp = new TempDir();
        BatchSession session = Loaded(temp, "a.epub", "b.epub", "c.epub");

        foreach (BatchEntry entry in session.Entries)
        {
            entry.Apply(MetadataField.Series, "The Sandman");
        }

        Entry(session, "a.epub").Apply(MetadataField.SeriesIndex, "10");
        Entry(session, "b.epub").Apply(MetadataField.SeriesIndex, "2");
        Entry(session, "c.epub").Apply(MetadataField.SeriesIndex, "2.5");

        // Compared as text, 10 comes before 2 and 2.5 before 2.
        Assert.Equal(
            ["b.epub", "c.epub", "a.epub"],
            Order(session, BatchEntryComparer.ByField(MetadataField.SeriesIndex, descending: false)));
    }

    [Fact]
    public void A_date_sorts_chronologically_whatever_the_file_wrote()
    {
        using var temp = new TempDir();
        BatchSession session = Loaded(temp, "a.epub", "b.epub", "c.epub", "d.epub");

        Entry(session, "a.epub").Apply(MetadataField.PublicationDate, "2011");
        Entry(session, "b.epub").Apply(MetadataField.PublicationDate, "2010-05-03");
        Entry(session, "c.epub").Apply(MetadataField.PublicationDate, "2011-02");
        Entry(session, "d.epub").Apply(MetadataField.PublicationDate, string.Empty);

        // A date is stored as the characters the file used, and "2011" sorts after
        // "2011-02" as text.
        Assert.Equal(
            ["b.epub", "a.epub", "c.epub", "d.epub"],
            Order(session, BatchEntryComparer.ByField(MetadataField.PublicationDate, descending: false)));

        Assert.Equal(
            ["c.epub", "a.epub", "b.epub", "d.epub"],
            Order(session, BatchEntryComparer.ByField(MetadataField.PublicationDate, descending: true)));
    }

    [Fact]
    public void Equal_values_keep_reading_order_in_both_directions()
    {
        using var temp = new TempDir();
        BatchSession session = Loaded(temp, "book-10.epub", "book-1.epub", "book-2.epub");

        foreach (BatchEntry entry in session.Entries)
        {
            entry.Apply(MetadataField.Publisher, "Vertigo");
        }

        // Sorting by a column that repeats groups the rows without shuffling them
        // inside a group.
        string[] expected = ["book-1.epub", "book-2.epub", "book-10.epub"];

        Assert.Equal(
            expected, Order(session, BatchEntryComparer.ByField(MetadataField.Publisher, descending: false)));
        Assert.Equal(
            expected, Order(session, BatchEntryComparer.ByField(MetadataField.Publisher, descending: true)));
    }

    [Fact]
    public void Any_text_a_row_can_be_asked_for_can_order_it()
    {
        using var temp = new TempDir();
        BatchSession session = Loaded(temp, "a.epub", "b.epub");

        // What the grid's Status and Format columns use: they show text the entry
        // knows rather than a field of the metadata.
        Assert.Equal(
            ["b.epub", "a.epub"],
            Order(session, BatchEntryComparer.ByText(e => e.FileName == "b.epub" ? "1" : "2", descending: false)));

        Assert.Throws<ArgumentNullException>(() => BatchEntryComparer.ByText(null!, descending: false));
    }
}
