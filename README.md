# EBookMetaEditor

A fast metadata editor for ebooks and comics on Windows.

Metadata in ebooks is nightmare, they never use the exact same name etc... and so when you import then in kavita you cannot look for author etc...

EBook meta editor allow to quickly update all metadata of media in batch, with a simple right click en few seconds.
Additionnally it will fix any issue detected in the xml in order to be properly imported in tools lik kavita and others...

## Why

There are already good tools on the web but none of them was able to achieve what I was looking for.
 - Edit metadata without importing book in any application (Direcly in windwos explorer) 
 - Support of all format (epub, cbz, etc...)
 - Fix incorrect metadata: Most metadata editors will happily write out an OPF or a `ComicInfo.xml`
  without checking whether the result is valid, let alone consistent with the
  rest of the file.
  
## Formats

| Format | Read | Write | Metadata stored in |
|---|---|---|---|
| EPUB 2 / 3 | ✅ | ✅ | OPF package document |
| CBZ | ✅ | ✅ | `ComicInfo.xml` |

That is the whole list, deliberately. Both are ZIP archives, which means one
container implementation and one write path to get right — and the write path is
where a metadata editor destroys your library if it is careless.

Not supported: CBR, CB7, CBT, MOBI, PRC, AZW, AZW3, KFX, FB2, PDF, LIT, PDB,
RB, DjVu, audiobooks. Each needs a different container or a different metadata
document, and several need both.

**Recognising a format is not the same as supporting it.** EBookMetaEditor identifies
RAR, 7z, TAR, PalmDB and PDF by content so it can tell you what a file really
is — it just will not edit them. That matters more than it sounds: a `.cbz` that
is really a RAR archive is extremely common, and being told so is usually what
you wanted.

For comics, EBookMetaEditor reads `ComicInfo.xml`, CoMet and the ComicBookLover JSON
blob, writes `ComicInfo.xml`, and leaves the others untouched.

## What it does

**Edits the fields you actually care about** — title, sort title, creators with
sort names and roles, series and index, description, publisher, date, subjects,
identifiers, language, and the cover image. For comics, the full ComicRack set
including Writer, Penciller, Inker, Colorist, Letterer and Cover Artist.

**Writes both EPUB 2 and EPUB 3 conventions.** Series, sort names, roles and
cover declarations are expressed differently in the two versions, and readers
disagree about which they honour. EBookMetaEditor writes both, so the file works in old
and new readers alike.

**Validates the file.** Around forty checks, each with a stable rule ID:
XML well-formedness, `unique-identifier` actually resolving to a
`dc:identifier`, spine entries pointing at real manifest items, `PageCount`
matching the actual number of images, declared encoding matching the real
bytes, image filenames sorting into a stable reading order, and more. Findings
carry line and column where the format has them.

**Detects files whose extension lies.** A `.cbz` that is really a RAR archive
is extremely common. EBookMetaEditor identifies formats by content and tells you when
the extension disagrees.

**Repairs broken XML on open.** A package document that uses an undeclared
namespace prefix — `opf:file-as` without `xmlns:opf`, the most common reason an
EPUB will not load at all — is corrected as the file opens, so you can get
straight to editing it. The log records what was corrected. The file
on disk is untouched until you save; saving writes the correction along with your
edits, and keeps the previous version as a `.bak`.

Only corrections that are certain are made. A prefix no specification defines is
reported and left alone rather than guessed at, because inventing a namespace
would put metadata in your book that was never there.

Unclosed tags, bare ampersands and encoding mismatches are not handled yet.

**Never loses a field it doesn't understand.** Unrecognised `ComicInfo`
elements and arbitrary `<meta>` entries are preserved verbatim — for XML that
means the element is never touched in the first place, so it cannot be
reformatted by a save.

**Does not touch anything else.** Content files, stylesheets and page images
are copied byte for byte. Entry order and compression method are preserved.
Saving a file you haven't edited produces a byte-identical result.

**Keeps a log you can read.** Everything it did this session — what a file was
detected as, what was repaired, what was written — is under the **?** menu, along
with the About box. It stays in memory on a clean run and is written to
`EBookMetaEditor.log` beside your settings the moment anything goes wrong, so a
crash still leaves evidence.

## What it doesn't do

Reading books, editing content or CSS, format conversion, DRM, library
management, page-image processing. Use calibre, Sigil or ComicTagger for those.

## Install

Requires Windows 10 (version 1903 or later) or Windows 11.

**Nothing to install.** EBookMetaEditor runs on .NET Framework 4.8, which ships with
Windows — there is no runtime to download first.

Download the latest release, unzip it anywhere, and run `EBookMetaEditor.exe`.

To add the right-click entry, open **File ▸ Settings** and press **Add to
context menu**. You choose which formats it applies to, so you can tag comics
without touching EPUB.

Registration writes only to `HKCU` — no administrator rights needed — and it
does not change which application opens these files by default. On Windows 11
the entry appears under *Show more options*.

The application is a single small executable and stores no configuration
outside its own folder.

## Building

```bash
git clone https://github.com/<you>/ebookmetaeditor
cd ebookmetaeditor
dotnet build
dotnet test
dotnet build src/EBookMeta.App -c Release
```

Any recent .NET SDK works. The projects are SDK-style and target `net48` via the
`Microsoft.NETFramework.ReferenceAssemblies` package, so neither Visual Studio
nor the .NET Framework targeting pack is required.

## Architecture

Two independent axes: **container** and **metadata document**. EPUB is
ZIP + OPF; CBZ is ZIP + `ComicInfo.xml`. Keeping these separate means a new
format is usually a new document handler over an existing container, not a new
codebase.

```
EBookMeta.Core   all logic — containers, format handlers, validation, repair.
                No UI dependencies whatsoever.
  BookFormats     the handler registry: which parser opens which format
  Containers/     ZipContainer, with its own central-directory reader
  Formats/        IFormatHandler + EpubHandler, FormatDetector, capabilities
  Documents/      OPF, ComicInfo
  Model/          BookMetadata and friends
  NamespaceRepair recovery of missing xmlns declarations
  Log             the session log, shown in the ? menu
EBookMeta.App    WinForms UI, single instance, receives a file path in argv.
                Also owns context-menu registration, from its Settings form.
```

Adding a format is one `IFormatHandler` and one `BookFormats.Register` call. The
UI asks the registry which handler to use and never names one, so nothing in the
window changes when a format is added.

Each format declares its capabilities, so the UI disables fields the format
cannot store rather than accepting input that would be discarded.

The UI is deliberately thin. `Core` is the project; the WinForms layer is a
form over it, and could be replaced without touching the logic.

`ZipContainer` parses the ZIP central directory itself, because
`ZipArchiveEntry` does not expose the per-entry compression method and
reproducing it exactly is required — an EPUB whose `mimetype` entry gets
compressed on save is rejected by readers.

Design notes, hard invariants and the full validation rule table are in
[`CLAUDE.md`](CLAUDE.md).

## Roadmap

1. EPUB read/write/validate/repair — establishes the container and atomic-write machinery
2. CBZ + `ComicInfo.xml` — reuses the ZIP layer

Possible later: online metadata lookup (Open Library, Google Books, Comic Vine)
to populate fields from an ISBN or a series name.

Formats beyond these two are out of scope rather than merely unscheduled.

## Contributing

Issues and pull requests welcome. Three things to know:

- **Fixtures must be synthetic.** Test files are generated by builders in
  `tests/EBookMeta.Core.Tests/Builders/` — one XHTML page and a 1×1 PNG for
  ebooks, a few 1×1 PNGs for comics. Please don't commit real books or comics.
- **New validation rules need a fixture** that triggers the rule in isolation,
  named after it, e.g. `broken-cbz-e020-pagecount.cbz`.
- **The hard invariants in `CLAUDE.md` exist because breaking them corrupts
  people's libraries.** Read that section before changing anything in a
  container or serialisation path.

## Prior art

- [calibre](https://calibre-ebook.com/) — `ebook-meta` is the reference for
  metadata conventions, and this project follows its EPUB 2 / EPUB 3 dual-write
  behaviour.
- [ComicTagger](https://github.com/comictagger/comictagger) — the reference for
  `ComicInfo.xml` handling.
- [Sigil](https://sigil-ebook.com/) — EPUB authoring.
- [epub-metadata-editor](https://github.com/benchen71/epub-metadata-editor) —
  Ben Chenoweth's VB.NET editor, the direct inspiration for the field layout.
- [epubcheck](https://github.com/w3c/epubcheck) — the authoritative EPUB
  validator. EBookMetaEditor implements a fast subset focused on what breaks in
  practice; run epubcheck for full conformance.

## Licence

[Apache License 2.0](LICENSE). With MOBI out of scope there was never a reason to
go near calibre's GPL-3.0 metadata code, so nothing constrains it.
