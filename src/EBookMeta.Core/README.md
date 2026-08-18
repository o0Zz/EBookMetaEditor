# EBookMeta.Core

All the logic: opening a book, reading its metadata, repairing what is provably
broken, and writing it back without disturbing anything else. Zero UI dependencies
— that is enforced by the `GuardCoreHasNoUiDependencies` target in the csproj, not
by review.

## How it fits together

Two axes, deliberately the same shape. A **container** knows how to get bytes out of a
file and how to write a new one; a **format** knows what the metadata document inside
those bytes means. Neither has heard of the other, and `Book` is the only thing
holding one of each.

```
                     ┌──────────────────────────┐
                     │ Book                     │
                     │                          │
                     │ Load(path)      Save()   │
                     └─────────────┬────────────┘
                                   │  asks both registries and names
                                   │  no implementation of either
                 ┌─────────────────┴─────────────────┐
                 │                                   │
   seam 1 · the metadata document       seam 2 · the bytes it sits in
                 │                                   │
   ┌─────────────▼────────────┐        ┌─────────────▼────────────┐
   │ BookFormats              │        │ BookContainers           │
   │                          │        │                          │
   │ Register  For            │        │ Register  For            │
   │ TryOpen  FromExtension   │        │ Open     Sniff           │
   └─────────────┬────────────┘        └─────────────┬────────────┘
                 │  the registry of                  │
   ┌─────────────▼────────────┐        ┌─────────────▼────────────┐
   │ IBookFormat              │        │ IContainer               │
   │                          │        │                          │
   │ Id   Extensions          │        │ Entries  (order kept)    │
   │ Capabilities             │        │ IsWritable               │
   │ TryOpen(BookSource)      │        │ OpenRead(entry)          │
   │ Read(container)          │        │ Rebuild(pending, path)   │
   │ Write(container, path)   │        │                          │
   ├──────────────────────────┤        ├──────────────────────────┤
   │ EpubFormat   .epub       │        │ ZipContainer      ZIP    │
   │ CbzFormat    .cbz .cbt   │        │ TarContainer      TAR    │
   │              .cbr .cb7   │        │ RarContainer      RAR *  │
   │ Fb2Format    .fb2        │        │ SevenZipContainer 7z  *  │
   │              .fb2.zip    │        │ PalmDbContainer   PalmDB │
   │ MobiFormat   .mobi .prc  │        │ RawContainer      none   │
   │              .azw .azw3  │        └─────────────┬────────────┘
   └──────────────────────────┘                      │  * cannot compress itself
                                       ┌─────────────▼────────────┐
                                       │ ExternalArchiver         │
                                       │                          │
                                       │ finds and runs the       │
                                       │ rar.exe / 7z.exe that    │
                                       │ is on the machine        │
                                       └──────────────────────────┘
```

Most of the design falls out of that picture:

- **The two axes are independent.** `ZipContainer` has never heard of EPUB and
  `EpubFormat` never opens a file. That is why CBZ, CBT, CBR and CB7 are one format
  class living in four containers, and why adding a container is a file and a
  `Register` line.
- **The registries are the only way across.** Nothing above them ever says
  `new ZipContainer()` — `Book` asks for a `ContainerKind` and gets an `IContainer`.
- **There is no second path.** `BatchSession` is a list of `Book`s; saving a row of
  the grid calls `Book.Save`, the same one the single-file window calls. Five hundred
  files are five hundred independent saves, not a batch write.

### Opening a file

```
Book.Load("comic.cbz")
 │
 ├─▶ BookFormats.TryOpen(path)
 │    │
 │    ├─▶ BookSource.Open(path)            the file is opened once; 8 KB is read
 │    │    └─▶ BookContainers.Sniff(head)
 │    │         "PK\x03\x04" at offset 0  ─▶  ContainerKind.Zip
 │    │
 │    ├─▶ offer that one BookSource to every registered format
 │    │         EpubFormat  ─▶ null        no mimetype entry
 │    │         CbzFormat   ─▶ Strong      holds ComicInfo.xml
 │    │         Fb2Format   ─▶ null        no .fb2 entry
 │    │         MobiFormat  ─▶ null        not a PalmDB
 │    │    the strongest MatchConfidence wins, never registration order
 │    │
 │    └─▶ source.Container ─▶ BookContainers.Open(path, Zip) ─▶ ZipContainer
 │             opened on first use and handed to the winner still open
 │
 ├─▶ CheckEntryNames(container)            GEN-E003 for a name that escapes
 │
 └─▶ CbzFormat.Read(container)
      └─▶ container.OpenRead("ComicInfo.xml")  ─▶  BookMetadata
```

**Almost nothing runs here**, which is most of why a cold launch stays under 400 ms.
A read parses the metadata document and stops: no entry is decompressed to decide what
a file is, and no page is touched.

### Saving a file

```
Book.Save()
 │
 └─▶ AtomicFileWriter.Write(path, …)      the only sanctioned way a file is replaced
      │
      ├ 1  BookContainers.Open(path, Zip)
      │      reopened inside the callback, so the read handle is shut before the
      │      swap pulls the file out from under it
      │
      ├ 2  CbzFormat.Write(container, metadata, "comic.cbz.tmp")
      │      │   every correction happens here, never on open:
      │      │     CBZ-W010  no ComicInfo.xml    ─▶ one is created
      │      │     CBZ-E011  it sits in a folder ─▶ moved to the root
      │      │     CBZ-E020  PageCount is wrong  ─▶ recounted from the images
      │      │
      │      └─▶ container.Rebuild(PendingEntry[], "comic.cbz.tmp")
      │             the metadata document is replaced; every other entry is
      │             copied through byte for byte
      │
      └ 3  File.Replace(comic.cbz.tmp ─▶ comic.cbz, backup comic.cbz.bak)
```

`Read` parses and stops; **`Write` is where every repair lives**. A repair therefore
cannot reach the disk unless the user saves, which is what makes "the file on disk is
what you last saved" true by construction rather than by care.

### When the container cannot compress itself

```
RarContainer.Rebuild(entries, "comic.cbr.tmp")        SevenZipContainer is identical
 │
 ├─ no archiver on this machine
 │    └─▶ CBR-F002 / CB7-F002 — refused, and the user's file is left untouched
 │
 └─▶ ExternalArchiver
      ├─ Stage()  writes every entry under comic.cbr.tmp.stage\
      │             the one place in Core that extracts to disk, and therefore
      │             where ".." and duplicate names are refused outright
      ├─ writes __entries.lst — UTF-16, one relative name per line
      └─ runs rar.exe / 7z.exe  ─▶  comic.cbr.tmp
            every way that can fail becomes the same BookIoException
```

## Read these six files, in this order

| # | File | Why |
|---|---|---|
| 1 | `Book.cs` | ~200 lines and the whole story: `Load` and `Save`. Start here. |
| 2 | `IBookFormat.cs` | Axis 1 — the metadata document. The interface and every type spoken around it. |
| 3 | `IContainer.cs` | Axis 2 — the physical file. Same shape as axis 1, deliberately. |
| 4 | `BookFormats.cs` | The format registry, and how a file is identified. |
| 5 | `BookContainers.cs` | The container registry, and the magic-number sniff. |
| 6 | `Formats/CbzFormat.cs` | The smallest real format. Read one before reading four. |

After that, `Formats/` is four files and `Containers/` six, and they can be read in
any order, or not at all until you need one.

## Adding a format

One implementation plus one line. Implement `IBookFormat`, call
`BookFormats.Register` — nothing in the UI or the open path changes, because both
ask the registry and neither names a format. The format brings its own
`Extensions`, which is where the Settings form's context-menu list and the
file-dialog filter both come from.

Build it in this order: fixture builder → `TryOpen` → `Read` → rules → `Write`.
Never write before round-trip reading is proven.

## Adding a container

The same shape: one file under `Containers/` plus one `BookContainers.Register`
call. The container exposes a `ContainerFormat` of its own — its `ContainerKind`, its
opener and the magic numbers `Sniff` answers to — exactly as a format exposes its
`Extensions`, so `BookContainers` knows no magic numbers. A container that cannot
compress itself — RAR, 7z — supplies an `ExternalArchiver` with the name of a
program to find, the registry keys that record where it installed, and its command
line, and gets the staging, the list file and the one failure answer for free.

In full, and this is the whole list: the file, a `ContainerKind` member, the
`Register` line. Nothing else in Core names a container, and nothing switches on
`ContainerKind` — `ExtensionPointTests` proves it by adding a container and a format
from the test assembly and opening a file through both.

**A comic archive is smaller still.** `CbzFormat.Flavours` pairs a `FormatId` with a
`ContainerKind` and an extension; a new row plus the container is the whole change,
and `BookFormats` does not need editing.

`TryOpen` **claims; it does not parse, and it never throws.** A damaged file is
still that format's file — an EPUB whose OPF will not parse is exactly the file the
repair path exists for. Parsing happens in `Read`, after a winner is picked.
