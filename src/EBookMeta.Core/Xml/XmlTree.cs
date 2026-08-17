using System.Xml.Linq;

namespace EBookMeta.Xml;

/// <summary>Tree edits shared by the XML formats' writers.</summary>
internal static class XmlTree
{
    /// <summary>Removes an element together with the whitespace that preceded it.</summary>
    /// <param name="element">The element to remove.</param>
    internal static void RemoveWithWhitespace(XElement element)
    {
        // Take the whitespace too, otherwise deleting a field leaves a blank line
        // behind and the diff shows two changes.
        if (element.PreviousNode is XText text && text.Value.Trim().Length == 0)
        {
            text.Remove();
        }

        element.Remove();
    }

    /// <summary>The whitespace an element separates its children with.</summary>
    /// <param name="parent">The element to inspect.</param>
    /// <param name="fallback">What to use when it has no indented children yet.</param>
    /// <returns>The indentation, newline included.</returns>
    internal static string DetectIndent(XElement parent, string fallback) =>
        // The whitespace before the first child is the document's own style, whatever
        // it happens to be. Guessing two spaces instead would make a generated element
        // visibly foreign in a file that uses tabs.
        parent.FirstNode is XText text && text.Value.Contains('\n') ? text.Value : fallback;

    /// <summary>Whether two field values are the same text.</summary>
    /// <param name="a">One value.</param>
    /// <param name="b">The other.</param>
    /// <returns><see langword="true"/> when they match.</returns>
    internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
}
