using System.Text;

namespace EBookMeta.Documents;

/// <summary>What the bytes of an XML document say about their own encoding.</summary>
public sealed record XmlEncodingInfo
{
    /// <summary>The encoding the document should actually be decoded with.</summary>
    public required Encoding Encoding { get; init; }

    /// <summary>
    /// The encoding named in the XML declaration, verbatim and with its
    /// original casing — <c>UTF-8</c>, <c>utf-8</c>, <c>windows-1252</c>.
    /// <see langword="null"/> when the declaration named none.
    /// </summary>
    public string? DeclaredName { get; init; }

    /// <summary>The byte order mark found, if any.</summary>
    public int ByteOrderMarkLength { get; init; }

    /// <summary>Whether the document begins with a byte order mark.</summary>
    public bool HasByteOrderMark => ByteOrderMarkLength > 0;

    /// <summary>
    /// Whether the bytes actually decode cleanly as the declared encoding.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> drives rule EPUB-E050, and the case it catches is
    /// common: a file that declares <c>UTF-8</c> but contains Windows-1252
    /// bytes, so an accented character renders as a replacement glyph in some
    /// readers and throws in stricter ones.
    /// </remarks>
    public bool DeclarationMatchesBytes { get; init; } = true;

    /// <summary>
    /// What the disagreement is, in words suitable for a finding.
    /// <see langword="null"/> when there is none.
    /// </summary>
    public string? Mismatch { get; init; }
}

/// <summary>
/// Determines an XML document's encoding from its bytes.
/// </summary>
/// <remarks>
/// BOM, then declaration, then UTF-8 — the order the XML spec requires.
/// Deliberately not delegated to <c>XDocument</c>, which hides the distinction
/// between "declared UTF-8 and is UTF-8" and "declared UTF-8, is really
/// Windows-1252, and the parser substituted replacement characters". Telling the
/// user which one they have is the point of rule EPUB-E050.
/// </remarks>
public static class XmlEncodingDetector
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];
    private static ReadOnlySpan<byte> Utf32LeBom => [0xFF, 0xFE, 0x00, 0x00];
    private static ReadOnlySpan<byte> Utf32BeBom => [0x00, 0x00, 0xFE, 0xFF];
    private static ReadOnlySpan<byte> Utf16LeBom => [0xFF, 0xFE];
    private static ReadOnlySpan<byte> Utf16BeBom => [0xFE, 0xFF];

    /// <summary>Detects the encoding of an XML document.</summary>
    /// <param name="bytes">The complete document bytes.</param>
    /// <returns>What was found, including any disagreement worth reporting.</returns>
    public static XmlEncodingInfo Detect(ReadOnlySpan<byte> bytes)
    {
        (Encoding? bomEncoding, int bomLength) = DetectByteOrderMark(bytes);
        string? declaredName = ReadDeclaredEncoding(bytes.Slice(bomLength), bomEncoding);

        // A BOM outranks the declaration: it is unambiguous, and a document
        // whose declaration contradicts it is broken in a way worth reporting
        // rather than quietly resolving.
        if (bomEncoding is not null)
        {
            bool agrees = declaredName is null || EncodingsAgree(bomEncoding, declaredName);

            return new XmlEncodingInfo
            {
                Encoding = bomEncoding,
                DeclaredName = declaredName,
                ByteOrderMarkLength = bomLength,
                DeclarationMatchesBytes = agrees,
                Mismatch = agrees
                    ? null
                    : $"the byte order mark indicates {bomEncoding.WebName} but the declaration says '{declaredName}'",
            };
        }

        if (declaredName is not null)
        {
            Encoding? declared = TryGetEncoding(declaredName);

            if (declared is null)
            {
                // An unknown label is not a reason to refuse the file; UTF-8 is
                // the spec's default and overwhelmingly the right guess.
                return new XmlEncodingInfo
                {
                    Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    DeclaredName = declaredName,
                    DeclarationMatchesBytes = false,
                    Mismatch = $"declared encoding '{declaredName}' is not one this build recognises",
                };
            }

            bool decodes = DecodesCleanly(bytes, declared);

            return new XmlEncodingInfo
            {
                Encoding = declared,
                DeclaredName = declaredName,
                DeclarationMatchesBytes = decodes,
                Mismatch = decodes
                    ? null
                    : $"the bytes are not valid {declared.WebName} despite the declaration saying so",
            };
        }

        // No BOM, no declaration: the spec says UTF-8. Verify rather than
        // assume, because a bare Windows-1252 file is common in older EPUBs.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        bool validUtf8 = DecodesCleanly(bytes, utf8);

        return new XmlEncodingInfo
        {
            Encoding = utf8,
            DeclaredName = null,
            DeclarationMatchesBytes = validUtf8,
            Mismatch = validUtf8
                ? null
                : "no encoding is declared and the bytes are not valid UTF-8",
        };
    }

    /// <summary>Decodes a document to text using the detected encoding, dropping any BOM.</summary>
    /// <param name="bytes">The complete document bytes.</param>
    /// <param name="info">The result of <see cref="Detect"/>.</param>
    /// <returns>The decoded text.</returns>
    public static string Decode(ReadOnlySpan<byte> bytes, XmlEncodingInfo info)
    {
        Throw.IfNull(info);
        return info.Encoding.GetString(bytes.Slice(info.ByteOrderMarkLength));
    }

    /// <summary>Encodes document text back to bytes in the encoding it arrived in.</summary>
    /// <param name="text">The document text, without a byte order mark.</param>
    /// <param name="info">The result of <see cref="Detect"/>.</param>
    /// <returns>The document's bytes.</returns>
    /// <remarks>
    /// The exact inverse of <see cref="Decode"/>, and here beside it so the pair
    /// cannot drift. A BOM is restored when the original had one and not added
    /// when it did not: removing one is a change to the file that no edit asked
    /// for and that some readers depend on, and adding one to a file that lacked
    /// it is the same mistake in reverse.
    /// </remarks>
    public static byte[] Encode(string text, XmlEncodingInfo info)
    {
        Throw.IfNull(text);
        Throw.IfNull(info);

        byte[] content = info.Encoding.GetBytes(text);

        if (!info.HasByteOrderMark)
        {
            return content;
        }

        byte[] preamble = info.Encoding.GetPreamble();
        byte[] result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result, 0);
        content.CopyTo(result, preamble.Length);
        return result;
    }

    private static (Encoding?, int) DetectByteOrderMark(ReadOnlySpan<byte> bytes)
    {
        // UTF-32 LE must be tested before UTF-16 LE: both start FF FE, and
        // testing in the other order would misidentify every UTF-32 LE file.
        if (bytes.StartsWith(Utf32LeBom))
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        }

        if (bytes.StartsWith(Utf32BeBom))
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        }

        if (bytes.StartsWith(Utf8Bom))
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        }

        if (bytes.StartsWith(Utf16LeBom))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        }

        if (bytes.StartsWith(Utf16BeBom))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        }

        return (null, 0);
    }

    /// <summary>
    /// Extracts the <c>encoding</c> pseudo-attribute from the XML declaration.
    /// </summary>
    /// <remarks>
    /// The declaration is guaranteed to be ASCII-compatible in its own
    /// character repertoire, so it can be read without knowing the encoding
    /// yet — which is the whole point, since the declaration is how you find
    /// out. For BOM-less UTF-16 the bytes interleave with nulls, detected here
    /// by looking at the shape of the opening angle bracket.
    /// </remarks>
    private static string? ReadDeclaredEncoding(ReadOnlySpan<byte> bytes, Encoding? bomEncoding)
    {
        if (bytes.Length < 6)
        {
            return null;
        }

        Encoding prologEncoding = bomEncoding ?? DetectPrologEncoding(bytes);

        int probeLength = Math.Min(bytes.Length, 256);
        string prolog;
        try
        {
            prolog = prologEncoding.GetString(bytes.Slice(0, probeLength));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        int start = prolog.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        int end = prolog.IndexOf("?>", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        // Plain string work rather than spans: the prolog is at most 256 bytes,
        // so slicing allocations are irrelevant here, and the span overloads
        // that take a StringComparison do not exist on this target.
        string declaration = prolog.Substring(start, end - start);
        int encodingAt = declaration.IndexOf("encoding", StringComparison.Ordinal);
        if (encodingAt < 0)
        {
            return null;
        }

        string rest = declaration.Substring(encodingAt + "encoding".Length);
        int equals = rest.IndexOf('=');
        if (equals < 0)
        {
            return null;
        }

        rest = rest.Substring(equals + 1).TrimStart();
        if (rest.Length == 0 || (rest[0] != '"' && rest[0] != '\''))
        {
            return null;
        }

        char quote = rest[0];
        rest = rest.Substring(1);
        int closing = rest.IndexOf(quote);

        return closing < 0 ? null : rest.Substring(0, closing).Trim();
    }

    private static Encoding DetectPrologEncoding(ReadOnlySpan<byte> bytes)
    {
        // '<' is 0x3C. In BOM-less UTF-16 it appears as 3C 00 or 00 3C.
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0x3C && bytes[1] == 0x00)
            {
                return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            }

            if (bytes[0] == 0x00 && bytes[1] == 0x3C)
            {
                return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
            }
        }

        // Latin-1 rather than UTF-8: it never throws on any byte sequence, and
        // the declaration's own characters are ASCII, where the two agree.
        return Encodings.Latin1;
    }

    private static bool DecodesCleanly(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        // Latin-1 and friends map every byte to some character, so there is no
        // such thing as invalid input and the check is vacuously true.
        if (encoding.CodePage is 28591 or 1252 or 20127)
        {
            return true;
        }

        try
        {
            Encoding strict = Encoding.GetEncoding(
                encoding.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);

            _ = strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool EncodingsAgree(Encoding actual, string declaredName)
    {
        Encoding? declared = TryGetEncoding(declaredName);
        return declared is not null && declared.CodePage == actual.CodePage;
    }

    private static Encoding? TryGetEncoding(string name)
    {
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
