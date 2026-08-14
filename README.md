# EBookMetaEditor

A fast metadata editor for ebooks and comics on Windows.

Metadata in ebooks is a nightmare — no two files use the same field names, and once
you import them into Kavita you cannot search by author.

EBookMetaEditor lets you update the metadata of a whole library in batch, from a
right-click, in a few seconds. It also fixes the problems it finds in the file's
XML along the way, so the result actually imports cleanly into Kavita and the rest.

## Why

There are already good tools out there, but none of them did what I was looking
for:

- Edit metadata without importing the book into an application first — directly
  from Windows Explorer.
- Support every format I actually have (EPUB, CBZ, MOBI, FB2, …).
- Fix incorrect metadata. Most editors will happily write out an OPF or a
  `ComicInfo.xml` without checking whether the result is valid, let alone
  consistent with the rest of the file.

## Formats

| Format | Read | Write | Container | Metadata stored in |
|---|---|---|---|---|
| EPUB 2 / 3 | ✅ | ✅ | ZIP | OPF package document |
| CBZ | ✅ | ✅ | ZIP | `ComicInfo.xml` |
| CBT | ✅ | ✅ | TAR | `ComicInfo.xml` |
| FB2 | ✅ | ✅ | plain XML file | `<description>` |
| FB2.ZIP | ✅ | ✅ | ZIP | `<description>` |
| MOBI / PRC | ✅ | ✅ | PalmDB | EXTH records |
| AZW / AZW3 | ✅ | ✅ | PalmDB | EXTH records |

**Not supported:** CBR, CB7, PDF, KFX, AZW4, LIT, PDB, RB, DjVu, audiobooks.

CBR and CB7 are the ones people ask about. Both can be *read* — but neither can be
written: RAR compression is proprietary and its licence forbids building a
compatible compressor, and no 7z writer ships here either. That would mean an
editor that opens your comics and then cannot save them, which is not what this
tool is for. The rest each need a different container, a different metadata
document, or both.

**DRM-protected files are refused, not worked around.** An encrypted AZW or AZW3
is detected on open and reported; nothing is written. Removing DRM is not something
this tool does.

Files are identified by their **content, not their extension**. A `.cbz` that is
really a RAR archive is extremely common, and the tool tells you so by name instead
of mangling it.

## What you can edit, per format

Formats do not store the same things, so the editor greys out fields the file
cannot keep rather than accepting text it would silently discard.

| Field | EPUB | CBZ / CBT | FB2 | MOBI / AZW3 |
|---|---|---|---|---|
| Title | read + write | read + write | read + write | read + write |
| Sort title | read + write | — | — | — |
| Authors | read + write | read + write | read + write | read + write |
| Author sort names | read + write | — | — | — |
| Author roles | read + write | read + write | read + write | — |
| Series | read + write | read + write | read + write | — |
| Series index | read + write | read + write | read + write | — |
| Description | read + write | read + write | read + write | read + write |
| Publisher | read + write | read + write | read + write | read + write |
| Publication date | read + write | read + write | read + write | read + write |
| Modification date | read + write | — | — | — |
| Language | read + write | read + write | read + write | read + write |
| Subjects / tags | read + write | read + write | read + write | read + write |
| Identifiers (ISBN…) | read + write | — | read | read |
| Rights | read + write | — | — | read + write |
| Cover image | read + write | read | read | read |

A few of these are worth explaining:

- **Covers can only be replaced in EPUB.** Elsewhere the cover is a page image, a
  base64 blob buried in the book's own XML, or a whole database record — and
  editing page images is out of scope.
- **MOBI has no series field.** There is no EXTH record for one that can be
  verified against the published format documentation, and writing a guessed record
  number would put your series into a field that means something else, in a file
  you cannot inspect. So the box is greyed out rather than quietly wrong.
- **MOBI language is read from EXTH 524 only.** The header's numeric locale field
  is not used as a fallback, because turning it back into a language tag would be
  guesswork.

## Checking and repair

Every file is checked as it opens — no button, no "validate" step. Structural and
semantic problems are reported by a stable rule ID (`EPUB-E020`, `CBZ-E020`,
`MOBI-W012`, …) in the log, under the **?** menu.

Broken XML is repaired **in memory** so a file no other tool will load becomes
editable. Nothing reaches your disk until you press Save, and saving writes the
repair along with your edits — plus anything else it can *prove* wrong, like a
comic's page count recomputed from the images actually in the archive.

Saving a file you did not edit gives you back byte-for-byte the same file. That is
tested for every format.

## Editing in batch

Select several files in Explorer and pick **Edit metadata**, drop a folder on the
window, or use **File ▸ Batch edit folder…** — any of them opens one window with a
row per file and a column per field.

- Type straight into the cells, or **copy and paste** them: `Ctrl+C` and `Ctrl+V`
  work as they do in a spreadsheet. One copied value pastes into every selected
  cell — copy a publisher once, select the column down thirty rows, paste — and a
  copied block fills right and down, including a block pasted in from Excel. Both
  are under **Edit** and under right-click.
- **Save all** writes every file you changed, and nothing else: a file you did not
  edit is not rewritten, it is not even opened. Each one keeps a `.bak`.
- Every row says what happened to it. A file that cannot be edited — a `.cbz` that
  is really a RAR archive, or a DRM-protected AZW — says so instead of failing
  quietly, and one that fails to save fails alone.
- Columns a format cannot store are greyed out per row, so a comic's sort-title
  cell is dead while the book's beside it is live.

Description and cover art are not in the grid; those are what the single-file
window is for. Double-click a row's file name to open it there.

## Install

Download the latest release, unzip it anywhere, and run `EBookMetaEditor.exe`.
Nothing else is installed — it is a single executable and it runs on a clean
Windows 10 or 11 machine.

To add the right-click entry, open **File ▸ Settings** and press **Add to
context menu**. You choose which formats it applies to, so you can tag comics
without touching your ebooks.

> `.fb2.zip` is not offered in the right-click menu. Windows can only attach a verb
> to a single extension, and the only one available would be `.zip` — which would
> put this tool's entry on every archive on your machine. Open those files by
> dragging them onto the window, or through **File ▸ Open**.

## Building

```bash
git clone https://github.com/o0Zz/EBookMetaEditor
cd EBookMetaEditor
dotnet build
dotnet test
dotnet build src/EBookMeta.App -c Release
```

Any recent .NET SDK works. The projects are SDK-style and target `net48` via the
`Microsoft.NETFramework.ReferenceAssemblies` package, so neither Visual Studio
nor the .NET Framework targeting pack is required.

## Architecture

Two independent axes: **container** and **metadata document**. EPUB is ZIP + OPF;
CBZ is ZIP + `ComicInfo.xml`; CBT is the same document over TAR; MOBI is
PalmDB + EXTH. Keeping these separate means a new format is usually a new document
handler over an existing container, or a new container under an existing document —
not a new codebase. CBT cost one container and three lines of registration.

Those two axes are two interfaces, and they sit at the root of `EBookMeta.Core`
so the shape is visible at a glance:

```
EBookMeta.Core   all logic — containers, formats, validation, repair.
                No UI dependencies whatsoever.
  IBookFormat     what a metadata document can do: Read, Write
  IContainer      what a file of named entries can do: Entries, OpenRead, Rebuild
  BookFormats     registry of IBookFormat: which one opens which format
  BookContainers  factory for IContainer: which one a file gets
  Book            one open file: Load and Save, and what they noticed
  Containers/     ZipContainer (with its own central-directory reader),
                  TarContainer, PalmDbContainer, RawContainer
  Formats/        EpubFormat, CbzFormat, Fb2Format, MobiFormat — each X.cs
                  (read/write) plus X.Rules.cs (the validation rules);
                  FormatDetector, capabilities
  Documents/      OPF, ComicInfo, FictionBook, MOBI/EXTH, and the XML-fidelity
                  plumbing beneath them
  Model/          BookMetadata and friends
  BatchSession    many files read, edited and saved together
  MetadataFields  what a field looks like in a box, shared by both editors
  NamespaceRepair recovery of missing xmlns declarations
  Log             the session log, shown in the ? menu
EBookMeta.App    WinForms UI, single instance, receives paths in argv.
                One window per file, one grid for many. Also owns context-menu
                registration, from its Settings form.
```

Adding a format is one `IBookFormat` and one `BookFormats.Register` call. The UI
asks the registry which format to use and never names one, so nothing in the
window changes when a format is added. `CbzFormat`, `Fb2Format` and `MobiFormat`
are each registered twice — once per format id — because in each case two formats
are the same metadata document in a different wrapper.

Each format declares its capabilities, which is what drives the table above and
what greys out fields the format cannot store.

The UI is deliberately thin. `Core` is the project; the WinForms layer is a form
over it, and could be replaced without touching the logic.

Getting the bytes right is most of the work, and each format needed a different
trick for it:

- `ZipContainer` parses the ZIP central directory itself, because
  `ZipArchiveEntry` does not expose the per-entry compression method — and an EPUB
  whose `mimetype` entry gets compressed on save is rejected by readers.
- `TarContainer` keeps each entry's raw 512-byte header and writes it back
  untouched, so the mode, owner and padding a real `tar` recorded survive an edit.
- `Fb2Document` parses only the `<description>` element and splices it back at the
  offsets it came from. An FB2 is the metadata *and* the entire book in one XML
  file; re-serialising all of it to change a title would rewrite every line.
- `MobiDocument` rebuilds the header record and preserves every EXTH record it does
  not understand, byte for byte — those are the only copy in the file.

MOBI support is written from the published description of the PalmDB, MOBI and
EXTH layouts. No code is taken from calibre, whose equivalent is GPL-3.0 and
incompatible with this project's Apache-2.0 licence.

Design notes, hard invariants and the full validation rule tables are in
[`CLAUDE.md`](CLAUDE.md).
