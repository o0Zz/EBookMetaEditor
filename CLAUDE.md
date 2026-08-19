# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this project is

`EBookMetaEditor` — a fast ebook and comic metadata editor for Windows, launched
from the Explorer right-click menu.

1. **It repairs without being asked.** Broken XML is recovered on open, in memory.
   Disk bytes are untouched until the user saves.
2. **It fixes what it can prove and says nothing about the rest.** No Validate
   button, no findings panel — see **Repairs**.

## Keep it simple

- **Do not overthink.** Answer the question asked, at the size it was asked. A one-line
  fix needs no new abstraction, and a decision that is cheap to reverse needs no
  paragraph in this file.
- **Do the simplest thing that meets the requirement.** No speculative generality: no
  interface with one implementation, no options record with one field, no cache or lock
  around something that is never contended, no setting nobody asked for. If the caller
  does not exist yet, neither does the code for it.
- **Reach for a library before hand-rolling.** Prefer the BCL, then a permissively
  licensed package, then your own code — in that order. SharpCompress reads RAR and
  writes ZIP because reimplementing either would be absurd.
- **Byte-exactness outranks convenience.** Invariant 5 says a save must not rewrite what
  the user did not edit. Where a library cannot express a field it did not write, that is
  an **accepted limitation**: named in invariant 5, with a test that states what the
  property becomes instead. It is never a silent change, and **never a byte-identity
  expectation edited until it passes.** The ZIP central directory and PalmDB records are
  read by hand for this reason.

## Format support

Two independent axes — **container** and **metadata document**. Conflating them is
the main design risk in this codebase.

| Format | Container | Metadata document |
|---|---|---|
| EPUB 2 / 3 | ZIP | OPF |
| CBZ | ZIP | `ComicInfo.xml` |
| CBT | TAR | `ComicInfo.xml` |
| CBR | RAR | `ComicInfo.xml` |
| CB7 | 7z | `ComicInfo.xml` |
| FB2 | none (raw XML) | `<description>` |
| FB2.ZIP | ZIP | `<description>` |
| MOBI / PRC | PalmDB | EXTH |
| AZW / AZW3 | PalmDB | EXTH (one or two) |

All are writable. Anything not in this table is out of scope. **A format that reuses
an existing metadata document is a container; one that needs a new document is a
project.** `CbzFormat` is registered under four `FormatId`s and `Fb2Format` and
`MobiFormat` under two each, and none of them names a container — `CbzFormat.Flavours`
is the one table pairing a comic `FormatId` with the `ContainerKind` it lives in and
the extension it wears, and adding a fifth comic archive is a row in it plus a
container.

Comic archives may also carry CoMet (`comet.xml`) or a ComicBookLover JSON blob in
the ZIP comment. Read all three, write `ComicInfo.xml`, leave the others untouched.
`System.IO.Compression` cannot write a ZIP comment back, so `CbzFormat.Write` logs
`CBZ-W012` and throws rather than dropping the blob — on write, not on open.

### CBR and CB7 write through someone else's archiver

Reading either needs only SharpCompress. Writing needs a compressor this build has
not got: the licences forbid both redistributing `rar.exe` and building a compatible
RAR compressor, and SharpCompress reads 7z but writes only ZIP, TAR and GZip. So
`RarContainer` and `SevenZipContainer` each run a program already on the machine, and
**`ExternalArchiver` is that machinery, written once**: the search, the staging
directory, the list file, the process, the timeout, the one failure answer. A
container supplies three things and nothing else — the executable's name, the registry
keys that record its install directory, and its command line.

**Core finds it; there is no setting.** Each archiver is looked for in two places, and
the search stops at the first answer:

1. the registry keys the container named, under both bitness views and `HKLM` then
   `HKCU` — a 32-bit WinRAR on 64-bit Windows registers under `Wow6432Node`, and
   whether a save works must not depend on how this build was compiled. Each key's
   `Path`, `Path64` or default value is read; a value ending in `.exe` names the
   install directory by way of its own path. WinRAR uses
   `App Paths\WinRAR.exe`, 7-Zip `SOFTWARE\7-Zip` and `App Paths\7zFM.exe`.
2. the executable on `PATH`.

- **Never fall back to a windowed build** — `WinRAR.exe`, `7zFM.exe`, `7zG.exe`. The
  command lines are console switches, and a windowed build puts a progress window on
  screen mid-save. An install missing `Rar.exe` or `7z.exe` counts as no archiver.
- Guess no directories, read no version number.
- **No caching, no lock, no setting.** Do not grow it into a cached, lockable,
  host-configurable property.
- `Locator` is the one seam, internal and one per container, so tests can point it at
  `StandInArchiver` or at `() => null`. **Whether CBR-F002 or CB7-F002 fires must never
  depend on what the machine running the suite has installed.**

**The two command lines are not shared, and that is the point of passing one in.**
WinRAR keeps reading `@listfile` after `--`; 7-Zip's `--` stops list-file parsing as
well as switch parsing, so `@list` after it becomes the name of a file to add and the
save silently archives nothing. RAR therefore gets `-- "target" @"list"` and 7-Zip gets
`"target" @"list"` with no separator. Nothing is lost by the missing separator: the
only path on that command line is a `.tmp` sibling this build composed, and entry names
arrive inside the list file, which is never switch-parsed.

**The refusal belongs to the container, not the format.** `CbzFormat` treats CBR and
CB7 exactly as CBZ and CBT — same capabilities, same corrections, same `Write`. The
container's `Rebuild` is what refuses, as `CBR-F002` or `CB7-F002`, so a save runs the
whole ordinary path and either reaches an archiver or fails at the last step with the
user's file untouched.

Three rules that look like oversights and are not:

- **The editor is never read-only, archiver or not.** Fields stay live and
  `Book.CanSave` stays true; both follow `FormatCapabilities`, which is a fact about
  `ComicInfo.xml`. Declaring CBR or CB7 unwritable would turn a refusal that happens
  once into a permanent greyed-out mode.
- **Never "fix" a missing archiver by writing a ZIP instead.** A `.cbr` that is not a
  RAR is the disguised-archive problem this tool exists to report. Conversion would be a
  separate, user-initiated verb; it is not implemented.
- **A full rebuild, not an update.** The pending list can add, move and drop entries as
  well as replace one, and getting that diff subtly wrong is how an archive loses a page.

**Failure is one answer.** Running someone else's program fails a dozen ways — not
there, not executable, will not start, hangs, refuses the arguments, disk full. There
is no ladder of checks and no message per cause: nothing probes for the file, checks a
version, or names a vendor. Everything produces the same
`BookIoException($"Could not write '{targetPath}'.")`, with the particulars in
`Log.Debug`. `IsWriteFailure` is that one bucket, and it names `Win32Exception` and
deliberately *not* its base `SystemException`, so a null-reference bug in this file
still crashes instead of being reported as a polite save failure.

**Writing through an archiver is the only place Core extracts to disk**, which is what
makes hard invariant 4 enforceable rather than advisory. `ExternalArchiver.Stage`
refuses, before a byte is written:

- a name that is absolute or contains `..`, via `ContainerEntry.EscapesArchive` —
  shared with `Book.Load` so the two cannot disagree about what "escapes" means, and
  backed by a full-path containment check on where the file landed;
- a **duplicate entry name**, which archives may legally repeat: on disk the second
  copy overwrites the first and a page vanishes from the saved comic.

Both are `BookFormatException`, specific because they are about the archive rather than
the tool.

**Refused at open:** an **encrypted** archive of either kind, which needs a password
this build never asks for, and a **solid RAR**, which stores every file in one
compression stream SharpCompress cannot serve an entry out of — `CBR-F001` and
`CB7-F001`. Refused in `Open`, not at `OpenRead`: the entry list of an archive whose
`ComicInfo.xml` cannot be read is not a book.

**A solid 7z is not refused, and must not be.** 7-Zip packs one block by default, so
nearly every `.cb7` in the wild is solid, and SharpCompress decodes the block to serve
an entry out of it. Reading the metadata document costs the block; that is the price of
the format, not a bug to route around.

**A CB7 rebuild does not preserve entry order.** 7-Zip sorts what it is handed and no
switch asks it not to. Pages are found by name — the cover is picked with
`NaturalNameComparer` and readers sort too — so nothing above the container depends on
it, but do not write a test that says otherwise.

## Target and deployment

- **.NET Framework 4.8**: ships with Windows 10 (1903+) and 11, so the app runs on a
  clean machine. No Windows preinstalls the .NET 5+ runtime.
- **A single executable**, `EBookMetaEditor.exe`. No CLI, no setup program;
  context-menu registration lives in the Settings form.
- **WinForms.** Never add `PublishAot` or `PublishTrimmed`. On `net48`
  `UseWindowsForms` does not apply — `EBookMeta.App` uses classic `<Reference>` items.
- The UI stays thin: a port to Avalonia must not touch `EBookMeta.Core`.

## Layout

```
src/EBookMeta.Core/      net48 — all logic. ZERO UI dependencies.
  README.md              where to start reading, and in what order
  IBookFormat.cs         seam 1 (metadata-document axis) + its whole vocabulary:
                         FormatId, MetadataField, FormatCapabilities, ReadOptions,
                         BookSource, FormatClaim, MatchConfidence
  IContainer.cs          seam 2 (physical axis) + its vocabulary: ContainerKind,
                         ContainerFormat, ContainerSignature, ContainerEntry,
                         PendingEntry, ZipCompressionMethods, SectionStream,
                         ReadAllBytes
  BookFormats.cs         registry of seam 1 and the open path
  BookContainers.cs      registry of seam 2: Register / For / Open / Sniff. Holds no
                         magic numbers — each container declares its own Format
  Book.cs                one open file: Load and Save
  BookExceptions.cs      BookFormatException, BookIoException, UnsupportedFormatException
  AtomicFileWriter.cs    the only sanctioned way a user's file is replaced
  ExternalArchiver.cs    finds and runs somebody else's compressor, for the two
                         containers that cannot write themselves
  BatchSession.cs        many files read, edited and saved together
  MetadataFields.cs      the text projection of a field, shared by both editors
  NaturalNameComparer.cs so 2.jpg sorts before 10.jpg
  Log.cs, Compat.cs
  Containers/            ZipContainer, TarContainer, RarContainer and
                         SevenZipContainer (both write only through an archiver they
                         find), PalmDbContainer, RawContainer
  Formats/               EpubFormat, CbzFormat (CBZ+CBT+CBR+CB7), Fb2Format (FB2+FB2.ZIP),
                         MobiFormat (MOBI/PRC + AZW/AZW3) — each holding its own
                         detection, read, write, repairs and metadata document
  Xml/                   XmlEncodingDetector, XmlSourceFormat, XmlExactWriter,
                         XmlLineIndex, XmlTree (tree edits two formats share)
  Model/                 BookMetadata, Creator, Identifier, SeriesInfo (which owns
                         series-index text), CoverImage, UnmappedField, BookDate
                         (which owns date parsing)
src/EBookMeta.App/       net48 — WinForms, single instance, argv = paths
  Program, MainForm, BatchForm, SettingsForm, LogForm, AboutForm, Dialogs,
  AppSettings, ShellRegistration, SingleInstance, AppIcon, Strings, KeyValueFile,
  EmbeddedAssemblies
  Languages/             one key = value file per interface language
tests/EBookMeta.Core.Tests/  net48
  Builders/              synthetic file generators (see Test corpus)
```

1. **The two seams live at the Core root**, each with its registry beside it. Nothing
   outside `BookFormats` / `BookContainers` names a concrete implementation —
   `Book.Load` opens an `IContainer`, never a `ZipContainer`.
2. **One file per format, one per container**, and those folders hold nothing else, so
   `ls Formats/` answers "what does this build support". Format files are long (EPUB
   ~1800 lines); that is the accepted cost. Do not split one out, and do not park a
   shared type in the folder.
3. **A shared type goes in the seam file for its axis**, decided by which registry
   consumes it, not by which folder is closest.
4. **A file gets a folder only when several files share a subject.** One format calling
   it means it belongs inside that format, however general its name sounds; two or more
   means `Xml/`, `Model/` or the root.

**Adding a format is one implementation plus one `BookFormats.Register` call.** It
brings its own `Extensions` and its own `TryOpen`. **Adding a container is one file
under `Containers/` plus one `BookContainers.Register` call**, which is passed the
container's own `Format` — the same shape: a container states its `ContainerKind`,
its opener and the magic numbers `Sniff` answers to, and `BookContainers` only says
which containers are in the build.

Everything downstream reads the two registries, so neither addition edits a third
file. In particular: the Settings form's context-menu checkboxes, the file-dialog
filter and the About box are all built from `Extensions`, the log's name for a format
comes from `FormatId.DisplayName`, and `BookFormats.FromExtension` and
`BookContainers.Sniff` answer from what is registered rather than from a `switch`.
**If a change of yours needs a list of formats or extensions written out a second
time, that is the bug.** Nothing outside `BookContainers`' six `Register` lines names
a container type at all, and there is no `switch` on `ContainerKind` anywhere.

`ExtensionPointTests` is that promise made executable: it adds a container and a
format from the *test* assembly, registers them, and opens a file end to end through
`Book.Load` — no Core file edited. It is what fails if the inventory gets hardcoded
again.

The exceptions, all deliberate:

- **`ContainerKind`** is an enum, so a new container is also a line there. That, the
  file, and the `Register` call are the complete list for a container nothing needs
  to *read as a book*.
- **A container holds books only once a format claims it.** For a comic archive that
  is one row in `CbzFormat.Flavours`, plus a `FormatId` and its `DisplayName` line;
  for anything else, a new `IBookFormat`.
- **`FormatCapabilities`** must be stated per format — see **Formats**.
- **`.fb2.zip`** is the one compound extension. `Fb2Format` declares it, the file
  dialog offers it, and `ShellRegistration` drops it, because
  `SystemFileAssociations` keys on a single extension and registering it would mean
  claiming `.zip` and putting this app's verb on every archive on the machine. That
  filter lives in `ShellRegistration`, where the reason is, and not in the format.

`EBookMeta.Core` referencing `System.Windows.Forms`, `System.Drawing` or any UI
package is a build-breaking error, enforced by `GuardCoreHasNoUiDependencies` in
`EBookMeta.Core.csproj`. Cover art crosses as `byte[]` plus a media type, never as a
`Bitmap`.

## Commands

```bash
dotnet build
dotnet test
dotnet build src/EBookMeta.App -c Release
```

There is no CLI, so **the xunit corpus is the entire verification surface**. Every new
Core capability gets tests in the same change.

## Architecture

### Containers

```csharp
interface IContainer : IDisposable {
    bool IsWritable { get; }
    IReadOnlyList<ContainerEntry> Entries { get; }   // order preserved
    Stream OpenRead(ContainerEntry entry);
    void Rebuild(IEnumerable<PendingEntry> entries, string targetPath);
}
```

It knows nothing about books. `ContainerEntry` carries name, length and **compression
method as read**, so `Rebuild` can reproduce it. `ZipArchiveEntry` does not expose
that, so `ZipContainer` parses the central directory itself and pairs it with
`ZipArchive` **by index**, because ZIP names are not unique.

`ContainerEntry` / `PendingEntry` are a read/write pair; content is a `Func<Stream>` so
a rebuild streams rather than holding a 300-page comic in memory. **Use
`PendingEntry.Replacing`, not `FromBytes`, whenever new content stands in for an
existing entry** — `FromBytes` is for content with no original, and choosing it by
mistake silently discards what the source container was holding on to.

- `RawContainer` presents a bare `.fb2` as a container of one entry named after the
  file, so `Fb2Format`, `Book`, `AtomicFileWriter` and the batch grid need not care
  whether it is a loose file or a ZIP member.
- `PalmDbContainer` exposes numbered PalmDB records as `record0`, `record1`, … It
  refuses a rebuild whose record *count* differs from the source, because record
  numbers are referenced from inside the file and this build cannot find every such
  pointer. Resizing a record is fine and recomputes the offset table.
- `RarContainer` and `SevenZipContainer` cannot rebuild themselves unaided:
  `IsWritable` follows their `Locator`, and `Rebuild` either shells out through
  `ExternalArchiver` or reports `CBR-F002` / `CB7-F002`. Both normalise entry names
  from backslashes — `CbzFormat` decides whether `ComicInfo.xml` is nested by looking
  for a slash, so `sub\ComicInfo.xml` left alone would read as a root entry and
  `CBZ-E011` would never fire. **`ExternalArchiver.Stage` is the one place a directory
  entry must be handled rather than copied through**: RAR and 7z both record a folder
  marker with no trailing separator, so only `IsDirectory` tells it from a page, and
  writing it as a file fails on the directory its own pages just created. ZIP
  reproduces its markers as zero-length entries and must keep doing so, or a CBZ
  with a folder loses them and breaks byte-identity.
- `TarContainer` reads and writes through SharpCompress, which models an entry as a
  name, a size and a timestamp. A save therefore resets each entry's mode, uid and gid,
  drops its uname and gname, and pads with two zero blocks where `tar` pads to ten
  kilobytes — the accepted limitation invariant 5 names.

  **Three SharpCompress writer defects decide how it is configured, so do not
  "simplify" any of them away.** Each was found by writing a file and reading it back;
  none is documented.

  1. Its **GNU writer zeroes the magic field** at offset 257, so `BookContainers.Sniff`
     would not recognise this build's own output. Hence USTAR.
  2. Its **USTAR writer never fills the ustar prefix field** and throws a bare
     `Exception` on any name over 100 bytes, splittable or not.
     `RefuseIfNameTooLong` pre-empts it as CBT-F001, where the entry can still be named.
  3. **`WriteDirectory` zeroes the magic in either format**, so a comic whose first
     entry is its page folder — how comics in the wild are packed — would save to a file
     Sniff rejects. Folder markers are therefore **dropped on write**; the pages carry
     the structure in their names and `tar -x` recreates the directory. Writing the
     marker as a zero-length trailing-slash entry instead does not work: SharpCompress
     reads it back as a regular file named `pages`, which then collides with the
     directory `pages/01.png` needs.

### Formats

```csharp
interface IBookFormat {
    FormatId Id { get; }
    FormatCapabilities Capabilities { get; }
    IReadOnlyList<string> Extensions { get; }
    FormatClaim? TryOpen(BookSource source);
    BookMetadata Read(IContainer c, ReadOptions? o = null);
    void Write(IContainer c, BookMetadata m, string targetPath);
}
```

No `Validate`: reading reports what it noticed and writing reports what it corrected.
Implementations are **stateless singletons** — the registry hands the same instance to
every caller, including parallel batch threads. `Write` takes no source path: no format
touches the user's file, it produces a complete new file where `AtomicFileWriter` says.

`FormatCapabilities` declares which model fields a format can store **on write**, and
**the UI reads it to disable fields** so a user never types into a box whose content
will be discarded. It says nothing about reading: a format reads whatever it finds.
Adding a model field means updating every format — intentional friction.

### Opening a file: every format is asked

```csharp
using BookSource? source = BookFormats.TryOpen(path, out DetectedFormat detected);
```

The file is opened **once** and the same `IContainer` is shared with every format
asked. On success it stays open and `Book.Load` reads straight through it.

**`TryOpen` claims; it does not parse, and it never throws.** A format checks the
marker that identifies it and nothing more. A damaged file is still that format's file:
an EPUB whose OPF will not parse is what the repair path exists for, and declining it
would leave the file claimed by nobody and reported as unsupported. An exception would
abandon the loop before the remaining formats are asked.
`DetectionTests.A_damaged_file_is_still_claimed_by_its_own_format` keeps this true.

### Detection

By content, never by extension — in real collections a `.cbz` that is really RAR is
common. Two passes; a check belongs to the first when more than one format shares the
answer.

**`BookContainers.Sniff`, on the first 8 KB**, walking the `ContainerSignature`s each
registered container declared, rather than a `switch`: `PK\x03\x04` → ZIP; `Rar!\x1a\x07` →
RAR; `7z\xBC\xAF\x27\x1C` → 7z; `ustar` at offset 257 → TAR; `BOOKMOBI` or `TEXtREAd`
at offset 60 → PalmDB; anything else → Raw. **Signatures must not overlap**: the first
match wins, and nothing should depend on the order containers registered in.

**`IBookFormat.TryOpen`, each format with the shared `BookSource`:**

| Format | Claims on | Confidence |
|---|---|---|
| EPUB | ZIP with a `mimetype` entry reading `application/epub+zip` | Certain |
| EPUB | ZIP whose `mimetype` is wrong, compressed or misplaced | Strong |
| CBZ | ZIP holding `ComicInfo.xml` or `comet.xml` | Strong |
| CBZ | ZIP of nothing but images — the ComicRack convention | Weak |
| CBT | TAR, which no other supported format uses | Strong |
| CBR | RAR, which no other supported format uses | Strong |
| CB7 | 7z, which no other supported format uses | Strong |
| FB2.ZIP | ZIP holding a `.fb2` entry | Strong |
| FB2 | a `<FictionBook` root element in the first 2 KB of text | Certain |
| MOBI | PalmDB | Strong |

The strongest `MatchConfidence` wins, so the answer never depends on registration
order. The second EPUB row is the important one: **refusing to open files you know how
to repair is the failure mode to watch for here.**

- FB2 is the only format recognised from text, having no magic number. The search is
  bounded to the first 2 KB and gated on a leading `<`, so it costs nothing for non-XML
  and opens no `RawContainer` to decline a file.
- **Only the ZIP flavour of `CbzFormat` looks inside the archive.** TAR, RAR and 7z are
  this format's alone, so the container is the claim, and the CBR and CB7 arms must
  never touch `BookSource.Container`: opening one can throw `CBR-F001` or `CB7-F001`,
  and `TryOpen` must not throw. The refusal is left to `Book.Load`, where it is a real
  error rather than a reason to try the next format.
- Two answers no format can give, both in `BookFormats`: **which container the bytes
  are** (`Sniff`, run first), and **"recognised but not openable"** — the `Unsupported`
  table, which today holds `%PDF-` → PDF alone. Saying a `.cbz` is really something
  else (`GEN-W002`, `GEN-W004`) is a headline feature; naming a format costs a
  magic-number comparison, supporting it costs a container and a document.
- **Never decompress an entry to decide what a file is.** Beyond the 8 KB header a
  format may read **entry names** and at most one entry's content — the EPUB
  `mimetype`, twenty stored bytes.

### Metadata model

`BookMetadata` covers the common 80%: title, sort title, creators (name, sort name,
role), series + index, description, publisher, publication date, language, subjects,
identifiers, rights, cover.

- **One date.** The model holds a publication date only, so an EPUB 2 `dc:date` marked
  `opf:event="creation"` or `"modification"` is skipped on read rather than promoted
  into it, and `dcterms:modified` is not read. Both survive a save regardless, because
  nothing goes near the element.
- **Never lose a field you do not understand.** For XML formats this is achieved by
  *not touching the node*: the document retains the parsed tree and mutates only
  elements a field actually changed. `UnmappedFields` records such fields and the tests
  assert preservation through it; it is not the preservation mechanism.
- **Role mapping is lossy and that is accepted.** `ComicInfo`'s Writer / Penciller /
  Inker / Colorist / Letterer / CoverArtist do not map cleanly onto MARC relators. Keep
  the native role string alongside the mapped one and prefer it when writing back to
  the originating format.

### Editing many files at once

`BatchSession` uses the same machinery as the single-file window: one
`AtomicFileWriter.Write` per file, container reopened inside the callback. There is no
batch write path. A row is a `Book`; `SaveOne` calls `Book.Save`.

- **No transaction across files.** Twenty files are twenty independent saves; one
  failure leaves its own file untouched and says why on its own row.
- **A file is written because it is ticked, and editing it ticks it.**
  `WillSave => IsWritable && (IsDirty || SaveRequested)`, with `SaveRequested` as the
  grid's leftmost column. `IsDirty` means "differs from the snapshot taken on read", and
  cannot be the only gate, because a repair found on open changes no field. Ticking only
  ever *adds* files: an edited row is ticked and its box locked, so no click can drop an
  edit.
- **Covers are not read** (`ReadOptions.WithoutCover`).
- **Sorting is display only.** `Entries` keeps the order the files arrived in, so what a
  save writes, and in what order, never depends on how the grid is looking at it. The
  comparer reads the model, not the cells: a series index compares as a number and a
  date chronologically, though both are shown as the characters the file used. A blank
  sorts last in *both* directions, because an unread row is blank in every field; equal
  values keep file-name order. The grid re-sorts when a read finishes, never on an edit
  — a row must not jump out from under the cursor as it is typed into.
- **Numbering is the one bulk edit a paste cannot do**, being the one field every row
  wants a *different* value in. `Ctrl+I` counts down the series index of the selected
  rows in the order the grid is showing them. A row whose format cannot store an index,
  and a row with no series name — the model cannot hold an index on its own — are left
  alone and counted in the status line; only a row that took a number consumes one, so
  the sequence has no gaps.
- **Capabilities gate per cell, not per window**, so `Sort title` is dead on a comic and
  live on a book in the same column. `BatchEntry.Apply` refuses a field the format
  cannot store even if a caller asks, and every refusal is counted and reported.
- `Load` reads only `Pending` entries, so adding files later is cheap and calling it
  twice cannot discard unsaved edits.
- **`Refresh` is the only way back to `Pending`, and it will not touch an edited row.**
  It exists because files change underneath an open grid and because a row that failed
  deserves another go; it is reached by the button and by F5. A row with unsaved edits
  is counted and left exactly as it is, so there is still no reload in the sense that
  matters: **nothing in this class can drop an edit the user has not saved.** A *tick*
  is not an edit and is spent by the re-read, because `Snapshot` spends one on every
  read and a decision about a file's old contents should not carry over to its new
  ones.

**Both editors share `MetadataFields`** — authors split on semicolons, subjects on
commas, a date kept as the characters the file used, a sort name carried forward only
when its author did not change. Its early-return checks are what make "open a file and
save it without editing" byte-identical; a second implementation would keep that for
one editor and quietly lose it for the other.

## Hard invariants

Not style preferences. Violating these corrupts users' libraries.

**Writing, all formats**

1. **Never modify in place.** Build into a sibling `.tmp`, then `File.Replace` with a
   `.bak`. `AtomicFileWriter` is the only sanctioned path.
2. **Never open an archive with `ZipArchiveMode.Update`.**
3. Entries other than the metadata document are copied **byte for byte**. Never
   round-trip XHTML, CSS or images through a parser.
4. Reject and report absolute paths and `..` traversal in entry names and manifest
   hrefs rather than following them. `ContainerEntry.EscapesArchive` is the one
   predicate. Reading only reports it (`GEN-E003`); `ExternalArchiver.Stage` refuses.
5. **Round-tripping a valid file is a no-op**: open, save unedited, get identical
   bytes. There is a test per format; keep them green. An *invalid* file round-trips to
   a corrected one, and that is the point — the property is "saving does not
   gratuitously rewrite", not "saving never changes anything". A change with no logged
   rule behind it is the bug.

   *Not applicable to CBR or CB7*, whose bytes come from an archiver this build does
   not control and cannot ask to reproduce a compression setting — 7-Zip will not even
   be asked to keep the entry order it was handed. They are the only formats whose
   writer is not in this repository; not a precedent.

   *Accepted limitation, ZIP only:* `System.IO.Compression` does not preserve ZIP extra
   fields, timestamps or the archive comment, so byte-identity holds for archives whose
   structure can be reproduced (every fixture) and may not for third-party files
   carrying extra fields. Do not hand-roll a ZIP writer.

   *Accepted limitation, TAR:* SharpCompress's writer takes a name, a size and a
   timestamp, so a save resets each entry's mode, uid and gid, drops its uname and
   gname, replaces the producer's blocking factor with the minimum two blocks, and
   drops folder markers — see **Containers** for why that last one is not optional.
   Byte-identity therefore holds only for archives this build wrote itself, which is
   what `Saving_without_editing_is_byte_identical` covers; for a third-party archive
   the property is names, order, content and timestamps, per
   `Saving_keeps_what_the_writer_can_express`. **Dropping a marker is the one place
   this limitation changes archive content rather than a header field**, and it is safe
   only because the page names still carry the path.

**EPUB**

6. **`mimetype` is the first entry, stored uncompressed**, containing exactly
   `application/epub+zip` — no trailing newline, no BOM. Readers reject files that get
   this wrong.
7. **Write both EPUB 2 and EPUB 3 conventions on save**, regardless of the declared
   `package/@version`. It is the only way to be read correctly by both old and new
   readers.

   | Field | EPUB 2 | EPUB 3 |
   |---|---|---|
   | file-as | `opf:file-as` attribute | `<meta refines="#id" property="file-as">` |
   | role | `opf:role` attribute | `<meta refines="#id" property="role" scheme="marc:relators">` |
   | series | `<meta name="calibre:series">` | `<meta property="belongs-to-collection">` + `collection-type` |
   | series index | `<meta name="calibre:series_index">` | `<meta refines property="group-position">` |
   | cover | `<meta name="cover" content="id">` | manifest item `properties="cover-image"` |

**XML, all formats**

8. Load with `LoadOptions.PreserveWhitespace`, save with
   `SaveOptions.DisableFormatting`. Changing a title must produce a one-line diff.
9. Preserve the original XML declaration verbatim as literal source text —
   round-tripping through `XDeclaration` is not character-exact.
10. **Detect the real encoding from the bytes** (BOM, then declaration, then UTF-8) and
    flag mismatches. Do not trust `XDocument` to have guessed right.
11. Never invent namespace prefixes. Reuse the prefixes bound in the source; `opf:` and
    `dc:` are conventional, not guaranteed.

**Repair**

12. The text of every parsed document is retained for the session, because repairs are
    edits against it.
13. **A repair never writes a file by itself.** `Book.Load` recovers in memory,
    `Book.Save` persists, and there is nothing in between, which makes "the file on disk
    is what the user last saved" true by construction. Every repair is logged as a
    warning and never interrupts the user to ask.
14. Recovery uses a tolerant parse (`XmlTextReader` with `Namespaces = false`,
    `DtdProcessing.Ignore`, `XmlResolver = null`). A recovered document that still fails
    to parse is reported as unrepairable — do not guess further, and do not apply a
    partial repair.
15. **A repair is an edit, not a reserialisation.** Repairs are offset-and-length edits
    against the original text, so everything outside the edited span is copied byte for
    byte. The tolerant parse is for *diagnosis*; it must not become the thing that writes
    the file.
16. **Never infer what a name means.** Supplying a missing namespace URI is legitimate
    only for prefixes fixed by a published specification; `EpubFormat.KnownNamespaces`
    is that list, and a prefix absent from it is reported, never bound.
17. Diagnosis reads the markup, never a framework exception message. `XmlException` text
    is localised, so a regex over "'opf' is an undeclared prefix" works on an English
    machine and silently stops matching elsewhere.

## Repairs

**This tool fixes; it does not lecture.** No Validate button, no findings panel, no
`Finding` type.

> **A rule earns its place only if it changes the file.** If all it can do is report,
> it does not go in, and adding one needs a better reason than "epubcheck flags it".

The test is *provable, or merely likely?* A page count recomputed from the images
present is provable. A missing `dc:title` is not — nothing in the file says what it
should have been — so there is no such rule.

### Corrections

Provable from the file alone, and logged as a warning through `Log.Rule`.

| ID | When | What the save does |
|---|---|---|
| EPUB-W070 | The OPF uses a namespace prefix it never declares | Recovered in memory on open, persisted on save |
| EPUB-E040 | `mimetype` missing, compressed, or not first | Written back as the first entry, stored |
| EPUB-E041 | `package/@unique-identifier` names an id no `dc:identifier` carries, and exactly one identifier is present | The reference is pointed at that identifier — or, when it has no `id`, the element is labelled with the name the package already declares |
| EPUB-W062 | EPUB 2 only: the NCX's `dtb:uid` disagrees with the package identifier | The NCX is spliced back into line |
| CBZ-W010 | The archive carries no `ComicInfo.xml` | One is created |
| CBZ-E011 | `ComicInfo.xml` sits below the archive root | Moved up to the root |
| CBZ-E020 | `PageCount` disagrees with the images present | Recomputed from the images |
| MOBI-W030 | A joint MOBI/KF8 file was edited | Both headers were written |

### Refusals

| ID | Why |
|---|---|
| EPUB-F001 / F002 | The OPF or `container.xml` cannot be parsed or located |
| CBZ-F001 | `ComicInfo.xml` is present but not well-formed |
| CBZ-W012 | The archive carries a ZIP comment a rebuild cannot write back |
| CBT-F001 | An entry name is over 100 bytes, which the TAR header this build writes cannot hold |
| CBR-F001 | The RAR is solid or encrypted, so its entries cannot be read |
| CBR-F002 | Saving a CBR on a machine where no archiver was found. Everything that can go wrong *with* one is a plain `BookIoException` with no rule ID, on purpose |
| CB7-F001 | The 7z is encrypted. A solid one is read, not refused — see **CBR and CB7** |
| CB7-F002 | Saving a CB7 on a machine where no archiver was found, exactly as CBR-F002 |
| FB2-F001 / F002 | Not well-formed, or no `<description>` to edit |
| MOBI-F001 | No MOBI header in record 0 |
| MOBI-F002 | DRM-encrypted — rewriting the header produces a file no reader opens |
| GEN-W002 | The extension disagrees with the content |
| GEN-W004 | The format is recognised but unsupported |

`ZipContainer` also refuses outright when the central directory and `ZipArchive`
disagree on entry count, because rebuilding could pair the wrong compression method
onto the wrong entry. It throws `BookFormatException` without logging a rule.

Three rules that constrain what a repair may do:

- **GEN-E003 only reports, and invariant 5 is why that is allowed.** An entry name that
  is absolute or contains `..` is logged and nothing else happens: no correction,
  because nothing says what the name should have been; no refusal, because reading
  resolves nothing against the file system. The door is shut where it matters, in
  `ExternalArchiver.Stage`.
- **A joint MOBI/KF8 file's halves may disagree, and neither is provably right**, so
  there is no rule for the disagreement: copying either half over the other would delete
  the fields only that half carries. A save propagates *the fields the user edited* and
  nothing else — `MobiFormat.Merge` overlays the difference between what `Read` handed
  out and what came back onto each header's own metadata. Applying the edited
  `BookMetadata` wholesale to both headers is the obvious implementation and turns an
  unedited save of a mismatched file into data loss, which
  `MobiTests.Saving_a_joint_file_does_not_overwrite_one_half_with_the_other` catches.
- **EPUB-E041 stops at exactly one `dc:identifier`.** With two or more, nothing says
  which the package meant. It moves the *reference*, never the identifier's `id`:
  `unique-identifier` is pointed at by nothing, while an `id` may be the target of a
  `meta refines="#…"`. `RepairNcxIdentifier` resolves through that reference, so E041 is
  what lets EPUB-W062 fire at all.

**Where a repair lives:** in the format's own file, in `Write`, next to the other
corrections — `RepairMimetype` and `RepairNcxIdentifier` are the models to copy.
EPUB-W070 happens on *open* instead, because a document that will not parse cannot be
read at all otherwise.

## Logging

`Log.Info` progress · `Log.Warning` anything handled but notable, **including every
repair** · `Log.Error(message, exception)` failures · `Log.Rule` a repair or refusal,
rule ID first · `Log.Debug` detail that matters only after something went wrong.

Core logs; Core never writes to the console and holds no opinion about presentation.
`LogForm` renders `Log.Entries` directly rather than reading the file back.

Exceptions carry no structured path, so a message names the file or entry it is about
whenever that is not obvious from where it was raised.

## Interface language

`Strings` serves every piece of UI text from one `key = value` file per language in
`src/EBookMeta.App/Languages/` (`en`, `de`, `es`, `fr`, `it`), embedded in the exe.
`en.lang` is the master and the per-key fallback, so a half-finished translation shows
English rather than raw key names.

- **Not .resx.** Satellite assemblies are DLLs in subfolders and this app is one file.
  A plain text file is also something a translator can open.
- **`KeyValueFile` reads that format**, for the language files and for the settings in
  `EBookMetaEditor.ini` — named after the exe, like the `.log` beside it. No
  `[sections]`; a settings key is a flat name.
- **Adding a language is adding a file.** The picker is built from what is embedded and
  the csproj globs `Languages\*.lang`. `WithCulture=false` on that item is load-bearing
  — without it MSBuild builds a satellite assembly.
- **The log stays English, rule IDs included.** Core knows nothing about the interface
  language and must not learn.
- **`Strings.Use` sets `CurrentUICulture`, never `CurrentCulture`.** Core parses and
  writes metadata using the latter; a date that round-tripped differently because the
  window is in German would be the interface reaching the user's file.
- **Two plural forms**, `key.one` and `key.many`, via `Strings.Plural`. Everything a
  window shows goes through `Strings`, including counts a Core type can already render
  in English for the log.
- **Lay windows out with panels, not coordinates.** German runs about a third longer
  than English.

## Test corpus

Small and synthetic. **Never commit a real copyrighted book or comic.** Fixtures are
generated by builders in `Builders/` — one XHTML page and a 1×1 PNG cover for ebooks,
three 1×1 PNGs for comics — written to a temp directory at test time, so no binaries
are committed. A broken fixture is named after the rule it triggers.

- `CbzBuilder.WriteTo` takes a `ContainerKind`, so one set of comic fixtures serves CBZ
  and CBT. Do not fork it into a second builder.
- `RawTarBuilder` and `MobiBuilder` deliberately do *not* use `TarContainer` or
  `PalmDbContainer`. They assemble bytes the way `tar` and kindlegen do — a mode, an
  owner and a ten-kilobyte tail; a record table and an EXTH block with records this
  build has no field for. **A fixture generated by the code under test cannot prove that
  code reads real files**, and for these two formats that is the whole question.
- `RarBuilder` assembles the published RAR 4 block layout and stores every file with
  method `0x30`, and `SevenZipBuilder` the published 7z header layout with the Copy
  coder and an uncompressed next header, the data copied in verbatim in both. **Neither
  is an archive writer and neither must become one:** compression is exactly the part
  nothing here touches. There is nothing in either to promote into `EBookMeta.Core`.
  `SevenZipBuilder` writes a `kSubStreamsInfo` structure it could in principle leave
  out — SharpCompress reads no entry sizes at all without one, and 7-Zip always writes
  it — and its `Solid()` shape is the one 7-Zip produces by default.
- **Line endings are load-bearing.** `.gitattributes` normalises every text file to LF
  and `.editorconfig` agrees, and the builders hold their fixtures in C# raw string
  literals, which capture the *source file's* own newlines. A tool that rewrites a
  builder as CRLF changes the bytes of every fixture it builds, and `git diff` shows
  nothing because git normalises on read. Check the working copy, not the diff.

Required coverage:

- byte-identical round-trip for every format except CBR and CB7, which this build never
  writes itself, and CBT, where it holds only for archives this build wrote
- a CBT written by real `tar`, asserted to keep every entry's name, order, content and
  timestamp across a save — its mode, uid, gid, uname and blocking factor do not survive
- a CBT entry name over 100 bytes, splittable and not, both refused as CBT-F001 with the
  original untouched
- a CBT whose pages sit in a folder, saved and **reopened** — the folder marker is
  dropped and the assertion is that detection still works, which is the failure a
  round-trip through `Book.Load` catches and a container-level test does not
- a MOBI carrying EXTH records this build does not map, asserted to survive a write
- a MOBI whose header record is resized both ways, asserted to leave every later record
  readable
- a joint MOBI/KF8 file: read from the KF8 half, write an edit to both, leave each
  half's own unedited fields alone
- a DRM-encrypted MOBI, asserted to be refused
- an FB2 with a large body, byte-identical from `<body>` onwards after an edit
- the repair path: an undeclared prefix, an unknown prefix that must *not* be bound, an
  unclosed tag
- a compressed `mimetype`; a Latin-1 file declaring UTF-8
- a real RAR and a real 7z: read through, a Windows path normalised, entry order kept
  by RAR, and — with the search stubbed to find nothing — a save refused as `CBR-F002`
  or `CB7-F002` with the file unchanged and no `.tmp` or `.bak` left beside it
- a 7z packed as one solid block, asserted to serve every entry including the last,
  because that is the shape 7-Zip writes by default
- the archiver search, once for both archivers: a directory holding the executable is
  the answer, junk and quoted entries survive, the real search does not throw. **Every
  test that asserts a refusal points `Locator` at nothing first**, because a suite whose
  result depends on what the machine has installed is worse than no suite
- a solid RAR and an encrypted one, both refused as `CBR-F001` on the way in; an
  encrypted 7z refused as `CB7-F001`
- a CBR and a CB7 save through `StandInArchiver`: every entry staged under its own
  relative name in reading order, the file swapped in, the staging directory gone. The
  stand-in is a console program compiled at test time that parses both command lines
  and reads the same UTF-16 list file, and writes a manifest instead of compressing. It
  proves the hand-off, not that a real `rar.exe` or `7z.exe` likes the switches — **that
  is the one part of this build verified by hand**, and it is where the CB7 command line
  losing its `--` was found
- a save with the archiver missing, not a program, and returning non-zero — all three
  the same `BookIoException`, original untouched, nothing left behind
- an entry name that escapes the archive and a name that appears twice, both refused by
  `ExternalArchiver.Stage` before anything is written
- a CBR whose pages sit in a folder: the folder marker is staged as a directory and
  never listed for the archiver
- `BookContainers.Sniff` over every registered signature, and that every kind it can
  answer with has an implementation registered for it
- a 300-page comic, for order preservation and to prove open does not read pages

Repair and write tests assert on exact resulting bytes, so an accidental reformat fails
loudly.

Known gaps, open on purpose:

- **a missing `mimetype`.** EPUB-E040 covers "missing, compressed, or not first"; only
  compressed and not-first are tested.
- **reading CoMet.** No fixture carries a `comet.xml`; the CBZ test only proves a
  `<comet>` root is refused as a `ComicInfo.xml`.
- **PAX headers.** SharpCompress handles them and no fixture carries one, though bsdtar
  and macOS `tar` emit PAX by default. Worth knowing that a PAX archive is read through
  library code this build does not exercise, and is written back as plain USTAR.
- `ZipCompressionMethods.ToName` names stored and deflate only; anything else renders as
  `method 12` in a diagnostic.

## Dependencies

- **Microsoft.NETFramework.ReferenceAssemblies** (in `Directory.Build.props`) —
  build-time only. Lets `net48` build without Visual Studio.
- **System.Memory** — `Span<T>` and `BinaryPrimitives` on `net48`.
- **SharpCompress** (MIT) — **ZIP writing, RAR and 7z reading, and TAR both ways.** The
  first three are not conveniences. On .NET Framework `System.IO.Compression` cannot
  emit a stored ZIP entry at all (`CompressionLevel.NoCompression` produces deflate at
  level 0, method 8, not method 0), which makes a spec-compliant EPUB impossible since
  `mimetype` must be stored. ZIP reading stays on `ZipArchive`. **It writes ZIP, TAR and
  GZip and nothing else**, which is why RAR and 7z go out to an archiver on the machine.
  TAR *is* a convenience — it was hand-rolled and was traded for ~600 fewer lines, at the
  cost of header fidelity and with three writer defects to work around; see
  **Containers**.
- **xunit** — tests only.

**The project is Apache-2.0.** MIT dependencies are fine; copyleft ones are not. Do not
add iText (AGPL) or any GPL library.

**Do not read calibre's MOBI code and do not port it.** `MetadataUpdater` in
`calibre/ebooks/metadata/mobi.py` does what `MobiDocument` does and is GPL-3.0.
`MobiDocument` is written from the published description of the PalmDB, MOBI and EXTH
layouts — the record table at offset 78, the MOBI header at record 0 offset 16, the
`EXTH` block at `16 + headerLength` when bit `0x40` of the EXTH flags is set. That is a
specification, not an implementation. If a MOBI question cannot be answered from the
format description, say so rather than going to look.

## Shell integration

Per-user, `HKCU`, no elevation, registered from the Settings form:

```
HKCU\Software\Classes\SystemFileAssociations\<.ext>\shell\EBookMetaEditorEdit
  (default)          = "Edit metadata"
  Icon               = "<exe>,0"
  MultiSelectModel   = "Player"
  \command (default) = "\"<exe>\" \"%1\""
```

- Use `SystemFileAssociations`, not `HKCU\Software\Classes\<.ext>`, which hijacks the
  user's default association. Never write to `HKLM`, and never touch
  `HKCU\...\Explorer\FileExts`, which is the user's choice of default app.
- An `IExplorerCommand` COM handler for the top-level Windows 11 menu is out of scope.
- Registration is opt-in per format group (ebooks / comics).
  `ShellRegistration.SupportedExtensions` is built from the registered formats'
  `IBookFormat.Extensions`, and the Settings form builds its checkboxes from that, so
  the list exists in no second place.
- `.fb2.zip` is deliberately absent: `SystemFileAssociations` keys on a single
  extension, so registering it would mean registering `.zip` and putting this verb on
  every archive on the machine. `Fb2Format` still declares it, because the file dialog
  wants it; `SupportedExtensions` drops every compound extension, which is where that
  reason belongs.
- `MultiSelectModel = "Player"` asks Explorer to invoke the verb **once** with the whole
  selection. It is a request, not a guarantee — Explorer still falls back to one process
  per file, and hides the verb past its own item limit (around fifteen).
  `SingleInstance` covers the fallback; Open-folder and drag-and-drop cover the limit.

## Startup budget

Cold launch to visible, populated window: **under 400 ms** for a 5 MB file. This is a
product requirement — the whole point is right-click, fix, close.

- No DI container, no reflection-heavy configuration, no logging framework on the hot
  path. `Log` is a static list behind a lock.
- **Logging must not touch the disk on a clean run.** `Log.FilePath` is set at launch
  but nothing is opened; the file appears only once a warning or worse is logged, and
  then carries the whole session. An eager open can cost an antivirus scan.
- **Opening a file costs one open, not two.** `BookFormats.TryOpen` shares one container
  with every format and hands it to the winner still open.
- **Looking for an archiver happens where it is needed, not at launch**, so nothing but
  a CBR or CB7 save pays for it.
- **Almost nothing runs on open, which is most of why this is fast.** A read parses the
  metadata document and stops; the corrections all happen in `Write`, where the archive
  is being rebuilt anyway. Never decompress or hash entries on open.
  `DetectionTests.A_long_comic_opens_without_reading_its_pages` keeps this true.
- **Hold one copy of a document, not two.** A parsed document keeps the text it will
  splice back into and nothing more.
- **FB2 could most easily break this budget:** metadata and the whole book are one XML
  file with illustrations base64-encoded in, so ten megabytes is ordinary. `Fb2Format`
  locates `<description>`, parses that alone, and splices its serialised form back at
  the offsets it came from. Do not "simplify" it into a whole-document parse — that
  breaks the budget and invariant 15 in one change.
- MOBI reads record 0 and nothing else; the record table gives every other record's
  length without touching it.
- Decode the cover image off the UI thread.
- Single instance: a named mutex decides who is first; later launches hand their paths
  over a named pipe and exit. Both names are per-user and per-session. Forwarding
  failure is never fatal — a duplicate window is a smaller problem than a file the user
  asked for that never appeared.
- **The batch grid is exempt.** A folder of five hundred books cannot be read in 400 ms
  and must not pretend to be, so the grid shows its rows immediately and fills them in
  as reads complete.

## Style

- Nullable enabled, `TreatWarningsAsErrors=true` in Core.
- No `async void` outside event handlers.
- Core throws typed exceptions and never writes to the console.
- **No member exists for a caller that does not exist.** A property nothing reads, a
  parameter every call site defaults, an overload nobody calls: delete it.
- **No guard that cannot fire.** Argument checks belong on entry points a caller outside
  Core can reach, not on internal paths whose only caller already proved the condition.

### Comments are sparse. This is a hard rule.

- **Comment only what would otherwise read as a mistake** — a ZIP quirk, an encoding
  trap, a deliberate choice that looks wrong. Everything else has no comment. Never
  explain *what* the code does; the code says that.
- **One or two lines.** If a comment needs a paragraph, the reasoning belongs in this
  file, once, not repeated at the call site. No `<remarks>` essays.
- **Do not record history.** Neither a comment nor this file says what the code used to
  do, what was tried before, or what was removed. Write the rule that holds now.
- **A change to code must not arrive with a bigger change to comments.** If a diff is
  mostly comment churn, delete the comments and resubmit.
- Public Core API needs a doc comment or the build fails
  (`GenerateDocumentationFile` + `TreatWarningsAsErrors`). Satisfy it with a single
  `<summary>` line. Add `<param>` / `<returns>` only where the name does not already say
  it — and note `<param>` is all-or-nothing per member, or the build warns.
  `<exception>` for what a caller must catch. `<remarks>` almost never.

## Working style for Claude

- **Never touch a serialisation path while any round-trip or golden-byte test is red.**
  They are the only thing between a bug and a corrupted library.
- **Every format that writes has a byte-identity test, and it is the load-bearing one.**
  EPUB and CBZ reach it by reproducing the archive, CBT by re-emitting retained TAR
  headers, FB2 by splicing an edited `<description>` back into the original text, MOBI
  by returning record 0 untouched when nothing changed. If a new format cannot be given
  that test, the design is wrong, not the test.
- Prefer Core changes + tests over touching the UI. UI is the last step of a feature,
  not the first.
- When adding a format the order is builder → `TryOpen` → read → repairs → write, all in
  one new file under `Formats/`. Never write before round-trip reading is proven.
- **A new rule has to fix something.** A correction goes in `Write`, must be provable
  from the file alone, and must log what it changed.
- If a task would require breaking a hard invariant, stop and say so instead of finding
  a workaround.
