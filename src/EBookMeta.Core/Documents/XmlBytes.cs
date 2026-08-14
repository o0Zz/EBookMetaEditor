namespace EBookMeta.Documents;

/// <summary>
/// Converts XML text back to bytes in the encoding it arrived in.
/// </summary>
internal static class XmlBytes
{
    /// <summary>Encodes document text, restoring the byte order mark if there was one.</summary>
    /// <param name="text">The document text, without a byte order mark.</param>
    /// <param name="encoding">The encoding the document was read as.</param>
    /// <returns>The document's bytes.</returns>
    /// <remarks>
    /// A BOM is preserved when the original had one and not added when it did
    /// not. Both directions matter: removing one is a change to the file that no
    /// edit asked for and that some readers depend on, and adding one to a file
    /// that lacked it is the same mistake in reverse.
    /// </remarks>
    internal static byte[] Encode(string text, XmlEncodingInfo encoding)
    {
        Throw.IfNull(text, nameof(text));
        Throw.IfNull(encoding, nameof(encoding));

        byte[] content = encoding.Encoding.GetBytes(text);

        if (!encoding.HasByteOrderMark)
        {
            return content;
        }

        byte[] preamble = encoding.Encoding.GetPreamble();
        byte[] result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result, 0);
        content.CopyTo(result, preamble.Length);
        return result;
    }
}
