using System.Globalization;
using System.Text;
using System.Xml.Linq;
using EBookMeta.Model;

namespace EBookMeta.Documents;

/// <summary>
/// The write half of <see cref="OpfDocument"/>: applying edits to the parsed
/// tree and serialising it back without disturbing anything else.
/// </summary>
public sealed partial class OpfDocument
{
    /// <summary>
    /// Serialises the document back to bytes.
    /// </summary>
    /// <returns>The complete package document.</returns>
    /// <remarks>
    /// An untouched document serialises to exactly the characters it arrived as,
    /// and a one-field edit produces a one-line diff — see
    /// <see cref="XmlSourceFormat"/> for what that costs and why.
    /// </remarks>
    public byte[] Serialize() => Format.Compose(Document.Root);

    /// <summary>
    /// Applies metadata to the document, writing both EPUB 2 and EPUB 3
    /// conventions.
    /// </summary>
    /// <param name="metadata">The metadata to write.</param>
    /// <remarks>
    /// <para>
    /// Both conventions are always written, regardless of the declared
    /// <c>package/@version</c>. Old readers understand only the attribute forms
    /// and <c>calibre:series</c>; new ones prefer the refinement forms. Writing
    /// both is what calibre does, and it is the only way a file reads correctly
    /// in both.
    /// </para>
    /// <para>
    /// Elements are updated in place and only when their value actually
    /// changes. Nothing this method does not recognise is touched, which is how
    /// an unknown <c>&lt;meta&gt;</c> survives a save intact.
    /// </para>
    /// </remarks>
    public void ApplyMetadata(BookMetadata metadata)
    {
        Throw.IfNull(metadata);

        XElement metadataElement = Metadata
            ?? throw new BookFormatException("The package has no metadata element.", EntryName);

        // Compare against the document as it currently stands and touch only
        // what actually differs.
        //
        // This is what reconciles two invariants that would otherwise conflict:
        // "always write both EPUB 2 and EPUB 3 conventions" and "saving without
        // editing yields identical bytes". A file carrying only the EPUB 3 form
        // would otherwise gain EPUB 2 attributes merely by being opened and
        // saved, which is a change the user did not ask for. So dual-convention
        // output applies to fields the user changed; a field left alone is left
        // alone. Reporting single-convention files is EPUB-W032 and EPUB-W061's
        // job, with the user opting in to the fix.
        BookMetadata current = ReadMetadata();

        ApplyTitle(metadataElement, current, metadata);
        ApplySimple(metadataElement, "language", current.Language, metadata.Language);
        ApplySimple(metadataElement, "publisher", current.Publisher, metadata.Publisher);
        ApplySimple(metadataElement, "description", current.Description, metadata.Description);
        ApplySimple(metadataElement, "rights", current.Rights, metadata.Rights);
        ApplyDate(metadataElement, current, metadata);
        ApplySubjects(metadataElement, current, metadata);
        ApplyCreators(metadataElement, current, metadata);
        ApplySeries(metadataElement, current, metadata);

        InvalidateCaches();
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    private void ApplyTitle(XElement metadataElement, BookMetadata current, BookMetadata metadata)
    {
        XElement? title = DcElements("title").FirstOrDefault();

        if (metadata.Title is null)
        {
            if (title is null)
            {
                return;
            }

            // Removed rather than ignored. A cleared field that quietly keeps its
            // old value is the worst of the three options: the user is told nothing
            // and the editor then disagrees with the file. The publication is now
            // missing a required element, which is what rule EPUB-E012 is for.
            Log.Warning(
                $"The title was cleared, so '{EntryName}' no longer has a dc:title. "
                + "EPUB requires one, and readers will show the file name instead.");

            RemoveRefinement(title, null);
            RemoveWithWhitespace(title);
            return;
        }

        if (title is null)
        {
            title = AddDcElement(metadataElement, "title", position: 0);
            SetValue(title, metadata.Title);
        }
        else if (!Same(current.Title, metadata.Title))
        {
            SetValue(title, metadata.Title);
        }

        if (!Same(current.SortTitle, metadata.SortTitle))
        {
            ApplyFileAs(metadataElement, title, metadata.SortTitle, "title");
        }
    }

    /// <summary>
    /// Writes a sort form in both conventions: an <c>opf:file-as</c> attribute
    /// for EPUB 2 readers and a <c>meta refines</c> element for EPUB 3 ones.
    /// </summary>
    private void ApplyFileAs(XElement metadataElement, XElement target, string? sortForm, string idHint)
    {
        if (sortForm is null)
        {
            target.Attribute(OpfNs + "file-as")?.Remove();
            RemoveRefinement(target, "file-as");
            return;
        }

        target.SetAttributeValue(OpfNs + "file-as", sortForm);
        SetRefinement(metadataElement, target, "file-as", sortForm, idHint, scheme: null);
    }

    /// <summary>
    /// Whether either cover convention already names the given manifest item.
    /// </summary>
    /// <param name="manifestItemId">The manifest <c>id</c> to check for.</param>
    /// <returns>
    /// <see langword="true"/> when writing the declaration would change nothing.
    /// </returns>
    /// <remarks>
    /// Lets a save skip touching the cover declarations entirely when they are
    /// already correct, which is what keeps an unedited save byte-identical.
    /// </remarks>
    public bool CoverIsAlreadyDeclaredAs(string manifestItemId)
    {
        Throw.IfNullOrEmpty(manifestItemId);

        bool epub3 = Manifest.Any(i =>
            i.IsCoverImage && string.Equals(i.Id, manifestItemId, StringComparison.Ordinal));

        bool epub2 = Metadata?
            .Elements()
            .Any(e => e.Name.LocalName == "meta" &&
                      (string?)e.Attribute("name") == "cover" &&
                      (string?)e.Attribute("content") == manifestItemId) == true;

        // Either form on its own counts as "already declared" for the purpose of
        // not rewriting an untouched file. A file carrying only one is reported
        // by EPUB-W032 rather than silently corrected on save.
        return epub3 || epub2;
    }

    /// <summary>
    /// Declares a manifest item as the cover in both conventions.
    /// </summary>
    /// <param name="manifestItemId">The manifest <c>id</c> of the cover image.</param>
    /// <remarks>
    /// EPUB 2 readers look for <c>&lt;meta name="cover"&gt;</c>; EPUB 3 readers
    /// look for <c>properties="cover-image"</c> on the manifest item. Files in
    /// the wild routinely carry one and not the other, which is what rule
    /// EPUB-W032 reports — so when the cover does change, write both.
    /// </remarks>
    public void ApplyCoverDeclaration(string manifestItemId)
    {
        Throw.IfNullOrEmpty(manifestItemId);

        if (Metadata is null)
        {
            return;
        }

        SetLegacyMeta(Metadata, "cover", manifestItemId);

        foreach (ManifestItem item in Manifest)
        {
            bool isCover = string.Equals(item.Id, manifestItemId, StringComparison.Ordinal);
            string[] properties = (item.Properties ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !string.Equals(p, "cover-image", StringComparison.Ordinal))
                .ToArray();

            if (isCover)
            {
                properties = properties.Concat(new[] { "cover-image" }).ToArray();
            }

            if (properties.Length == 0)
            {
                item.Element.Attribute("properties")?.Remove();
            }
            else
            {
                item.Element.SetAttributeValue("properties", string.Join(" ", properties));
            }
        }

        InvalidateCaches();
    }

    private void ApplyCreators(XElement metadataElement, BookMetadata current, BookMetadata metadata)
    {
        if (SameCreators(current.Creators, metadata.Creators))
        {
            return;
        }

        ApplyCreatorSet(metadataElement, metadata, CreatorKind.Creator, "creator");
        ApplyCreatorSet(metadataElement, metadata, CreatorKind.Contributor, "contributor");
    }

    private static bool SameCreators(IList<Creator> a, IList<Creator> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!Same(a[i].Name, b[i].Name) ||
                !Same(a[i].SortName, b[i].SortName) ||
                !Same(a[i].NativeRole ?? a[i].Role, b[i].NativeRole ?? b[i].Role) ||
                a[i].Kind != b[i].Kind)
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyCreatorSet(
        XElement metadataElement, BookMetadata metadata, CreatorKind kind, string localName)
    {
        List<Creator> wanted = metadata.Creators.Where(c => c.Kind == kind).ToList();
        List<XElement> existing = DcElements(localName).ToList();

        for (int i = 0; i < wanted.Count; i++)
        {
            Creator creator = wanted[i];
            XElement element = i < existing.Count
                ? existing[i]
                : AddDcElement(metadataElement, localName, position: null);

            SetValue(element, creator.Name);
            ApplyFileAs(metadataElement, element, creator.SortName, $"{localName}{i + 1}");

            // Prefer the native role string over the mapped relator: it is what
            // the originating format's readers expect, and round-tripping
            // through the mapping would degrade it.
            string? role = creator.NativeRole ?? creator.Role;

            if (role is null)
            {
                element.Attribute(OpfNs + "role")?.Remove();
                RemoveRefinement(element, "role");
            }
            else
            {
                element.SetAttributeValue(OpfNs + "role", role);
                SetRefinement(
                    metadataElement, element, "role", role, $"{localName}{i + 1}", "marc:relators");
            }
        }

        // Anything left over was deleted by the user.
        for (int i = wanted.Count; i < existing.Count; i++)
        {
            RemoveRefinement(existing[i], null);
            RemoveWithWhitespace(existing[i]);
        }
    }

    private void ApplySeries(XElement metadataElement, BookMetadata current, BookMetadata metadata)
    {
        SeriesInfo? series = metadata.Series;

        if (Same(current.Series?.Name, series?.Name) &&
            current.Series?.Index == series?.Index &&
            Same(current.Series?.RawIndex, series?.RawIndex))
        {
            return;
        }

        string? index = series?.Index is { } value
            // Invariant culture: a French locale would write "2,5", which no
            // reader parses.
            ? value.ToString("0.############", CultureInfo.InvariantCulture)
            : series?.RawIndex;

        // EPUB 2 form — calibre's, and what most files in the wild actually use.
        SetLegacyMeta(metadataElement, "calibre:series", series?.Name);
        SetLegacyMeta(metadataElement, "calibre:series_index", index);

        // EPUB 3 form.
        XElement? collection = metadataElement
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta" &&
                                 (string?)e.Attribute("property") == "belongs-to-collection");

        if (series is null)
        {
            if (collection is not null)
            {
                RemoveRefinement(collection, null);
                RemoveWithWhitespace(collection);
            }

            return;
        }

        if (collection is null)
        {
            collection = new XElement(metadataElement.Name.Namespace + "meta",
                new XAttribute("property", "belongs-to-collection"));
            Append(metadataElement, collection);
        }

        SetValue(collection, series.Name);
        SetRefinement(metadataElement, collection, "collection-type", "series", "collection", null);

        if (index is null)
        {
            RemoveRefinement(collection, "group-position");
        }
        else
        {
            SetRefinement(metadataElement, collection, "group-position", index, "collection", null);
        }
    }

    private void ApplySubjects(XElement metadataElement, BookMetadata current, BookMetadata metadata)
    {
        if (current.Subjects.SequenceEqual(metadata.Subjects, StringComparer.Ordinal))
        {
            return;
        }

        List<XElement> existing = DcElements("subject").ToList();

        for (int i = 0; i < metadata.Subjects.Count; i++)
        {
            XElement element = i < existing.Count
                ? existing[i]
                : AddDcElement(metadataElement, "subject", position: null);

            SetValue(element, metadata.Subjects[i]);
        }

        for (int i = metadata.Subjects.Count; i < existing.Count; i++)
        {
            RemoveWithWhitespace(existing[i]);
        }
    }

    private void ApplyDate(XElement metadataElement, BookMetadata current, BookMetadata metadata)
    {
        if (Same(current.PublicationDate?.Raw, metadata.PublicationDate?.Raw))
        {
            return;
        }

        XElement? date = DcElements("date")
            .FirstOrDefault(e => (string?)e.Attribute(OpfNs + "event") is null or "publication");

        if (metadata.PublicationDate is null)
        {
            // Cleared, so the element goes. Unlike the title, nothing requires a
            // publication date, so this needs no warning.
            if (date is not null)
            {
                RemoveWithWhitespace(date);
            }

            return;
        }

        date ??= AddDcElement(metadataElement, "date", position: null);

        // The raw text is authoritative: a source that said "2013" must not be
        // rewritten as "2013-01-01", which would assert a day it never claimed.
        SetValue(date, metadata.PublicationDate.Raw);
    }

    private void ApplySimple(XElement metadataElement, string localName, string? currentValue, string? value)
    {
        if (Same(currentValue, value))
        {
            return;
        }

        XElement? element = DcElements(localName).FirstOrDefault();

        if (string.IsNullOrEmpty(value))
        {
            if (element is not null)
            {
                RemoveWithWhitespace(element);
            }

            return;
        }

        element ??= AddDcElement(metadataElement, localName, position: null);
        SetValue(element, value!);
    }

    /// <summary>
    /// Sets an element's text without disturbing it when nothing changed, so an
    /// untouched field contributes nothing to the diff.
    /// </summary>
    private static void SetValue(XElement element, string value)
    {
        if (!string.Equals(element.Value, value, StringComparison.Ordinal))
        {
            element.SetValue(value);
        }
    }

    private void SetLegacyMeta(XElement metadataElement, string name, string? content)
    {
        XElement? meta = metadataElement
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == name);

        if (content is null)
        {
            if (meta is not null)
            {
                RemoveWithWhitespace(meta);
            }

            return;
        }

        if (meta is null)
        {
            meta = new XElement(metadataElement.Name.Namespace + "meta", new XAttribute("name", name));
            Append(metadataElement, meta);
        }

        meta.SetAttributeValue("content", content);
    }

    /// <summary>
    /// Creates or updates an EPUB 3 <c>&lt;meta refines&gt;</c> pointing at
    /// <paramref name="target"/>, giving the target an id if it lacks one.
    /// </summary>
    private void SetRefinement(
        XElement metadataElement, XElement target, string property, string value,
        string idHint, string? scheme)
    {
        string id = (string?)target.Attribute("id") ?? MakeUniqueId(idHint);
        target.SetAttributeValue("id", id);

        XElement? refinement = metadataElement
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "meta" &&
                                 (string?)e.Attribute("property") == property &&
                                 ((string?)e.Attribute("refines"))?.TrimStart('#') == id);

        if (refinement is null)
        {
            refinement = new XElement(metadataElement.Name.Namespace + "meta",
                new XAttribute("refines", "#" + id),
                new XAttribute("property", property));

            if (scheme is not null)
            {
                refinement.SetAttributeValue("scheme", scheme);
            }

            // Placed immediately after what it refines, which is where a reader
            // expects to find it and keeps the diff local.
            target.AddAfterSelf(refinement);
            InsertSeparatorBefore(refinement, target);
        }

        SetValue(refinement, value);
    }

    private void RemoveRefinement(XElement target, string? property)
    {
        string? id = (string?)target.Attribute("id");
        if (id is null || Metadata is null)
        {
            return;
        }

        List<XElement> doomed = Metadata
            .Elements()
            .Where(e => e.Name.LocalName == "meta" &&
                        ((string?)e.Attribute("refines"))?.TrimStart('#') == id &&
                        (property is null || (string?)e.Attribute("property") == property))
            .ToList();

        foreach (XElement element in doomed)
        {
            RemoveWithWhitespace(element);
        }
    }

    private string MakeUniqueId(string hint)
    {
        var taken = new HashSet<string>(
            Document.Descendants()
                .Select(e => (string?)e.Attribute("id"))
                .Where(id => id is not null)
                .Select(id => id!),
            StringComparer.Ordinal);

        if (!taken.Contains(hint))
        {
            return hint;
        }

        for (int i = 2; ; i++)
        {
            string candidate = hint + i.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private XElement AddDcElement(XElement metadataElement, string localName, int? position)
    {
        // DcNs rather than a literal prefix: XDocument emits whichever prefix is
        // already bound in the source, so this never invents one.
        var element = new XElement(DcNs + localName);

        if (position == 0 && metadataElement.Elements().Any())
        {
            XElement first = metadataElement.Elements().First();
            first.AddBeforeSelf(element);
            InsertSeparatorBefore(first, element);
        }
        else
        {
            Append(metadataElement, element);
        }

        return element;
    }

    /// <summary>
    /// Appends a child, copying the indentation already used inside the parent
    /// so a generated element lines up with its hand-written neighbours.
    /// </summary>
    private static void Append(XElement parent, XElement child)
    {
        string indent = DetectIndent(parent);
        parent.Add(new XText(indent), child);
    }

    private static void InsertSeparatorBefore(XElement element, XElement reference)
    {
        string indent = reference.Parent is null ? "\n" : DetectIndent(reference.Parent);
        element.AddBeforeSelf(new XText(indent));
    }

    private static string DetectIndent(XElement parent)
    {
        // The whitespace before the first child is the parent's indentation
        // style, whatever it happens to be. Guessing two spaces instead would
        // make generated elements visibly foreign in a file that uses tabs.
        if (parent.FirstNode is XText text && text.Value.Contains('\n'))
        {
            return text.Value;
        }

        return "\n    ";
    }

    private static void RemoveWithWhitespace(XElement element)
    {
        // Take the whitespace that preceded the element too, otherwise deleting
        // a field leaves a blank line behind and the diff shows two changes.
        if (element.PreviousNode is XText text && text.Value.Trim().Length == 0)
        {
            text.Remove();
        }

        element.Remove();
    }

    private void InvalidateCaches()
    {
        _manifest = null;
        _spine = null;
        _refinements = null;
    }
}
