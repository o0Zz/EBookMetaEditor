using System.Xml;

namespace EBookMeta.Xml;

/// <summary>
/// Turns the line and column an XML reader reports into an index into the text
/// it was reading.
/// </summary>
/// <remarks>
/// Both formats that splice edits into original text need this: FB2 to find
/// where <c>&lt;description&gt;</c> starts and ends, EPUB to find where the root
/// element's name ends. A reader gives positions and an edit needs offsets, and
/// nothing in the framework converts between them.
/// </remarks>
internal static class XmlLineIndex
{
    /// <summary>
    /// Indexes where each line of <paramref name="text"/> begins.
    /// </summary>
    /// <param name="text">The text a reader is about to be run over.</param>
    /// <returns>The offset of the first character of each line, in order.</returns>
    public static int[] Starts(string text)
    {
        var starts = new List<int> { 0 };

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
            else if (text[i] == '\r')
            {
                // CRLF counts as one break; a lone CR is one too.
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }

    /// <summary>
    /// Converts a reader's current position into an offset into the text.
    /// </summary>
    /// <param name="lineStarts">The index from <see cref="Starts"/>.</param>
    /// <param name="info">The reader, positioned on the node of interest.</param>
    /// <returns>The offset of that node's first character.</returns>
    public static int Offset(int[] lineStarts, IXmlLineInfo info)
    {
        int line = Math.Min(Math.Max(info.LineNumber, 1), lineStarts.Length);
        return lineStarts[line - 1] + info.LinePosition - 1;
    }
}
