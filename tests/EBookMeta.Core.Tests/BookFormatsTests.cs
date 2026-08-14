using System.Text;
using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;
using EBookMeta.Tests.Builders;
using Xunit;

namespace EBookMeta.Tests;

/// <summary>
/// Covers the format registry: detection decides what a file is, the registry
/// decides who opens it, and the two answers stay separate.
/// </summary>
public sealed class BookFormatsTests
{
    [Fact]
    public void Epub_is_registered_out_of_the_box()
    {
        IBookFormat format = Assert.IsType<EpubFormat>(BookFormats.For(FormatId.Epub));

        Assert.Equal(FormatId.Epub, format.Id);
        Assert.True(BookFormats.IsSupported(FormatId.Epub));
        Assert.NotEmpty(BookFormats.All);
    }

    [Theory]
    [InlineData(FormatId.Cbr)]
    [InlineData(FormatId.Mobi)]
    [InlineData(FormatId.Pdf)]
    [InlineData(FormatId.Unknown)]
    public void Recognised_but_unsupported_formats_are_not_registered(FormatId format)
    {
        // Recognising a format and supporting it are different things, and the
        // registry is where that difference is expressed.
        Assert.Null(BookFormats.For(format));
        Assert.False(BookFormats.IsSupported(format));
    }

    [Fact]
    public void Resolve_picks_the_implementation_for_the_detected_format()
    {
        using var temp = new TempDir();
        string path = new EpubBuilder().WriteTo(temp.File("valid.epub"));

        IBookFormat? format = BookFormats.Resolve(path, out DetectedFormat detected);

        Assert.NotNull(format);
        Assert.Equal(FormatId.Epub, detected.Format);
        Assert.Equal(FormatId.Epub, format!.Id);
    }

    [Fact]
    public void An_unsupported_file_still_reports_what_it_is()
    {
        using var temp = new TempDir();
        string path = temp.File("rar-disguised-as-cbz.cbz");
        File.WriteAllBytes(
            path,
            [.. Encoding.ASCII.GetBytes("Rar!\x1a\x07\x00"), .. new byte[64]]);

        // The case that makes detection worth keeping out of the formats: nothing
        // can open this file, but the user still gets told what it really is.
        IBookFormat? format = BookFormats.Resolve(path, out DetectedFormat detected);

        Assert.Null(format);
        Assert.Equal(ContainerKind.Rar, detected.Container);
        Assert.False(detected.ExtensionAgrees);
    }

    [Fact]
    public void Registering_replaces_the_implementation_for_a_format()
    {
        Assert.Throws<ArgumentNullException>(() => BookFormats.Register(null!));

        IBookFormat original = BookFormats.For(FormatId.Epub)!;

        try
        {
            BookFormats.Register(new FakeFormat(FormatId.Epub));
            Assert.IsType<FakeFormat>(BookFormats.For(FormatId.Epub));
        }
        finally
        {
            // The registry is process-wide static state, so put it back or every
            // later test in the run inherits the fake.
            BookFormats.Register(original);
        }

        Assert.IsType<EpubFormat>(BookFormats.For(FormatId.Epub));
    }

    private sealed class FakeFormat(FormatId id) : IBookFormat
    {
        public FormatId Id { get; } = id;

        public FormatCapabilities Capabilities => new() { Format = Id, ReadableFields = MetadataField.None };

        public BookMetadata Read(
            IContainer container,
            ReadOptions? options = null,
            ICollection<Finding>? findings = null) => new();

        public void Write(
            IContainer container,
            BookMetadata metadata,
            string targetPath,
            ICollection<Finding>? findings = null) =>
            throw new NotSupportedException();
    }
}
