namespace EBookMeta.App.Utils;

/// <summary>
/// The <c>key = value</c> text format — one pair per line, <c>#</c> for a comment,
/// readable by a translator with Notepad. This app stores its settings and its
/// interface languages in it.
/// </summary>
internal static class KeyValueFile
{
    /// <summary>Reads the lines into a table, skipping blanks and <c>#</c> comments.</summary>
    /// <param name="reader">The text to read; the caller disposes it.</param>
    /// <returns>The table, with values verbatim.</returns>
    internal static Dictionary<string, string> Read(TextReader reader)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            int equals = trimmed.IndexOf('=');

            if (equals > 0)
            {
                table[trimmed.Substring(0, equals).Trim()] = trimmed.Substring(equals + 1).Trim();
            }
        }

        return table;
    }
}
