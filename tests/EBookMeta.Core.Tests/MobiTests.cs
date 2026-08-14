using System.Text;
using EBookMeta.Containers;
using EBookMeta.Xml;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// The MOBI family: a PalmDB of numbered records with an EXTH block in the first.
/// </summary>
public sealed class MobiTests
{
    private static void Write(
        string source, string target, Action<BookMetadata> edit, ICollection<Finding>? findings = null)
    {
        using PalmDbContainer container = PalmDbContainer.Open(source);
        var format = new MobiFormat();
        BookMetadata metadata = format.Read(container, ReadOptions.WithoutCover);
        edit(metadata);
        format.Write(container, metadata, target, findings);
    }

    private static BookMetadata Read(string path, ReadOptions? options = null)
    {
        using PalmDbContainer container = PalmDbContainer.Open(path);
        return new MobiFormat().Read(container, options ?? ReadOptions.WithoutCover);
    }

    private static List<Finding> FindingsOf(string path, ReadOptions? options = null)
    {
        var findings = new List<Finding>();
        using PalmDbContainer container = PalmDbContainer.Open(path);
        new MobiFormat().Read(container, options ?? ReadOptions.WithoutCover, findings);
        return findings;
    }

    /// <summary>Hard invariant 6, for MOBI.</summary>
    [Fact]
    public void Saving_without_editing_is_byte_identical()
    {
        using var temp = new TempDir();
        string source = MobiBuilder.Typical().WriteTo(temp.File("book.mobi"));
        string target = temp.File("saved.mobi");

        Write(source, target, _ => { });

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    [Fact]
    public void Reads_every_mapped_field()
    {
        using var temp = new TempDir();
        string path = MobiBuilder.Typical().WriteTo(temp.File("book.mobi"));

        BookMetadata metadata = Read(path);

        Assert.Equal("The Doll's House", metadata.Title);
        Assert.Equal("DC Comics", metadata.Publisher);
        Assert.Equal("A short summary.", metadata.Description);
        Assert.Equal("Copyright DC Comics", metadata.Rights);
        Assert.Equal("en", metadata.Language);
        Assert.Equal("1989-03-07", metadata.PublicationDate?.Raw);
        Assert.Equal("Neil Gaiman", Assert.Single(metadata.PrimaryCreators).Name);
        Assert.Equal(["Fantasy", "Horror"], metadata.Subjects);
        Assert.Contains(metadata.Identifiers, i => i.Scheme == "ISBN");
        Assert.Contains(metadata.Identifiers, i => i.Scheme == "MOBI-ASIN");
    }

    /// <summary>
    /// The EXTH records this build has no field for are the ones most at risk, and
    /// they are the only copy in the file.
    /// </summary>
    [Fact]
    public void Unmapped_exth_records_survive_a_write()
    {
        using var temp = new TempDir();
        string source = MobiBuilder.Typical().WriteTo(temp.File("book.mobi"));
        string target = temp.File("saved.mobi");

        BookMetadata before = Read(source);
        Assert.Contains(before.UnmappedFields, f => f.Key == "208");
        Assert.Contains(before.UnmappedFields, f => f.Key == "204");

        Write(source, target, m => m.Title = "Season of Mists");

        BookMetadata after = Read(target);

        UnmappedField watermark = Assert.Single(after.UnmappedFields, f => f.Key == "208");
        Assert.Equal("watermark-payload", watermark.Text);

        UnmappedField software = Assert.Single(after.UnmappedFields, f => f.Key == "204");
        Assert.Equal("201", software.Text);
    }

    [Fact]
    public void Writes_every_writable_field()
    {
        using var temp = new TempDir();
        string source = MobiBuilder.Typical().WriteTo(temp.File("book.mobi"));
        string target = temp.File("saved.mobi");

        Write(source, target, m =>
        {
            m.Title = "Season of Mists";
            m.Publisher = "Vertigo";
            m.Description = "A different summary.";
            m.Language = "fr";
            m.Rights = "Public domain";
            m.Creators.Clear();
            m.Creators.Add(new Creator { Name = "Jill Thompson" });
            m.Subjects.Clear();
            m.Subjects.Add("Comics");
        });

        BookMetadata after = Read(target);

        Assert.Equal("Season of Mists", after.Title);
        Assert.Equal("Vertigo", after.Publisher);
        Assert.Equal("A different summary.", after.Description);
        Assert.Equal("fr", after.Language);
        Assert.Equal("Public domain", after.Rights);
        Assert.Equal("Jill Thompson", Assert.Single(after.PrimaryCreators).Name);
        Assert.Equal(["Comics"], after.Subjects);
    }

    /// <summary>
    /// The record table is the only thing that says where a record starts, so
    /// resizing record 0 has to move every offset after it.
    /// </summary>
    [Fact]
    public void Resizing_the_header_record_moves_every_later_record()
    {
        using var temp = new TempDir();
        string source = MobiBuilder.Typical().WriteTo(temp.File("book.mobi"));
        string target = temp.File("saved.mobi");

        // A much longer title, so record 0 certainly grows.
        Write(source, target, m => m.Title = new string('x', 500));

        using PalmDbContainer saved = PalmDbContainer.Open(target);

        // The header record and one text record: the count must not change.
        Assert.Equal(2, saved.Entries.Count);

        // The text record must still read back as itself rather than as whatever
        // bytes happen to sit at a stale offset.
        using Stream text = saved.OpenRead(saved.Entries[1]);
        using var reader = new StreamReader(text);

        Assert.Equal("The book's text.", reader.ReadToEnd());
        Assert.Equal(new string('x', 500), new MobiFormat().Read(saved, ReadOptions.WithoutCover).Title);
    }

    /// <summary>
    /// A longer title first, then a shorter one, so the offsets have to move both
    /// ways and the file must not accumulate slack.
    /// </summary>
    [Fact]
    public void Shrinking_the_header_record_works_too()
    {
        using var temp = new TempDir();
        string source = MobiBuilder.Typical().WriteTo(temp.File("book.mobi"));
        string grown = temp.File("grown.mobi");
        string shrunk = temp.File("shrunk.mobi");

        Write(source, grown, m => m.Title = new string('x', 500));
        Write(grown, shrunk, m => m.Title = "The Doll's House");

        // Back to the original title through two rewrites: the bytes should match
        // the original, because nothing else was touched.
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(shrunk));
    }

    /// <summary>
    /// EXTH 503 overrides the header's name field where a file has one, and both
    /// have to be written or the two disagree.
    /// </summary>
    [Fact]
    public void An_updated_title_record_is_preferred_and_kept_in_step()
    {
        using var temp = new TempDir();

        string source = MobiBuilder.Typical()
            .WithFullName("Stale Header Title")
            .WithExth(503, "The Doll's House")
            .WriteTo(temp.File("book.mobi"));

        Assert.Equal("The Doll's House", Read(source).Title);

        string target = temp.File("saved.mobi");
        Write(source, target, m => m.Title = "Season of Mists");

        using PalmDbContainer container = PalmDbContainer.Open(target);
        MobiDocument header = MobiDocument.Parse(
            ReadRecord(container, 0), container.Entries[0].Name);

        Assert.Equal("Season of Mists", header.ReadMetadata().Title);

        // And the header's own field, which readers fall back to, moved with it.
        Assert.Contains("Season of Mists", TextOf(ReadRecord(container, 0)), StringComparison.Ordinal);
        Assert.DoesNotContain("Stale Header Title", TextOf(ReadRecord(container, 0)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A file without EXTH 503 does not gain one: the header field is what every
    /// reader falls back to, and adding a record changes what the file claims
    /// beyond what was asked.
    /// </summary>
    [Fact]
    public void A_file_without_an_updated_title_record_does_not_gain_one()
    {
        using var temp = new TempDir();
        string source = MobiBuilder.Typical().WriteTo(temp.File("book.mobi"));
        string target = temp.File("saved.mobi");

        Write(source, target, m => m.Title = "Season of Mists");

        using PalmDbContainer container = PalmDbContainer.Open(target);
        BookMetadata metadata = new MobiFormat().Read(container, ReadOptions.WithoutCover);

        Assert.Equal("Season of Mists", metadata.Title);
        Assert.DoesNotContain(metadata.UnmappedFields, f => f.Key == "503");
    }

    /// <summary>
    /// An AZW3 from kindlegen holds two books in one database, and readers prefer
    /// the second.
    /// </summary>
    [Fact]
    public void A_joint_mobi_and_kf8_file_is_read_from_the_kf8_half()
    {
        using var temp = new TempDir();

        string path = MobiBuilder.Typical()
            .WithFullName("The Old Mobi Title")
            .WithKf8Part(new MobiBuilder()
                .WithFullName("The KF8 Title")
                .WithExth(100, "Neil Gaiman")
                .WithExth(524, "en"))
            .WriteTo(temp.File("book.azw3"));

        Assert.Equal("The KF8 Title", Read(path).Title);
        Assert.Contains(FindingsOf(path), f => f.RuleId == "MOBI-W020");
    }

    /// <summary>
    /// Hard invariant 6 for a joint file, where there are two headers to leave
    /// alone rather than one.
    /// </summary>
    [Fact]
    public void Saving_a_joint_file_without_editing_is_byte_identical()
    {
        using var temp = new TempDir();

        string source = MobiBuilder.Typical()
            .WithFullName("The Old Mobi Title")
            .WithKf8Part(new MobiBuilder()
                .WithFullName("The Old Mobi Title")
                .WithExth(100, "Neil Gaiman")
                .WithExth(524, "en"))
            .WriteTo(temp.File("book.azw3"));

        string target = temp.File("saved.azw3");
        Write(source, target, _ => { });

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    /// <summary>
    /// A save propagates the fields the user edited, and only those.
    /// </summary>
    /// <remarks>
    /// The two halves of a joint file often disagree about more than the title:
    /// kindlegen fills the KF8 header sparsely and leaves the MOBI 6 one rich.
    /// Writing the metadata the user was shown into both would delete every field
    /// the older half has and the newer one lacks — on a save that may not have
    /// edited anything at all.
    /// </remarks>
    [Fact]
    public void Saving_a_joint_file_does_not_overwrite_one_half_with_the_other()
    {
        using var temp = new TempDir();

        // The MOBI 6 half is the rich one; the KF8 half has a title, an author and
        // a language and nothing else.
        string source = MobiBuilder.Typical()
            .WithFullName("The Old Mobi Title")
            .WithKf8Part(new MobiBuilder()
                .WithFullName("The KF8 Title")
                .WithExth(100, "Neil Gaiman")
                .WithExth(524, "en"))
            .WriteTo(temp.File("book.azw3"));

        string untouched = temp.File("untouched.azw3");
        Write(source, untouched, _ => { });

        // Nothing was edited, so nothing may change — in either half.
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(untouched));

        string edited = temp.File("edited.azw3");
        Write(source, edited, m => m.Title = "Season of Mists");

        using PalmDbContainer container = PalmDbContainer.Open(edited);

        MobiDocument first = MobiDocument.Parse(ReadRecord(container, 0), "record0");
        BookMetadata mobi6 = first.ReadMetadata();

        // The title followed the edit into the old half...
        Assert.Equal("Season of Mists", mobi6.Title);

        // ...and everything the old half carried that the KF8 half never had is
        // still there.
        Assert.Equal("DC Comics", mobi6.Publisher);
        Assert.Equal("A short summary.", mobi6.Description);
        Assert.Equal("Copyright DC Comics", mobi6.Rights);
        Assert.Equal(["Fantasy", "Horror"], mobi6.Subjects);
        Assert.Contains(mobi6.UnmappedFields, f => f.Key == "208");

        int boundary = first.Kf8BoundaryRecord ?? -1;
        MobiDocument kf8 = MobiDocument.Parse(ReadRecord(container, boundary), "kf8");

        Assert.Equal("Season of Mists", kf8.ReadMetadata().Title);

        // The KF8 half did not acquire the old half's publisher either: the edit
        // was a title, so a title is all that moved.
        Assert.Null(kf8.ReadMetadata().Publisher);
    }

    /// <summary>
    /// Both halves are written, or the file says two different things about
    /// itself and each reader believes a different one.
    /// </summary>
    [Fact]
    public void Saving_a_joint_file_updates_both_headers()
    {
        using var temp = new TempDir();

        string source = MobiBuilder.Typical()
            .WithFullName("The Old Mobi Title")
            .WithKf8Part(new MobiBuilder()
                .WithFullName("The KF8 Title")
                .WithExth(100, "Neil Gaiman")
                .WithExth(524, "en"))
            .WriteTo(temp.File("book.azw3"));

        string target = temp.File("saved.azw3");
        var findings = new List<Finding>();

        Write(source, target, m => m.Title = "Season of Mists", findings);

        Assert.Contains(findings, f => f.RuleId == "MOBI-W030");

        using PalmDbContainer container = PalmDbContainer.Open(target);

        // Both header records now carry the new title, and the boundary record
        // still points at a real header.
        MobiDocument first = MobiDocument.Parse(ReadRecord(container, 0), "record0");
        Assert.Equal("Season of Mists", first.ReadMetadata().Title);

        int boundary = first.Kf8BoundaryRecord ?? -1;
        Assert.InRange(boundary, 1, container.Entries.Count - 1);

        MobiDocument kf8 = MobiDocument.Parse(ReadRecord(container, boundary), "record" + boundary);
        Assert.Equal("Season of Mists", kf8.ReadMetadata().Title);
    }

    /// <summary>DRM is out of scope, and a DRM'd file is refused rather than mangled.</summary>
    [Fact]
    public void A_drm_protected_book_is_refused()
    {
        using var temp = new TempDir();

        string path = MobiBuilder.Typical()
            .WithDrm()
            .WriteTo(temp.File("book.azw"));

        var findings = new List<Finding>();
        using PalmDbContainer container = PalmDbContainer.Open(path);

        Assert.Throws<BookFormatException>(
            () => new MobiFormat().Read(container, ReadOptions.WithoutCover, findings));

        Assert.Contains(findings, f => f.RuleId == "MOBI-F002");
    }

    /// <summary>
    /// A PalmDB that is not a book at all — the container reads, the format does
    /// not pretend to.
    /// </summary>
    [Fact]
    public void A_palmdb_without_a_mobi_header_is_refused()
    {
        using var temp = new TempDir();

        string path = MobiBuilder.Typical()
            .WithTags("DATA", "TEST")
            .WriteTo(temp.File("book.prc"));

        // Blank the MOBI identifier so the record is no longer a header. The
        // record's offset is read from the table rather than assumed, so this
        // keeps working if the fixture gains a record.
        byte[] bytes = File.ReadAllBytes(path);
        int record0 = (bytes[78] << 24) | (bytes[79] << 16) | (bytes[80] << 8) | bytes[81];
        bytes[record0 + 16] = (byte)'X';
        File.WriteAllBytes(path, bytes);

        var findings = new List<Finding>();
        using PalmDbContainer container = PalmDbContainer.Open(path);

        Assert.Throws<BookFormatException>(
            () => new MobiFormat().Read(container, ReadOptions.WithoutCover, findings));

        Assert.Contains(findings, f => f.RuleId == "MOBI-F001");
    }

    [Fact]
    public void Reads_the_cover_from_the_record_it_points_at()
    {
        using var temp = new TempDir();

        string path = MobiBuilder.Typical()
            .WithCover(PngBuilder.OnePixel)
            .WriteTo(temp.File("book.mobi"));

        BookMetadata metadata = Read(path, ReadOptions.Default);

        Assert.NotNull(metadata.Cover);
        Assert.Equal("image/png", metadata.Cover!.MediaType);
        Assert.Equal(PngBuilder.OnePixel, metadata.Cover.Data);
    }

    [Fact]
    public void A_windows_1252_book_round_trips_in_its_own_encoding()
    {
        using var temp = new TempDir();

        string source = new MobiBuilder()
            .WithWindows1252()
            .WithExth(100, "Émile Zola")
            .WithExth(524, "fr")
            .WithFullName("L'Assommoir")
            .WithTextRecord("Le texte.")
            .WriteTo(temp.File("book.mobi"));

        Assert.Equal("Émile Zola", Assert.Single(Read(source).PrimaryCreators).Name);

        string target = temp.File("saved.mobi");
        Write(source, target, _ => { });
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));

        string edited = temp.File("edited.mobi");
        Write(source, edited, m =>
        {
            m.Creators.Clear();
            m.Creators.Add(new Creator { Name = "Gustave Flaubert" });
        });

        Assert.Equal("Gustave Flaubert", Assert.Single(Read(edited).PrimaryCreators).Name);
    }

    /// <summary>
    /// An older, shorter MOBI header has no field at the offsets the modern one
    /// uses, and must not be read past its own length.
    /// </summary>
    [Fact]
    public void A_short_mobi_header_is_read_without_running_off_the_end()
    {
        using var temp = new TempDir();

        string path = new MobiBuilder()
            .WithMobiHeaderLength(0x18)
            .WithFullName(null)
            .WithTextRecord("The book's text.")
            .WriteTo(temp.File("book.prc"));

        BookMetadata metadata = Read(path);

        Assert.Null(metadata.Title);
        Assert.Contains(FindingsOf(path), f => f.RuleId == "MOBI-E010");
    }

    [Theory]
    [InlineData(100, "MOBI-W011")]
    [InlineData(524, "MOBI-W012")]
    public void A_missing_field_is_reported(int removedExth, string rule)
    {
        using var temp = new TempDir();

        var builder = new MobiBuilder().WithFullName("A Title").WithTextRecord("text");

        if (removedExth != 100)
        {
            builder.WithExth(100, "Neil Gaiman");
        }

        if (removedExth != 524)
        {
            builder.WithExth(524, "en");
        }

        string path = builder.WriteTo(temp.File("book.mobi"));

        Assert.Contains(FindingsOf(path), f => f.RuleId == rule);
    }

    [Fact]
    public void Detection_and_registration_are_wired_up()
    {
        using var temp = new TempDir();
        string path = MobiBuilder.Typical().WriteTo(temp.File("book.mobi"));

        DetectedFormat detected = BookFormats.Identify(path);

        Assert.Equal(FormatId.Mobi, detected.Format);
        Assert.Equal(ContainerKind.PalmDb, detected.Container);

        Assert.NotNull(BookFormats.For(FormatId.Mobi));
        Assert.NotNull(BookFormats.For(FormatId.Azw3));
        Assert.True(BookContainers.IsSupported(ContainerKind.PalmDb));
    }

    /// <summary>The whole path a user takes: open, edit, save, reopen.</summary>
    [Fact]
    public void A_book_round_trips_through_load_and_save()
    {
        using var temp = new TempDir();
        string path = MobiBuilder.Typical().WriteTo(temp.File("book.mobi"));
        byte[] before = File.ReadAllBytes(path);

        Book book = Book.Load(path);

        Assert.Equal(FormatId.Mobi, book.Detected.Format);
        Assert.Equal("The Doll's House", book.Metadata.Title);
        Assert.True(book.CanSave);

        book.Save(keepBackup: false);
        Assert.Equal(before, File.ReadAllBytes(path));

        Book reopened = Book.Load(path);
        reopened.Metadata.Title = "Season of Mists";
        reopened.Save(keepBackup: false);

        Assert.Equal("Season of Mists", Book.Load(path).Metadata.Title);
    }

    private static byte[] ReadRecord(IContainer container, int index)
    {
        using Stream stream = container.OpenRead(container.Entries[index]);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string TextOf(byte[] bytes) =>
        new UTF8Encoding(false).GetString(bytes);
}
