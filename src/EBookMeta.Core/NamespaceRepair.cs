using System.Text;
using System.Xml;
using System.Xml.Linq;
using EBookMeta.Documents;

namespace EBookMeta;

/// <summary>
/// What repairing a document's namespace declarations would produce.
/// </summary>
public sealed record NamespaceRepairResult
{
    /// <summary>The document's bytes with the missing declarations added.</summary>
    public required byte[] RepairedBytes { get; init; }

    /// <summary>
    /// Whether the repaired document parses as well-formed, namespace-correct XML.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> means the repair is real but insufficient — the
    /// document has something else wrong with it too. A partial repair is not
    /// used: handing back a document that still will not parse is worse than
    /// reporting the original error.
    /// </remarks>
    public required bool IsComplete { get; init; }

    /// <summary>Prefixes that were declared, in first-use order.</summary>
    public required IReadOnlyList<string> Added { get; init; }

    /// <summary>
    /// Prefixes left alone because no specification says what they mean.
    /// </summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>
    /// Why the repaired document still does not parse, or <see langword="null"/>
    /// when it does.
    /// </summary>
    public string? RemainingError { get; init; }

    /// <summary>Whether anything was changed.</summary>
    public bool HasChanges => Added.Count > 0;
}

/// <summary>
/// Supplies namespace declarations a document uses but never declares.
/// </summary>
/// <remarks>
/// <para>
/// This fixes the single most common reason a package document will not open:
/// <c>'opf' is an undeclared prefix</c>. It happens because <c>opf:file-as</c>,
/// <c>opf:role</c> and <c>opf:scheme</c> are attributes on <c>dc:</c> elements, so
/// an author or tool that copies a <c>&lt;metadata&gt;</c> block carries the
/// attributes across without the <c>xmlns:opf</c> declaration that gave them
/// meaning. The document is otherwise intact — every byte of content is fine and
/// only one declaration is absent.
/// </para>
/// <para>
/// Three decisions hold this together, and each is load-bearing:
/// </para>
/// <para>
/// <b>Diagnose permissively, fix surgically, verify strictly.</b> The strict
/// parser refuses the file outright, so the prefixes are found by reading the
/// markup with namespace processing switched off. The fix is one attribute
/// inserted at one offset — not a reserialisation, because re-emitting the whole
/// tree rewrites every line, and a user who opened a book to correct a typo would
/// save a file in which nothing is where they left it. The result is then parsed
/// strictly to prove it actually loads.
/// </para>
/// <para>
/// <b>Read the markup, never the exception message.</b> Pulling <c>opf</c> out of
/// "'opf' is an undeclared prefix" with a regex works perfectly on an English
/// machine and silently stops matching everywhere else, because the framework
/// localises that text.
/// </para>
/// <para>
/// <b>Report unknown prefixes; never invent one.</b> <see cref="KnownNamespaces"/>
/// is the whole list of prefixes this project will bind on an author's behalf.
/// Binding an unrecognised <c>acme:</c> to a plausible-looking URI would fabricate
/// metadata that was never in the file, and the user would have no reason to
/// doubt it.
/// </para>
/// </remarks>
public static class NamespaceRepair
{
    /// <summary>The rule this repair answers.</summary>
    public const string RuleId = "EPUB-W070";

    /// <summary>
    /// Namespace URIs a missing declaration can be recovered from, by prefix.
    /// </summary>
    /// <remarks>
    /// Every entry is fixed by a published specification, not by convention. The
    /// boundary of this table is the feature's safety property: a prefix that is
    /// not in it is reported, never guessed.
    /// </remarks>
    private static readonly Dictionary<string, string> KnownNamespaces = new(StringComparer.Ordinal)
    {
        ["opf"] = "http://www.idpf.org/2007/opf",
        ["dc"] = "http://purl.org/dc/elements/1.1/",
        ["dcterms"] = "http://purl.org/dc/terms/",
        ["epub"] = "http://www.idpf.org/2007/ops",
        ["xhtml"] = "http://www.w3.org/1999/xhtml",
        ["xsi"] = "http://www.w3.org/2001/XMLSchema-instance",
        ["xlink"] = "http://www.w3.org/1999/xlink",
        ["svg"] = "http://www.w3.org/2000/svg",
        ["ncx"] = "http://www.daisy.org/z3986/2005/ncx/",
        ["ocf"] = "urn:oasis:names:tc:opendocument:xmlns:container",
        ["oebpf"] = "http://openebook.org/namespaces/oeb-package/1.0/",
    };

    /// <summary>Whether a missing declaration for this prefix can be recovered.</summary>
    /// <param name="prefix">The prefix, without the colon.</param>
    /// <returns><see langword="true"/> when the URI is known.</returns>
    public static bool IsKnownPrefix(string prefix) =>
        prefix is not null && KnownNamespaces.ContainsKey(prefix);

    /// <summary>
    /// Repairs a document's missing namespace declarations.
    /// </summary>
    /// <param name="bytes">The document's bytes.</param>
    /// <param name="entryName">The container entry name, for diagnostics.</param>
    /// <returns>
    /// The result, or <see langword="null"/> when every prefix the document uses
    /// is declared and there is nothing to repair.
    /// </returns>
    public static NamespaceRepairResult? Repair(ReadOnlySpan<byte> bytes, string entryName = "content.opf")
    {
        XmlEncodingInfo encoding = XmlEncodingDetector.Detect(bytes);
        string text = XmlEncodingDetector.Decode(bytes, encoding);

        List<Undeclared> undeclared = FindUndeclared(text, out bool reachedEnd, out string? stoppedBecause);

        if (undeclared.Count == 0)
        {
            return null;
        }

        List<string> added = [];
        List<string> skipped = [];
        string repairedText = text;

        foreach (Undeclared use in undeclared)
        {
            if (IsKnownPrefix(use.Prefix))
            {
                added.Add(use.Prefix);
            }
            else
            {
                skipped.Add(use.Prefix);
            }
        }

        if (added.Count > 0 && FindRootTagInsertionPoint(text, out int insertAt))
        {
            // One insertion carrying every recoverable declaration. The root
            // element is where the format expects them and where a single edit
            // covers the whole document, however many prefixes are missing.
            var declarations = new StringBuilder();
            foreach (string prefix in added)
            {
                declarations.Append(" xmlns:").Append(prefix)
                            .Append("=\"").Append(KnownNamespaces[prefix]).Append('"');
            }

            repairedText = text.Insert(insertAt, declarations.ToString());
        }
        else
        {
            added.Clear();
        }

        string? remaining = StrictParseError(repairedText);

        // The scan stopping early means there is more wrong than namespaces, and
        // any prefix after the break was never seen — so completeness cannot be
        // claimed even if what we changed does parse.
        if (remaining is null && !reachedEnd)
        {
            remaining = stoppedBecause;
        }

        return new NamespaceRepairResult
        {
            RepairedBytes = added.Count == 0 ? bytes.ToArray() : XmlBytes.Encode(repairedText, encoding),
            IsComplete = remaining is null && skipped.Count == 0,
            Added = added,
            Skipped = skipped,
            RemainingError = remaining,
        };
    }

    /// <summary>
    /// Reports undeclared prefixes as validation findings.
    /// </summary>
    /// <param name="bytes">The document's bytes.</param>
    /// <param name="entryName">The container entry name, reported as the location.</param>
    /// <returns>One finding per undeclared prefix, empty when there are none.</returns>
    /// <remarks>
    /// <c>HasAutofix</c> distinguishes the prefixes this project will bind from the
    /// ones it will only tell you about.
    /// </remarks>
    public static IEnumerable<Finding> Validate(ReadOnlySpan<byte> bytes, string entryName = "content.opf")
    {
        XmlEncodingInfo encoding = XmlEncodingDetector.Detect(bytes);
        string text = XmlEncodingDetector.Decode(bytes, encoding);

        var findings = new List<Finding>();

        foreach (Undeclared use in FindUndeclared(text, out _, out _))
        {
            bool known = IsKnownPrefix(use.Prefix);

            findings.Add(new Finding
            {
                RuleId = RuleId,
                Severity = Severity.Warning,
                Message = $"Namespace prefix '{use.Prefix}' is used but never declared.",
                Location = entryName,
                Line = use.Line,
                Column = use.Column,
                HasAutofix = known,
                Detail = known
                    ? $"{use.OnName}; can be declared as {KnownNamespaces[use.Prefix]}"
                    : $"{use.OnName}; no known namespace URI for this prefix, so it cannot be repaired automatically",
            });
        }

        return findings;
    }

    /// <summary>One prefix used without a declaration, and where it was first seen.</summary>
    private readonly record struct Undeclared(string Prefix, int Line, int Column, string OnName);

    /// <summary>
    /// Finds prefixes used on an element or attribute name that no
    /// <c>xmlns:</c> declaration binds.
    /// </summary>
    /// <remarks>
    /// <see cref="XmlTextReader"/> is used deliberately, obsolete though it looks:
    /// it is the only reader exposing <c>Namespaces = false</c>, and
    /// <c>XmlReader.Create</c> offers no equivalent. Without that, a document
    /// whose prefixes do not resolve cannot be read at all — which is exactly the
    /// document being diagnosed.
    /// </remarks>
    private static List<Undeclared> FindUndeclared(
        string text, out bool reachedEnd, out string? stoppedBecause)
    {
        var used = new List<Undeclared>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);

        reachedEnd = false;
        stoppedBecause = null;

        using var stringReader = new StringReader(text);
        using var reader = new XmlTextReader(stringReader)
        {
            Namespaces = false,

            // Never fetch an external DTD. A malicious or merely broken document
            // could otherwise make this reach out over the network.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };

        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                Record(reader.Name, reader, used, seen);

                if (!reader.HasAttributes)
                {
                    continue;
                }

                while (reader.MoveToNextAttribute())
                {
                    string name = reader.Name;

                    if (name.Equals("xmlns", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (name.StartsWith("xmlns:", StringComparison.Ordinal))
                    {
                        declared.Add(name.Substring("xmlns:".Length));
                        continue;
                    }

                    Record(name, reader, used, seen);
                }

                reader.MoveToElement();
            }

            reachedEnd = true;
        }
        catch (XmlException ex)
        {
            // Something beyond a namespace problem — an unclosed tag, a bare
            // ampersand. What was found so far is still true; the caller needs to
            // know the picture is incomplete.
            stoppedBecause = ex.Message;
        }

        return used.Where(u => !declared.Contains(u.Prefix)).ToList();
    }

    private static void Record(
        string qualifiedName, XmlTextReader reader, List<Undeclared> used, HashSet<string> seen)
    {
        int colon = qualifiedName.IndexOf(':');
        if (colon <= 0)
        {
            return;
        }

        string prefix = qualifiedName.Substring(0, colon);

        // Both are bound by the XML specification, so using them without a
        // declaration is correct and needs no repair.
        if (prefix.Equals("xml", StringComparison.Ordinal) ||
            prefix.Equals("xmlns", StringComparison.Ordinal))
        {
            return;
        }

        if (seen.Add(prefix))
        {
            used.Add(new Undeclared(prefix, reader.LineNumber, reader.LinePosition, qualifiedName));
        }
    }

    /// <summary>
    /// Finds the offset in the root element's start tag at which an attribute may
    /// be inserted.
    /// </summary>
    /// <remarks>
    /// Scanned from the raw text because no parser will open this document. Two
    /// details make a naive <c>IndexOf('&gt;')</c> wrong, and both occur in real
    /// files: a quoted attribute value may contain <c>&gt;</c>
    /// (<c>title="a &gt; b"</c>), and a DOCTYPE internal subset may contain one
    /// (<c>&lt;!DOCTYPE p [&lt;!ENTITY nbsp "&amp;#160;"&gt;]&gt;</c>). Quotes and
    /// bracket depth are both tracked.
    /// </remarks>
    private static bool FindRootTagInsertionPoint(string text, out int insertAt)
    {
        insertAt = 0;
        int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }

            if (Peek(text, i + 1) == '?')
            {
                int close = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return false;
                }

                i = close + 2;
                continue;
            }

            if (Peek(text, i + 1) == '!')
            {
                if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
                {
                    int close = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return false;
                    }

                    i = close + 3;
                    continue;
                }

                i = SkipDoctype(text, i);
                if (i < 0)
                {
                    return false;
                }

                continue;
            }

            char first = Peek(text, i + 1);
            if (char.IsLetter(first) || first is '_' or ':')
            {
                return EndOfStartTag(text, i, out insertAt);
            }

            i++;
        }

        return false;
    }

    private static bool EndOfStartTag(string text, int start, out int insertAt)
    {
        insertAt = 0;

        int nameEnd = start + 1;
        while (nameEnd < text.Length && IsNameChar(text[nameEnd]))
        {
            nameEnd++;
        }

        char quote = '\0';
        for (int i = nameEnd; i < text.Length; i++)
        {
            char c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c != '>')
            {
                continue;
            }

            // Insert before the '/' of a self-closing tag, and before any
            // whitespace preceding the '>', so the result reads naturally.
            int at = i;
            if (at > nameEnd && text[at - 1] == '/')
            {
                at--;
            }

            while (at > nameEnd && char.IsWhiteSpace(text[at - 1]))
            {
                at--;
            }

            insertAt = at;
            return true;
        }

        // Unterminated start tag: broken well beyond a missing declaration, so
        // report nothing rather than guess where it ends.
        return false;
    }

    private static int SkipDoctype(string text, int start)
    {
        int depth = 0;
        char quote = '\0';

        for (int i = start + 2; i < text.Length; i++)
        {
            char c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    break;
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case '>' when depth <= 0:
                    return i + 1;
            }
        }

        return -1;
    }

    private static string? StrictParseError(string text)
    {
        try
        {
            XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            return null;
        }
        catch (XmlException ex)
        {
            return ex.Message;
        }
    }

    private static char Peek(string text, int index) =>
        index < text.Length ? text[index] : '\0';

    private static bool IsNameChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or ':' or '-' or '.';
}
