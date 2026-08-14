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

List of supported format 

| Format | Read | Write | Metadata stored in |
|---|---|---|---|
| EPUB 2 / 3 | ✅ | ✅ | OPF package document |
| CBZ | ✅ | ✅ | `ComicInfo.xml` |

That is the whole list, deliberately. Both are ZIP archives, which means one
container implementation and one write path to get right — and the write path is
where a metadata editor destroys your library if it is careless.

Not supported for now: CBR, CB7, CBT, MOBI, PRC, AZW, AZW3, KFX, FB2, PDF, LIT, PDB,
RB, DjVu, audiobooks. Each needs a different container or a different metadata
document, and several need both.

## Editing in batch

Select several files in Explorer and pick **Edit metadata**, drop a folder on the
window, or use **File ▸ Batch edit folder…** — any of them opens one window with a
row per file and a column per field.

- Type straight into the cells, or set a value once and **apply it to every
  selected row**. `Ctrl+D` copies the current cell down the selection.
- **Save all** writes every file you changed, and nothing else: a file you did not
  edit is not rewritten, it is not even opened. Each one keeps a `.bak`.
- Every row says what happened to it. A file that cannot be edited — a `.cbz` that
  is really a RAR archive — says so instead of failing quietly, and one that fails
  to save fails alone.
- Columns a format cannot store are greyed out per row, so a comic's sort-title
  cell is dead while the book's beside it is live.

Description and cover art are not in the grid; those are what the single-file
window is for. Double-click a row's file name to open it there.

## Install

Download the latest release, unzip it anywhere, and run `EBookMetaEditor.exe`.

To add the right-click entry, open **File ▸ Settings** and press **Add to
context menu**. You choose which formats it applies to, so you can tag comics
without touching EPUB.

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
  Formats/        IFormatHandler + EpubHandler + CbzHandler, FormatDetector,
                  capabilities
  Documents/      OPF, ComicInfo
  Model/          BookMetadata and friends
  BatchSession    many files read, edited and saved together
  MetadataFields  what a field looks like in a box, shared by both editors
  NamespaceRepair recovery of missing xmlns declarations
  Log             the session log, shown in the ? menu
EBookMeta.App    WinForms UI, single instance, receives paths in argv.
                One window per file, one grid for many. Also owns context-menu
                registration, from its Settings form.
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
