namespace EBookMeta.Containers;

/// <summary>
/// A read-only window onto part of another stream.
/// </summary>
/// <remarks>
/// What a container hands back for an entry stored uncompressed at a known
/// offset: TAR and PalmDB records, and the whole of a raw file. Reading one is a
/// bounded read rather than a decompression, so there is nothing to wrap it in
/// but bounds.
/// <para>
/// Seeks to its own position on every read, so a container that hands out several
/// of these over one shared stream does not have them lose each other's place.
/// <see cref="Dispose"/> closes the underlying stream only when this window was
/// given ownership of it — a caller's <c>using</c> must never close the
/// container's handle.
/// </para>
/// </remarks>
internal sealed class SectionStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _ownsStream;
    private long _position;

    /// <summary>Creates a window over part of a stream.</summary>
    /// <param name="inner">The stream to read from; must be seekable.</param>
    /// <param name="start">Where the window begins in <paramref name="inner"/>.</param>
    /// <param name="length">How many bytes the window covers.</param>
    /// <param name="ownsStream">
    /// <see langword="true"/> to dispose <paramref name="inner"/> with this window.
    /// </param>
    internal SectionStream(Stream inner, long start, long length, bool ownsStream)
    {
        _inner = inner;
        _start = start;
        _length = length;
        _ownsStream = ownsStream;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? 0 : Math.Min(value, _length);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        _inner.Position = _start + _position;

        int read = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
        _position += read;

        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };

        return _position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsStream)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
