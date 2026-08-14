using EBookMeta.Compat;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// One fixture per EPUB rule, each triggering its rule in isolation.
/// </summary>
/// <remarks>
/// <para>
/// The rules run as part of the load — there is no validate call to make — so every
/// test here is a load, and the findings are what the load reported.
/// </para>
/// <para>
/// Asserted by containment rather than as the whole set, unlike the comic suite. A
/// conformant EPUB 3 declares its cover and series the EPUB 3 way only, which
/// EPUB-W032 and EPUB-W061 legitimately report on every fixture below; demanding an
/// exact set would mean restating those two in twenty tests that are not about them.
/// </para>
/// </remarks>
public sealed class EpubRuleTests
{
    private static List<Finding> Load(string path)
    {
        var findings = new List<Finding>();

        try
        {
            Book.Load(path, ReadOptions.WithoutCover, findings);
        }
        catch (BookFormatException)
        {
        }

        return findings;
    }

    private static Finding Single(string path, string ruleId) =>
        Assert.Single(Load(path), f => f.RuleId == ruleId);

    private static void AssertNoRule(string path, string ruleId) =>
        Assert.DoesNotContain(Load(path), f => f.RuleId == ruleId);

    /// <summary>An OPF with one substitution applied to the EPUB 3 default.</summary>
    private static string Opf(string find, string replace)
    {
        Assert.Contains(find, EpubBuilder.Epub3Opf, StringComparison.Ordinal);
        return EpubBuilder.Epub3Opf.Replace(find, replace);
    }

    private static string Epub(TempDir temp, string name, string opf) =>
        new EpubBuilder().WithOpf(opf).WriteTo(temp.File(name));

    [Fact]
    public void A_valid_book_reports_no_errors()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid.epub"));

        Assert.DoesNotContain(Load(path), f => f.Severity >= Severity.Error);
    }

    // --- required metadata -----------------------------------------------

    [Fact]
    public void Epub_e010_no_unique_identifier_attribute()
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            "broken-epub-e010-no-unique-identifier.epub",
            Opf(@" unique-identifier=""pub-id""", string.Empty));

        Assert.Equal(Severity.Error, Single(path, "EPUB-E010").Severity);

        // E011 is about a reference that does not resolve. There is no reference at
        // all here, so reporting both would be reporting the same defect twice.
        AssertNoRule(path, "EPUB-E011");
    }

    [Fact]
    public void Epub_e011_unique_identifier_points_at_no_dc_identifier()
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            "broken-epub-e011-dangling-identifier.epub",
            Opf(@"unique-identifier=""pub-id""", @"unique-identifier=""nobody-here"""));

        Finding finding = Single(path, "EPUB-E011");

        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("nobody-here", finding.Detail);
    }

    [Fact]
    public void Epub_e012_no_title()
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            "broken-epub-e012-no-title.epub",
            Opf(
                @"<dc:title id=""t1"">The Ocean at the End of the Lane</dc:title>",
                @"<dc:title id=""t1""></dc:title>"));

        Assert.Equal(Severity.Error, Single(path, "EPUB-E012").Severity);
    }

    [Fact]
    public void Epub_e013_no_language()
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            "broken-epub-e013-no-language.epub",
            Opf("<dc:language>en-GB</dc:language>", string.Empty));

        Assert.Equal(Severity.Error, Single(path, "EPUB-E013").Severity);
        AssertNoRule(path, "EPUB-W014");
    }

    [Fact]
    public void Epub_w014_language_that_cannot_be_a_bcp47_tag()
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            "broken-epub-w014-bad-language.epub",
            Opf("<dc:language>en-GB</dc:language>", "<dc:language>English</dc:language>"));

        Finding finding = Single(path, "EPUB-W014");

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("English", finding.Detail);
    }

    /// <summary>
    /// A plausible tag is not reported, however unusual.
    /// </summary>
    /// <remarks>
    /// The rule tests shape, not the subtag registry: warning about a language that
    /// is perfectly legitimate is worse than staying quiet about an odd one.
    /// </remarks>
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hant-TW")]
    [InlineData("gsw")]
    public void Epub_w014_accepts_plausible_tags(string tag)
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            $"language-{tag}.epub",
            Opf("<dc:language>en-GB</dc:language>", $"<dc:language>{tag}</dc:language>"));

        AssertNoRule(path, "EPUB-W014");
    }

    // --- internal references ---------------------------------------------

    [Fact]
    public void Epub_e020_spine_points_at_nothing_in_the_manifest()
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            "broken-epub-e020-dangling-idref.epub",
            Opf(@"<itemref idref=""ch1""/>", @"<itemref idref=""ch99""/>"));

        Finding finding = Single(path, "EPUB-E020");

        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("ch99", finding.Detail);
    }

    [Fact]
    public void Epub_e021_manifest_lists_a_file_that_is_not_there()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithoutCoverImage()
            .WriteTo(temp.File("broken-epub-e021-missing-href.epub"));

        Finding finding = Single(path, "EPUB-E021");

        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("OEBPS/images/cover.png", finding.Detail);
    }

    [Fact]
    public void Epub_e022_two_manifest_items_share_an_id()
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            "broken-epub-e022-duplicate-id.epub",
            Opf(
                @"<item id=""cover-img"" href=""images/cover.png"" media-type=""image/png"" properties=""cover-image""/>",
                @"<item id=""ch1"" href=""images/cover.png"" media-type=""image/png"" properties=""cover-image""/>"));

        Finding finding = Single(path, "EPUB-E022");

        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("ch1", finding.Detail);
    }

    [Fact]
    public void Epub_w023_the_archive_holds_a_file_the_manifest_ignores()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithEntry("OEBPS/text/orphan.xhtml", "<html/>")
            .WriteTo(temp.File("broken-epub-w023-orphan.epub"));

        Finding finding = Single(path, "EPUB-W023");

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("OEBPS/text/orphan.xhtml", finding.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>META-INF</c> is never an orphan.
    /// </summary>
    /// <remarks>
    /// The manifest is not supposed to list it, and neither is <c>mimetype</c> or the
    /// package document itself — so a rule that flagged them would fire on every
    /// conformant book and be ignored within a day.
    /// </remarks>
    [Fact]
    public void Epub_w023_ignores_the_files_no_manifest_lists()
    {
        using var temp = new TempDir();
        AssertNoRule(new EpubBuilder().WriteTo(temp.File("valid.epub")), "EPUB-W023");
    }

    [Fact]
    public void Epub_w060_a_refinement_points_at_nothing()
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            "broken-epub-w060-dangling-refines.epub",
            Opf(@"<meta refines=""#t1"" property=""file-as"">", @"<meta refines=""#gone"" property=""file-as"">"));

        Finding finding = Single(path, "EPUB-W060");

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("file-as", finding.Detail);
    }

    // --- cover and series conventions ------------------------------------

    [Fact]
    public void Epub_e030_cover_metadata_names_a_manifest_item_that_does_not_exist()
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            "broken-epub-e030-dangling-cover.epub",
            Opf(
                @"<meta property=""custom:mood"">wistful</meta>",
                @"<meta name=""cover"" content=""not-a-real-item""/>"));

        Finding finding = Single(path, "EPUB-E030");

        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("not-a-real-item", finding.Detail);
    }

    [Fact]
    public void Epub_w031_no_cover_at_all()
    {
        using var temp = new TempDir();
        string path = Epub(
            temp,
            "broken-epub-w031-no-cover.epub",
            Opf(@" properties=""cover-image""", string.Empty));

        Assert.Equal(Severity.Warning, Single(path, "EPUB-W031").Severity);

        // Nothing declares a cover, so there are not two conventions to disagree.
        AssertNoRule(path, "EPUB-W032");
    }

    /// <summary>
    /// A cover declared only one way is reported, not corrected.
    /// </summary>
    /// <remarks>
    /// Deliberately not an autofix — see
    /// <see cref="An_unedited_save_leaves_the_convention_rules_standing"/> for why.
    /// </remarks>
    [Fact]
    public void Epub_w032_cover_in_only_one_convention()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("epub3-cover-only.epub"));

        Finding finding = Single(path, "EPUB-W032");

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.False(finding.HasAutofix);
    }

    [Fact]
    public void Epub_w061_series_in_only_one_convention()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("epub3-series-only.epub"));

        Finding finding = Single(path, "EPUB-W061");

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.False(finding.HasAutofix);
    }

    /// <summary>
    /// An unedited save does not settle the convention rules, and says so.
    /// </summary>
    /// <remarks>
    /// These two are reported, not corrected, and the distinction is forced by a
    /// genuine conflict between two hard invariants. Invariant 8 wants both
    /// conventions written on every save; invariant 6 wants an unedited save of a
    /// conformant book to be byte-identical. A conformant EPUB 3 book carries only
    /// the EPUB 3 forms, so honouring 8 unconditionally would rewrite metadata lines
    /// in a file the user only opened — which is why the second convention is written
    /// when the field is edited and not before. Asserted rather than left implicit so
    /// that changing the trade-off has to be deliberate.
    /// </remarks>
    [Fact]
    public void An_unedited_save_leaves_the_convention_rules_standing()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("book.epub"));

        Book book = Book.Load(path);
        Assert.Contains(book.LoadFindings, f => f.RuleId is "EPUB-W032" or "EPUB-W061");

        book.Save(keepBackup: false);

        Assert.Contains(Load(path), f => f.RuleId is "EPUB-W032" or "EPUB-W061");
    }

    // --- the mimetype entry ----------------------------------------------

    /// <summary>
    /// Without a <c>mimetype</c> entry there is nothing left to identify an EPUB by.
    /// </summary>
    /// <remarks>
    /// Not a rule but the absence of one, and worth pinning down. A ZIP with no
    /// <c>mimetype</c>, no <c>ComicInfo.xml</c> and no images is indistinguishable
    /// from any other ZIP by content, and detection is by content — so the honest
    /// answer is that this build cannot edit it, not a made-up EPUB verdict.
    /// </remarks>
    [Fact]
    public void A_zip_with_no_mimetype_is_not_recognised_as_an_epub()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithoutMimetype()
            .WriteTo(temp.File("no-mimetype.epub"));

        Assert.Throws<UnsupportedFormatException>(() => Book.Load(path));
    }

    [Fact]
    public void Epub_e040_mimetype_not_first()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithMimetypeNotFirst()
            .WriteTo(temp.File("broken-epub-e040-mimetype-late.epub"));

        Assert.Contains("first", Single(path, "EPUB-E040").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Epub_e040_mimetype_compressed()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithCompressedMimetype()
            .WriteTo(temp.File("broken-mimetype-compressed.epub"));

        Assert.Contains("stored", Single(path, "EPUB-E040").Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A book whose <c>mimetype</c> is wrong is made readable again by saving.
    /// </summary>
    /// <remarks>
    /// All three requirements are the specification's, not a preference, so putting
    /// the entry back is provable rather than a guess. This is the correction with
    /// the most direct payoff: readers reject the file outright until it is made.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Saving_puts_the_mimetype_entry_back(bool compressed)
    {
        using var temp = new TempDir();
        EpubBuilder builder = compressed
            ? new EpubBuilder().WithCompressedMimetype()
            : new EpubBuilder().WithMimetypeNotFirst();

        string path = builder.WriteTo(temp.File($"broken-mimetype-{compressed}.epub"));

        Book book = Book.Load(path);
        book.Save(keepBackup: false);

        Assert.Contains(book.SaveFindings, f => f.RuleId == "EPUB-E040" && f.HasAutofix);

        using ZipContainer saved = ZipContainer.Open(path);
        ContainerEntry first = saved.Entries[0];

        Assert.Equal("mimetype", first.Name);
        Assert.Equal(ZipCompressionMethods.Stored, first.CompressionMethod);

        // And the corrected file needs no correction the second time.
        AssertNoRule(path, "EPUB-E040");
    }

    [Fact]
    public void Epub_e040_mimetype_with_the_wrong_content()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder()
            .WithMimetypeContent("application/epub+zip\n")
            .WriteTo(temp.File("broken-epub-e040-mimetype-newline.epub"));

        // A trailing newline is enough: readers compare the bytes exactly.
        Assert.Contains("exactly", Single(path, "EPUB-E040").Message, StringComparison.Ordinal);
    }

    // --- encoding ---------------------------------------------------------

    [Fact]
    public void Epub_e050_declared_encoding_does_not_match_the_bytes()
    {
        using var temp = new TempDir();
        string declaredUtf8 = EpubBuilder.Epub3Opf.Replace(
            "The Ocean at the End of the Lane", "Café de la Païx");

        string path = new EpubBuilder()
            .WithOpfBytes(Encodings.Latin1.GetBytes(declaredUtf8))
            .WriteTo(temp.File("latin1-declared-utf8.epub"));

        Assert.Equal(Severity.Error, Single(path, "EPUB-E050").Severity);
    }
}
