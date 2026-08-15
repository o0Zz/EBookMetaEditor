# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this project is

`EBookMetaEditor` — a fast ebook and comic metadata editor for Windows, launched from
the Explorer right-click menu. Two things make it different from calibre,
Sigil, ComicTagger or epub-metadata-editor:

1. **It repairs, without being asked.** Broken XML is recovered on open, so a file
   that no other tool will load becomes editable. The correction lives in memory:
   the bytes on disk are untouched until the user saves, and saving writes the
   correction along with their edits.
2. **It fixes what it can prove, and says nothing about the rest.** A save
   recomputes a comic's page count from the images actually present, puts an EPUB's
   `mimetype` back where the specification requires it, and brings a stale table of
   contents back into line with the package. It deliberately does *not* enumerate
   everything that is wrong with a file — a checker that reports forty defects and
   repairs none is a worse tool than one that quietly fixes four. See **Repairs**.

Non-goals: reading books, editing content/XHTML/CSS, format conversion, DRM,
library management, page-image processing. If a feature request drifts toward
"become calibre", push back.

## Format support

Two independent axes — **container** and **metadata document**. Keep them
separate; conflating them is the main design risk in this codebase.

| Format | Container | Metadata document | Write |
|---|---|---|---|
| EPUB 2 / 3 | ZIP | OPF | yes |
| CBZ | ZIP | `ComicInfo.xml` | yes |
| CBT | TAR | `ComicInfo.xml` | yes |
| FB2 | none (raw XML) | `<description>` | yes |
| FB2.ZIP | ZIP | `<description>` | yes |
| MOBI / PRC | PalmDB | EXTH | yes |
| AZW / AZW3 | PalmDB | EXTH (one or two) | yes |


The table has two axes for a reason, and the additions show it. CBT reuses
`ComicInfo.xml` and every comic rule unchanged, so it cost one `IContainer` and
three lines of registration. FB2.ZIP reuses the FB2 document over the ZIP container
already there and cost nothing but a registration. MOBI cost both halves at once.
**A format that reuses an existing metadata document is a container; a format that
needs a new one is a project.** Each of `CbzFormat`, `Fb2Format` and `MobiFormat` is
registered twice, once per `FormatId`, and none of them names a container.

**Explicitly out of scope**, say so rather than attempting: CBR, CB7, KFX, AZW4,
PDF, LIT, PDB, RB, DjVu, audiobook formats.

Do not add one of these because it "looks close to" something supported. PDF needs
incremental update, KFX is a proprietary Ion container, LIT has no implementation
outside GPL projects, and `.pdb` is not one format but a family of them — PalmDoc,
eReader, Plucker — that happen to share a container with MOBI. Each is a project of
its own, and none is in scope.

**CBR and CB7 are the ones to keep saying no to**, because they look like the CBT
change and are not. SharpCompress reads RAR and 7z and writes neither: RAR
compression is proprietary and the UnRAR licence forbids building a compatible
compressor, and no 7z writer ships here. Either one would open into an editor that
cannot save, which is not this product. Their fixtures could not be generated
either, so the corpus rule would break with them.

**Detection is not support, and the difference matters.** `BookContainers.Sniff`
recognises RAR and 7z, and `BookFormats` names them and PDF, and both must keep
doing so — they are the answers no registered format is there to give. A
`.cbz` that is really a RAR archive is extremely common, and telling the user
that (`GEN-W002`) is one of this tool's headline features. Recognising a format
well enough to name it costs a few magic-number comparisons; supporting it costs
a container implementation. Name them; do not open them.

Secondary metadata conventions in comic archives, in priority order:
`ComicInfo.xml` (ComicRack schema — the de facto standard, used by Komga,
Kavita, ComicTagger), then CoMet (`comet.xml`), then the ComicBookLover JSON
blob in the ZIP archive comment. Read all three, write `ComicInfo.xml`,
preserve the others untouched.

Note on that last point: `System.IO.Compression` cannot write a ZIP archive
comment, so a CBZ carrying a ComicBookLover blob cannot be rebuilt with it
intact. **Resolved by refusing:** `CbzFormat.Write` throws rather than saving
such a file, and `CBZ-W012` reports the blob on open. Losing a user's
ComicBookLover metadata to a title edit is not a trade this tool makes on their
behalf, and a warning they can dismiss is not consent.

## Target and deployment

- **.NET Framework 4.8**, which ships with Windows 10 (1903+) and Windows 11.
  Nothing to install: the app runs on a clean machine. This is why 4.8 rather
  than modern .NET — no version of Windows preinstalls the .NET 5+ runtime, so a
  framework-dependent build would fail to launch and a self-contained one would
  cost ~150 MB for a right-click utility.
- **A single executable.** `EBookMetaEditor.exe` and nothing else. There is no CLI and
  no separate setup program; context-menu registration lives in the app's own
  Settings form.
- Projects are SDK-style and build with any recent .NET SDK via the
  `Microsoft.NETFramework.ReferenceAssemblies` package — no Visual Studio and no
  targeting pack required.
- **WinForms** for the UI. Never add `PublishAot` or `PublishTrimmed`.
- The UI must stay thin and replaceable. A future port to Avalonia or Rust must
  not require touching `EBookMeta.Core`.

## Layout

```
EBookMeta.sln
src/
  EBookMeta.Core/       net48          — all logic. ZERO UI dependencies.
    IBookFormat.cs      ── seam 1: the metadata-document axis.
                           TryOpen / Read / Write / Extensions, plus the
                           vocabulary they are spoken in: BookSource,
                           FormatClaim, MatchConfidence
    IContainer.cs       ── seam 2: the physical axis. Entries / OpenRead / Rebuild,
                           plus the vocabulary it is spoken in: ContainerEntry,
                           PendingEntry, SectionStream, ReadAllBytes
    BookFormats.cs         registry of seam 1, and the open path: Register / For /
                           TryOpen / Identify / FromExtension + DetectedFormat
    BookContainers.cs      factory for seam 2: Open / IsSupported / Sniff
    Book.cs                one open file: Load and Save, and what they noticed
    BookExceptions.cs      BookFormatException, BookIoException, UnsupportedFormatException
    AtomicFileWriter.cs    the only sanctioned way a user's file is replaced
    BatchSession.cs        many files read, edited and saved together
    MetadataFields.cs      the text projection of a field, shared by both editors
    NaturalNameComparer.cs so 2.jpg sorts before 10.jpg
    Log.cs                 the session log: Debug / Info / Warning / Error / Rule
    Compat.cs              everything net48 lacks, in one file
    Containers/        one file per container, and nothing else:
                       ZipContainer, TarContainer, PalmDbContainer, RawContainer
    Formats/           one file per format, and nothing else — each holding its
                       detection, read, write, rules and metadata document:
                       EpubFormat (+ OPF, container.xml, xmlns repair),
                       CbzFormat (CBZ + CBT, + ComicInfo.xml),
                       Fb2Format (FB2 + FB2.ZIP, + the FictionBook document),
                       MobiFormat (MOBI/PRC + AZW/AZW3, + the EXTH document);
                       then the small shared vocabulary they are asked in:
                       FormatCapabilities, FormatId, ReadOptions
    Xml/               the plumbing XDocument makes necessary, shared by every
                       XML format: XmlEncodingDetector, XmlSourceFormat,
                       XmlExactWriter
    Model/             BookMetadata, Creator, Identifier, SeriesInfo, CoverImage,
                       BookDate (which owns date parsing, because more than one
                       format needs it)
  EBookMeta.App/        net48          — WinForms, single instance, argv = paths
                       MainForm, BatchForm, SettingsForm, LogForm, AboutForm,
                       Dialogs (the chrome all four share),
                       ShellRegistration, SingleInstance, AppIcon,
                       Strings, EmbeddedAssemblies
    Languages/         one key = value file per interface language
tests/
  EBookMeta.Core.Tests/ net48
    Fixtures/          golden expected-byte files only
    Builders/          synthetic file generators (see Test corpus)
```

**Two interfaces define the architecture, and they live at the Core root** beside
`Book.cs` so the shape is legible from a directory listing: `IBookFormat` for the
metadata-document axis, `IContainer` for the physical one. Each has a registry
next to it — `BookFormats` and `BookContainers` — and nothing outside those two
files names a concrete implementation. `Book.Load` opens an `IContainer`, never a
`ZipContainer`. Keep it that way; an abstraction the spine bypasses is decoration.

**One file per format, one file per container. This is the layout rule that
matters most, and it is not about tidiness.** `ls Formats/` is the answer to "what
does this build support", and `ls Containers/` is the answer to "how does it read
them". A format that spilled across `EpubFormat.cs`, `EpubFormat.Rules.cs`,
`OpfDocument.cs`, `ContainerXml.cs` and a sniffer in a sixth file made both
questions require a search, and made "what does EPUB depend on" unanswerable
without reading five files. Each format file is now long — EPUB is around 2500
lines — and that is the accepted cost. **Do not split one back out.** If a format
file feels unwieldy, the fix is `#region`-free ordering and good headers within
it, not a second file.

**A file gets a folder only when several files share a subject.** `Xml/` holds the
three helpers every XML format needs and `Model/` the metadata types; both earn
their folder. `Documents/` did not survive: once each metadata document moved into
its format, what was left was the XML plumbing, so the folder was renamed to say
what it actually holds. A directory holding one file is pure navigation cost.

**The test for where something lives: how many formats call it?** One means it is
that format's code, however general the name makes it sound — `NamespaceRepair.cs`
sat at the Core root looking like general XML recovery when every prefix in its
table was an EPUB prefix and every rule it answered was an `EPUB-` rule. Two or
more means it is shared and belongs in `Xml/`, `Model/` or at the root —
`BookDate.Parse` is there because EPUB, ComicInfo and the editors all parse dates.
`ImageExtensions` went the other way: once the sniffer was gone, `CbzFormat` was
its only caller, so it moved inside.

**Adding a format is one implementation plus one line.** Implement `IBookFormat`,
call `BookFormats.Register`, done: nothing in the UI or the open path changes. The
format brings its own `Extensions` — which is where the Settings form's
context-menu list comes from — and its own `TryOpen`, so recognising its files is
part of implementing it rather than a second edit in a sniffer somewhere else.

### Opening a file: every format is asked

There is no sniffer. `BookFormats.TryOpen` offers the file to every registered
format and the strongest `FormatClaim` wins:

```csharp
using BookSource? source = BookFormats.TryOpen(path, out DetectedFormat detected);
```

`BookSource` is what makes that affordable. The file is opened **once** and the
same `IContainer` is handed to every format that looks at it, so asking seven
formats costs one header read and one container open — not one of each per format.
A format whose `ContainerKind` does not match declines without touching the
container at all. On success the container stays open and `Book.Load` reads
straight through it, which is one file open fewer than sniffing and then opening.

**`TryOpen` claims; it does not parse. This distinction is load-bearing.** A format
checks the marker that identifies it and nothing more. A damaged file is still that
format's file — an EPUB whose OPF will not parse is precisely the file the repair
path exists for, and declining it would leave it claimed by nobody and reported as
unsupported. Parsing happens in `Read`, after a winner is picked, where a failure
is a real error rather than a reason to try the next format.
`DetectionTests.A_damaged_file_is_still_claimed_by_its_own_format` exists to keep
that true. For the same reason `TryOpen` must never throw: an exception abandons
the loop before the formats after it are asked.

`MatchConfidence` settles overlaps — an EPUB's `mimetype` is `Certain`, a
`ComicInfo.xml` is `Strong`, an archive of nothing but images is `Weak` — so the
answer never depends on registration order.

**Two answers are still not available to any format**, because a format can only
ever say "that is mine". Both live in `BookFormats`:

- **Which container the bytes are.** Several formats share one, so the physical
  sniff belongs to `BookContainers.Sniff` — the same one-place-decides rule as
  `BookContainers.Open`, and it runs before any format is asked.
- **"This is a format we recognise and cannot open."** RAR, 7z and PDF have no
  registered format to speak for them, and saying a `.cbz` is really a RAR
  (`GEN-W002`, `GEN-W004`) is a headline feature. Naming a format costs a few
  magic-number comparisons; supporting it costs a container and a document.

`EBookMeta.Core` referencing `System.Windows.Forms`, `System.Drawing` or any UI
package is a build-breaking error, enforced by the
`GuardCoreHasNoUiDependencies` target in `EBookMeta.Core.csproj`. Cover art
crosses the boundary as `byte[]` plus a media type string, never as a `Bitmap`.

Note that WinForms is wired differently on `net48` than on modern .NET:
`UseWindowsForms` does not apply, and `EBookMeta.App` takes classic
`<Reference Include="System.Windows.Forms" />` items instead.

## Commands

```bash
dotnet build
dotnet test
dotnet build src/EBookMeta.App -c Release
```

There is no CLI. **The xunit corpus is the verification surface** — it is not a
side project. Every new Core capability gets tests in the same change, because
with no headless driver there is nothing else standing between a serialisation
bug and a corrupted library.

## Architecture rules

### Containers

`IContainer` exposes an ordered list of entries, byte access, and an atomic
rebuild. It knows nothing about books.

```csharp
interface IContainer : IDisposable {
    bool IsWritable { get; }
    IReadOnlyList<ContainerEntry> Entries { get; }   // order preserved
    Stream OpenRead(ContainerEntry entry);
    void Rebuild(IEnumerable<PendingEntry> entries, string targetPath);
}
```

`ContainerEntry` carries name, length, and **compression method as read**, so
`Rebuild` can reproduce it. `ZipArchiveEntry` does not expose the compression
method and `CompressedLength == Length` is not a sound substitute, so
`ZipContainer` parses the ZIP central directory itself and pairs it with
`ZipArchive` **by index** — ZIP does not guarantee unique entry names.

`ContainerEntry` and `PendingEntry` are a read/write pair — one describes an
entry as found, the other instructs a rebuild to produce one, and
`PendingEntry.CopyOf` turns the first into the second. `PendingEntry` supplies
content as a `Func<Stream>` so a rebuild streams entries through rather than
holding a 300-page comic in memory.

Four are implemented — ZIP, TAR, PalmDB and Raw — and `BookContainers.Open` is
where the choice is made. `IsWritable` stays on the interface because a read-only
container is a real concept the callers should keep handling.

`RawContainer` is the degenerate case and worth understanding before adding
anything like it: a bare `.fb2` is not an archive at all, so it is presented as a
container of exactly one entry named after the file. That is a small lie, and it
buys the whole design — `Fb2Format` does not know whether it is reading a loose
file or a ZIP member, and `Book`, `AtomicFileWriter` and the batch grid needed no
changes at all.

`PalmDbContainer` tells the same kind of lie for MOBI: PalmDB records are numbered
rather than named, so they are exposed as `record0`, `record1` and so on. It
refuses a rebuild whose record *count* differs from the source, because record
numbers are referenced from inside the file — the KF8 boundary, the first image
index — and this build cannot find every such pointer to fix it up. Resizing a
record is fine and recomputes the offset table; adding or removing one is not.

`PendingEntry.Source` points back at the entry a rebuild is reproducing, and is
how a container preserves what `ContainerEntry` does not model. Use
`PendingEntry.Replacing` rather than `FromBytes` whenever new content stands in
for an existing entry — `FromBytes` is for content that has no original, and
choosing it by mistake silently discards whatever the source container was
holding on to.

`TarContainer` retains each entry's raw 512-byte header blocks and re-emits them
byte for byte, patching only the length and checksum of the entry whose content
changed. That is what preserves the mode, uid, gid, uname and gname a real
`tar` records and this build has no field for. **Do not replace it with
SharpCompress's `TarWriter`**: it takes a name, a size and a timestamp and
nothing else, and finalises with two zero blocks where `tar` pads to ten
kilobytes, so every save would rewrite every header in the archive. Reading TAR
is hand-rolled for the same reason — the retained headers have to come from
somewhere.

### Formats

```csharp
interface IBookFormat {
    FormatId Id { get; }
    FormatCapabilities Capabilities { get; }
    BookMetadata Read(IContainer c, ReadOptions? o = null);
    void Write(IContainer c, BookMetadata m, string targetPath);
}
```

Two methods, not three. Reading reports what it noticed and writing reports what it
corrected, so there is nowhere for a `Validate` to live — see **Validation rules**.
Implementations are stateless singletons: the registry hands the same instance to
every caller, including four parallel batch threads. Note what is *not* in the
signature — no path. No format touches the user's file; writing produces a
complete new file at a path `AtomicFileWriter` supplies.

`FormatCapabilities` declares which model fields the format can store, and
whether writing is supported. **The UI reads this to disable fields**, so that
a user never types into a box whose content will be discarded. Adding a model
field means updating every format's capabilities — that is intentional
friction.

### Detection

Detection decides by content, never by extension. In collections, extensions lie
constantly — a `.cbz` that is really RAR is common.

It happens in two passes, and which pass a check belongs to is decided by whether
more than one format shares the answer.

**`BookContainers.Sniff` — the physical pass, on the first 8 KB:**

- `PK\x03\x04` → ZIP
- `Rar!\x1a\x07` (v4) or `Rar!\x1a\x07\x01\x00` (v5) → RAR
- `7z\xBC\xAF\x27\x1C` → 7z
- `ustar` at offset 257 → TAR
- PDB type+creator at offset 60: `BOOKMOBI` or `TEXtREAd` → PalmDB
- anything else → Raw

**`IBookFormat.TryOpen` — each format, asked with a shared `BookSource`:**

| Format | What it claims on | Confidence |
|---|---|---|
| EPUB | ZIP with a `mimetype` entry whose content is `application/epub+zip` | certain |
| EPUB | ZIP with a `mimetype` entry whose content is wrong or compressed | strong |
| CBZ | ZIP holding `ComicInfo.xml` or `comet.xml` | strong |
| CBZ | ZIP of nothing but images — the ComicRack convention | weak |
| CBT | TAR, which no other supported format uses | strong |
| FB2.ZIP | ZIP holding a `.fb2` entry | strong |
| FB2 | a `<FictionBook` root element in the first 2 KB of text | certain |
| MOBI | PalmDB | strong |

The strongest claim wins, so the answer never depends on registration order. What
is left in `BookFormats` is what no registered format can say: RAR → CBR, 7z → CB7,
`%PDF-` → PDF, all recognised and none supported. If extension and content
disagree, report it (`GEN-W002`).

Read at most the first 8 KB for the magic-number pass. Beyond that a format reads
the container's **entry names**, which the ZIP central directory has already
supplied, and at most one entry's content — the EPUB `mimetype`, twenty stored
bytes, because a CBZ can contain a file of that name too. Never decompress an
entry to decide what a file is.

The EPUB rows are worth reading twice. A `mimetype` that is compressed, misplaced
or wrong still claims the file, one step down in confidence, because those are
exactly the defects `EPUB-E040` describes and a save corrects. Refusing to open the
files you know how to repair is the failure mode to watch for here.

FB2 is the one format recognised from text rather than a container, because it has
no magic number: it is an ordinary XML file whose root element is the only thing
that distinguishes it. The search is bounded to the first 2 KB of `BookSource.Head`
and gated on the file starting with an angle bracket, so it costs nothing for
everything that is not XML — and no `RawContainer` is opened to decline a file.

**Inconclusive includes a `mimetype` whose bytes cannot be read inline** — a
compressed one, for instance. Falling back there rather than concluding "anonymous
ZIP" is what lets the tool open the one broken EPUB it can fix outright, since
`EPUB-E040` is corrected by storing the entry on save. Refusing to open the files
you know how to repair is the failure mode to watch for here.

### Metadata model

`BookMetadata` covers the common 80%: title, sort title, creators (name, sort
name, role), series + index, description, publisher, dates, language,
subjects, identifiers, rights, cover.

Two rules that matter more than the model itself:

- **Never lose a field you do not understand.** For XML formats this is achieved
  by *not touching the node* — `OpfDocument` retains the parsed tree and mutates
  only the elements a field actually changed, so an unrecognised `<meta>`
  survives because nothing went near it. `UnmappedFields` records such fields so
  the UI can show them; it is not the preservation mechanism. Extracting and
  re-serialising would risk reformatting an element the user never edited.
- **Role mapping is lossy and that is accepted.** `ComicInfo`'s Writer /
  Penciller / Inker / Colorist / Letterer / CoverArtist do not map cleanly onto
  MARC relators. Keep the native role string alongside the mapped one; when
  writing back to the originating format, prefer the native string.

### Editing many files at once

`BatchSession` is the batch equivalent of what the single-file window does, and
deliberately the same machinery underneath: one `AtomicFileWriter.Write` per
file, with the container reopened inside the callback. There is no batch write
path, because a second way to replace a user's file is the last thing this
codebase needs.

- **No transaction across files.** Twenty files are twenty independent saves. One
  that fails leaves its own file untouched, does not stop the other nineteen, and
  says why on its own row. Rolling back nineteen good writes because the
  twentieth was read-only would be worse behaviour, not better.
- **A file is written because it is ticked, and editing it ticks it.** `BatchEntry`
  snapshots the text of every field on read, so dirtiness is "differs from what is
  on disk" rather than "somebody touched this row"; typing a value and typing it
  back writes nothing, and a file nobody edited is not rewritten byte-identically —
  it is not opened. But dirtiness alone was the wrong gate, because a repair found
  on open lives in memory and changes no field, so the one file that most needs
  saving was the one the grid refused to save. `BatchEntry.SaveRequested` is the
  grid's leftmost column and `WillSave` is `IsWritable && (IsDirty ||
  SaveRequested)`, which is what `Save` consults. It only ever *adds* files: an
  edited row is ticked and its box locked, so no click can silently drop an edit.
  This is the same offer the single-file window makes by enabling Save on
  `CanSave` alone — the asymmetry between the two editors was the bug.
- **Covers are not read** (`ReadOptions.WithoutCover`). A grid of titles has no
  use for three hundred full-size images.
- **Capabilities gate per cell, not per window.** A row's format decides which of
  its cells are editable, so `Sort title` is dead on a comic and live on a book
  in the same column. `BatchEntry.Apply` refuses a field the format cannot store
  even if a caller asks, which is what stops a paste across a mixed selection from
  writing into files that would discard it. Every such refusal is counted and
  reported — "pasted into 27 cells; 3 could not store it" — because a user who
  selected thirty deserves to know.
- `Load` reads only `Pending` entries, so adding files later is cheap and calling
  it twice cannot discard unsaved edits by re-reading the files they were made
  against. There is deliberately no reload.
- A row is a `Book`, so the grid loads and saves through exactly the same code as
  the single-file window. `BatchEntry` adds only the baseline text that makes
  dirtiness meaningful; `SaveOne` is a call to `Book.Save`.

**Both editors share `MetadataFields`.** The rules for what a field looks like in
a box and what typing in it does to the model — authors split on semicolons,
subjects on commas, a date kept as the characters the file used, a sort name
carried forward only when its author did not change — live in Core, once. Those
early-return checks are what make "open a file and save it without editing"
byte-identical, and a second implementation would keep that property for one
editor and quietly lose it for the other.

## Hard invariants

Not style preferences. Violating these corrupts users' libraries.

### Writing, all formats

1. **Never modify in place.** Read the source fully, build into a sibling
   `.tmp`, then `File.Replace` with a `.bak`. A crash mid-write must never
   leave a truncated file. `AtomicFileWriter` is the only sanctioned path;
   no format opens the target for writing directly.
2. **Never open an archive with `ZipArchiveMode.Update`.**
3. Entries other than the metadata document are copied **byte for byte**.
   Never round-trip XHTML, CSS or images through a parser.
4. **Preserve entry order and per-entry compression method.** Do not re-sort,
   do not recompress. Detect stored vs deflated on read and reproduce it.
5. Reject and report absolute paths and `..` traversal in entry names and
   manifest hrefs rather than following them.
6. **Round-tripping a valid file is a no-op**: open it, save without editing, get
   identical bytes. There is a test per format; keep them green.

   An *invalid* file round-trips to a corrected one, and that is the point rather
   than a violation — saving is where a defect gets fixed. Every correction is
   logged, and every correction is provable from the file alone: a page count is
   recomputed from the images that are present, not guessed. So the property to
   protect is "saving does not gratuitously rewrite", not "saving never changes
   anything". A change with no logged rule behind it is the bug.

Accepted limitation, ZIP only: `System.IO.Compression` does not preserve ZIP extra
fields, original timestamps, or the archive comment. Round-trip byte-identity
therefore holds for archives whose structure can be reproduced — including every
builder-generated fixture — and may not for third-party files carrying extra
fields. Document it; do not hand-roll a ZIP writer.

TAR carries no such caveat, and the difference is worth understanding before
reaching for the same excuse twice. A ZIP writer has to reproduce compression,
CRCs and a central directory, so hand-rolling one is a project. A TAR header is
512 bytes of octal ASCII with no index to keep consistent, which is why
`TarContainer` reproduces a `tar`-written archive exactly — verified against GNU
tar 1.34 — while `ZipContainer` can only promise to reproduce its own.

### EPUB specifics

7. **`mimetype` is the first entry, stored uncompressed**
   (`CompressionLevel.NoCompression`), containing exactly `application/epub+zip`
   with no trailing newline and no BOM. Readers reject files that get this
   wrong.
8. **Write both EPUB 2 and EPUB 3 conventions on save**, regardless of the
   declared `package/@version`:

   | Field | EPUB 2 | EPUB 3 |
   |---|---|---|
   | file-as | `opf:file-as` attribute | `<meta refines="#id" property="file-as">` |
   | role | `opf:role` attribute | `<meta refines="#id" property="role" scheme="marc:relators">` |
   | series | `<meta name="calibre:series">` | `<meta property="belongs-to-collection">` + `collection-type` |
   | series index | `<meta name="calibre:series_index">` | `<meta refines property="group-position">` |
   | cover | `<meta name="cover" content="id">` | manifest item `properties="cover-image"` |

   This is what calibre does and the only way to be read correctly by both old
   and new readers.

### XML, all formats

9. Load with `LoadOptions.PreserveWhitespace`, save with
   `SaveOptions.DisableFormatting`. Changing a title must produce a one-line
   diff, not a reformat of the whole file.
10. Preserve the original XML declaration verbatim unless the user is
    explicitly fixing it. Capture it as literal source text — round-tripping
    through `XDeclaration` is not character-exact.
11. **Detect the real encoding from the bytes** (BOM, then declaration, then
    UTF-8 fallback) and flag mismatches. Do not trust `XDocument` to have
    guessed right.
12. Never invent namespace prefixes. Reuse the prefixes bound in the source.
    `opf:` and `dc:` are conventional, not guaranteed.

### Repair

13. Original bytes of every parsed document are retained for the session.
14. **A repair never writes a file by itself.** `Book.Load` recovers, `Book.Save`
    persists, and there is nothing in between. Recovery happens on open and is
    held in memory; it reaches the disk only through the ordinary save path,
    when the user asks for a save. There is deliberately no repair-specific
    write path, which is what makes "the file on disk is what the user last
    saved" true by construction rather than by care. Every repair is logged as a
    warning so the user can find out what changed, but it does not interrupt them
    to ask.
15. Recovery uses a tolerant parse (`XmlReader` with `CheckCharacters = false`,
    `DtdProcessing.Ignore`; `AngleSharp.Xml` as second-stage fallback). A
    recovered document that still fails validation is reported as unrepairable
    — do not guess further. A partial repair is not applied at all: handing back
    a document that still will not parse is worse than the original error.
16. **A repair is an edit, not a reserialisation.** Repairs are expressed as
    offset-and-length `TextEdit`s against the original text, so everything
    outside the edited span is copied through byte for byte. The tolerant parse
    is for *diagnosis*; it must not become the thing that writes the file.
    Parsing permissively and re-emitting through a strict writer does fix the
    document, and it rewrites every line to do it — so a user who opened a book
    to correct a typo would save a file in which nothing is where they left it.
17. **Never infer what a name means.** Supplying a missing namespace URI is only
    legitimate for prefixes fixed by a published specification —
    `EpubFormat.KnownNamespaces` is that list, and a prefix absent from it is
    reported, never bound. Inventing a plausible URI would fabricate metadata that was
    never in the file, and the user would have no reason to doubt it.
18. Diagnosis reads the markup, never the exception message. `XmlException`
    text is localised by the framework, so a regex over
    "'opf' is an undeclared prefix" works on an English machine and silently
    stops matching everywhere else.

## Logging

The window has **no findings panel**. Rules, repairs and failures go to the
session log, reachable from the **?** menu, which also holds the About box.

The reasoning: a metadata editor is used for twenty seconds at a time, and a
permanent panel that usually reads "nothing to report" is furniture. A log is
what you want *after* something looked wrong, not while you are typing a title.

- `Log.Info` for progress — a file opened, a file saved, entries written.
- `Log.Warning` for anything handled but notable, **including every repair**. A
  repair must never be silent: it is the one thing that changes a user's file
  without them asking.
- `Log.Error` for failures, and `Log.Error(message, exception)` to keep the type
  and message of the cause.
- `Log.Rule` for a repair or a refusal, which puts the rule ID first in the line.
- `Log.Debug` for detail that only matters once something has gone wrong.

Core logs; Core never writes to the console. `Log` holds no opinion about
presentation — the UI decides that, and `LogForm` renders `Log.Entries` directly
rather than reading the file back.

## Repairs

**This tool fixes; it does not lecture.** There is no Validate button, no findings
panel, no `Finding` type and no rule that exists only to tell the user something is
wrong. A checker that reports forty defects and repairs none is a worse product
than one that silently fixes the four it can prove — so the rule for adding
anything to this section is blunt:

> **A rule earns its place only if it changes the file.** If all it can do is
> report, it does not go in. That is a deliberate reversal of an earlier design
> which had ~45 read-time rules across the four formats; they were deleted, not
> disabled, and re-adding one needs a better reason than "epubcheck flags it".

The three exceptions are refusals, which are not reports: a file this build cannot
parse, or must not rewrite, throws.

### What actually gets corrected

Every one of these is provable from the file alone — no assumption, no guess — and
every one is logged as a warning, because a repair is the one thing that changes a
user's file without them asking and must never be silent.

| ID | When | What the save does |
|---|---|---|
| EPUB-W070 | The OPF uses a namespace prefix it never declares | Recovered in memory on open, persisted on save |
| EPUB-E040 | `mimetype` missing, compressed, or not first | Written back as the first entry, stored |
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
| FB2-F001 / F002 | Not well-formed, or no `<description>` to edit |
| MOBI-F001 | No MOBI header in record 0 |
| MOBI-F002 | The text is DRM-encrypted |
| GEN-W002 | The extension disagrees with the content |
| GEN-W004 | The format is recognised but unsupported |

**MOBI-F002 is a refusal, not a warning.** DRM is a non-goal, and rewriting the
header of an encrypted book produces a file no reader will open — so the read
throws rather than handing back metadata the user could try to save.

Also refused rather than warned about: a CBZ carrying a ZIP comment.
`System.IO.Compression` cannot write one back, so `CbzFormat.Write` throws instead
of dropping a user's ComicBookLover metadata on their behalf.

### Corrections that were considered and rejected

**MOBI-W020 — the two halves of a joint file disagreeing — is left alone on
purpose.** Neither half is provably right, and copying the KF8 one over the MOBI 6
one would delete every field the older half carries and the newer one does not. So
a save propagates *the fields the user edited* and nothing else: `MobiFormat.Merge`
overlays the difference between what `Read` handed out and what came back onto each
header's own metadata. Applying the edited `BookMetadata` wholesale to both headers
is the obvious implementation and is wrong — it turns an unedited save of a
mismatched file into data loss, which
`MobiTests.Saving_a_joint_file_does_not_overwrite_one_half_with_the_other` exists
to catch.

That is the shape of the test: *is the right answer provable, or merely likely?*
A page count recomputed from the images present is provable. A missing `dc:title`
is not — nothing in the file says what the title should have been — so there is no
EPUB-E012 any more.

### Where a repair lives

In the format's own file, in `Write`, next to the other corrections — `RepairMimetype`
and `RepairNcxIdentifier` are the models to copy. It reports what it changed through
`Log.Rule`, and it must be provable from the file alone.

The one repair that happens on *open* rather than on save is EPUB-W070, because a
document that will not parse cannot be read at all otherwise. It is recovered in
memory; the bytes on disk are untouched until the user saves. There is deliberately
no repair-specific write path, which is what makes "the file on disk is what the
user last saved" true by construction.

## Interface language

`Strings` serves every piece of text the windows show, out of one
`key = value` file per language in `src/EBookMeta.App/Languages/`, embedded in
the exe. English (`en.lang`) is the master and the per-key fallback, so a
half-finished translation shows English lines rather than raw key names.

- **Not .resx.** Satellite assemblies are DLLs in subfolders, and this
  application is one file. A plain text file is also something a translator can
  open, which a `.resx` is not.
- **Adding a language is adding a file.** Nothing lists them: the picker is
  built from what is embedded, and the csproj globs `Languages\*.lang`.
  `WithCulture=false` on that item is load-bearing — without it MSBuild reads
  `de.lang` as a culture-specific resource and builds a satellite.
- **The log stays English, rule IDs included.** It is a diagnostic, pasted into
  bug reports. Core knows nothing about the interface language and must not learn.
- **`Strings.Use` sets `CurrentUICulture`, never `CurrentCulture`.** Metadata is
  parsed and written by Core using the latter; a series index or a date that
  round-tripped differently because the window is in German would be the
  interface reaching the user's file.
- **Two plural forms**, `key.one` and `key.many`, via `Strings.Plural`. Enough
  for the languages shipped, and honestly not enough in general.
- **Lay windows out with panels, not coordinates.** German runs about a third
  longer than English; a fixed `Size` on a label turns that into a truncated
  sentence.

## Test corpus

`tests/EBookMeta.Core.Tests/` holds small, synthetic files. **Never commit a real
copyrighted book or comic.** Fixtures are generated by builders in `Builders/` —
one XHTML page and a 1×1 PNG cover for ebooks, three 1×1 PNGs for comics —
written to a temp directory at test time under the documented names, so no
binaries are committed. `Fixtures/` holds only golden expected-byte files, which
are stable by definition.

Broken fixtures are named after the rule they trigger:
`broken-epub-e020-dangling-idref.epub`, `broken-cbz-e020-pagecount.cbz`.

`CbzBuilder.WriteTo` takes a `ContainerKind`, so one set of comic fixtures serves
CBZ and CBT — do not fork it into a second builder.

`RawTarBuilder` and `MobiBuilder` are the exceptions, and deliberately do *not* use
`TarContainer` or `PalmDbContainer`. They assemble bytes the way `tar` and
kindlegen do — a mode and an owner and a ten-kilobyte tail, a record table and an
EXTH block with records this build has no field for — so that preserving all of it
is provable. **A fixture generated by the code under test cannot prove the code
under test reads real files**, and for these two formats that is the whole
question.

Required coverage:

- valid + byte-identical round-trip for every format
- a CBT whose headers carry a real archive's mode, uid, gid, uname and gname, and
  a blocking factor above the minimum — the test that keeps `TarContainer` honest
- a MOBI carrying EXTH records this build does not map, asserted to survive a write
- a MOBI whose header record is resized in both directions, asserted to leave every
  later record readable — the record table is the only thing that says where they are
- a joint MOBI/KF8 file, asserted to read from the KF8 half, to write an edit to
  both, and to leave each half's own fields alone when they were not edited
- a DRM-encrypted MOBI, asserted to be refused
- an FB2 with a large body, asserted byte-identical from `<body>` onwards after an
  edit
- `broken-unclosed-tag.epub`, `broken-bare-ampersand.epub` — repair path
- `broken-mimetype-compressed.epub`
- `latin1-declared-utf8.epub`
- `rar-disguised-as-cbz.cbz` — sniffing a format we recognise but do not support
- an EPUB with 500 manifest entries and a CBZ with 300 pages, for
  order-preservation and startup-budget tests

Repair and write tests are golden-file: assert on exact resulting bytes, so an
accidental reformat fails loudly.

## Dependencies

Each dependency must be justified against the startup budget and the licence
policy.

- **Microsoft.NETFramework.ReferenceAssemblies** — build-time only, contributes
  nothing at runtime. Lets `net48` build without Visual Studio.
- **System.Memory** — `Span<T>` and `BinaryPrimitives` on `net48`.
- **SharpCompress** (MIT) — **ZIP writing only.** Not optional, and not a
  convenience. On .NET Framework, `System.IO.Compression` cannot emit a stored
  ZIP entry at all: `CompressionLevel.NoCompression` produces deflate at level 0
  (method 8), not method 0. A spec-compliant EPUB is therefore impossible
  through the framework writer, because `mimetype` must be stored or readers
  reject the file. This is a genuine .NET Framework behaviour difference —
  the identical code emits method 0 on .NET 5+ — and it is why
  `ZipContainer.Create` exists and why the guidance below permits one archive
  dependency rather than none. Reading stays on `ZipArchive`.

  **ZIP writing only** is the whole scope of it, and the package supporting a
  format is not a reason to route that format through it. SharpCompress also
  reads RAR, 7z and TAR and writes TAR — none of which is used. TAR is
  hand-rolled because its writer cannot be faithful (see **Containers**); RAR and
  7z stay unsupported because it has no writer for them at all.
- **AngleSharp.Xml** (MIT) — second-stage tolerant XML parse. Load lazily,
  only when strict parsing has already failed, so it stays off the hot path.
- **xunit** — tests only.

**The project is Apache-2.0**, so every dependency must be compatible with it.
Do **not** add iText (AGPL) or any GPL-licensed library. MIT dependencies are fine;
copyleft ones are not.

**MOBI brings the calibre licensing problem back, so it needs stating plainly.**
`MetadataUpdater` in `calibre/ebooks/metadata/mobi.py` does exactly what
`MobiDocument` does and is GPL-3.0, which this project cannot take code from.
`MobiDocument` was written from the published description of the PalmDB, MOBI and
EXTH layouts — the record table at offset 78, the MOBI header at record 0 offset
16, the `EXTH` block at `16 + headerLength` when bit `0x40` of the header's EXTH
flags is set. That description is a specification, not an implementation, and
implementing a documented file format independently is exactly what is allowed.
**Do not read calibre's MOBI code, and do not port it.** If a MOBI question cannot
be answered from the format description, say so rather than going and looking.

Note that MOBI, FB2 and CBT together added no dependencies at all. Every one of
them is a byte-level reader written against a documented layout, which is the
cheapest kind of format to add and the reason the list below is still four items
long.

## Shell integration

Per-user, `HKCU`, no elevation, registered from the app's own Settings form —
there is no separate setup executable. For each supported extension:

```
HKCU\Software\Classes\SystemFileAssociations\<.ext>\shell\EBookMetaEditorEdit
  (default)        = "Edit metadata"
  Icon             = "<exe>,0"
  MultiSelectModel = "Player"
HKCU\Software\Classes\SystemFileAssociations\<.ext>\shell\EBookMetaEditorEdit\command
  (default) = "\"<exe>\" \"%1\""
```

Use `SystemFileAssociations`, not `HKCU\Software\Classes\<.ext>` — the latter
hijacks the user's default association. Registration is opt-in per format group
(ebooks / comics) so a user can tag comics without touching EPUB. Register only
`.epub`, `.cbz`, `.cbt`, `.fb2`, `.mobi`, `.prc`, `.azw` and `.azw3`; do not
register formats the app cannot open. That list is not written down anywhere:
`ShellRegistration.SupportedExtensions` is built from the registered formats'
`IBookFormat.Extensions`, and the Settings form builds its checkboxes from that —
so a new format reaches the context menu by declaring its own extensions and
nothing else changes.

`.fb2.zip` is deliberately not registered. `SystemFileAssociations` keys on a
single extension, so the only thing available to register would be `.zip` — which
would put this app's verb on every archive on the machine. Those files open by
drag-and-drop or through the Open dialog.

`MultiSelectModel = "Player"` asks Explorer to invoke the verb **once** with the
whole selection rather than once per file, which is what makes right-clicking
thirty comics open one window with thirty rows. It is a request, not a guarantee:
Explorer still falls back to one process per file, and hides the verb entirely
past its own item limit (around fifteen). The single-instance forwarding in
`SingleInstance` covers the fallback, and Open-folder and drag-and-drop cover the
limit — which is why all three exist rather than any one of them.

Never write to `HKLM`. Never touch `HKCU\...\Explorer\FileExts` — that is the
user's choice of default app. An `IExplorerCommand` COM handler for the
top-level Windows 11 menu is out of scope; appearing under "Show more options"
is acceptable.

## Startup budget

Cold launch to visible, populated window: **under 400 ms** for a 5 MB file.
This is a product requirement — the whole point is right-click, fix, close.

- No DI container, no reflection-heavy configuration, no logging framework on
  the hot path. `Log` is a static list behind a lock for exactly this reason —
  a provider model would cost more at startup than everything it logs.
- **Logging must not touch the disk on a clean run.** `Log.FilePath` is set at
  launch but nothing is opened; the file appears only once a warning or worse is
  logged, and then carries the whole session so far. Opening a file eagerly can
  cost an antivirus scan, which is real money against 400 ms.
- Sniff the container from the first 8 KB. Parse only the metadata document. Do
  not hash or decompress the whole archive on open.
- **Opening a file costs one open, not two.** `BookFormats.TryOpen` opens the
  container once and shares it with every format it asks, then hands it to the
  winner still open — so `Book.Load` never reopens the file it just identified.
  A `BookSource` per format, or an identify-then-open pair, would put that second
  open back.
- **FB2 is the format that could most easily break this budget, and the design is
  what stops it.** The metadata and the entire book are one XML file, illustrations
  base64-encoded into it, so a ten-megabyte document is ordinary and running it
  through `XDocument` would cost the budget several times over. `Fb2Document`
  therefore locates `<description>` and parses that alone, splicing its serialised
  form back at the offsets it came from on save. Do not "simplify" it into a whole
  document parse: that would break the startup budget and hard invariant 6 in the
  same change.
- MOBI reads record 0 and nothing else. The record table gives every other record's
  length without touching it, so a cover is only pulled when one was asked for.
- **Almost nothing runs on open, which is most of why this is fast.** A read parses
  the metadata document and stops. The corrections all happen in `Write`, where the
  archive is being rebuilt anyway and the cost is already paid — so opening a
  300-page comic costs what opening a one-page one costs, which
  `DetectionTests.A_long_comic_opens_without_reading_its_pages` keeps true.
  The thing to never do on open is decompress or hash entries.
- Decode the cover image off the UI thread.
- Single instance: a named mutex decides who is first, and later launches hand
  their paths over a named pipe and exit (`SingleInstance`). Both names are
  per-user and per-session. Forwarding failure is never fatal — the second
  process opens its own window, because a duplicate window is a smaller problem
  than a file the user asked for that never appeared.
- **The batch grid is exempt from the 400 ms budget**, and the exemption is the
  point of stating it: that budget is about right-clicking one file. A folder of
  five hundred books cannot be read in 400 ms and must not pretend to be, so the
  grid shows its rows immediately and fills them in as reads complete.

## Style

- Nullable enabled, `TreatWarningsAsErrors=true` in Core.
- No `async void` outside event handlers.
- Core throws typed exceptions (`BookFormatException`, `BookIoException`) and
  never writes to the console.
- Comments explain *why*, especially around ZIP and encoding — they look like
  mistakes to anyone who has not been bitten.
- Public Core API documented with XML doc comments. `GenerateDocumentationFile`
  plus `TreatWarningsAsErrors` means a missing one fails the build.

## Working style for Claude

- All seven formats are implemented. Never touch a serialisation path while the
  round-trip or golden-byte tests for any of them are red — they are the only thing
  standing between a bug and a corrupted library.
- **Every format has a byte-identity test, and that is the load-bearing one.** Each
  reaches it differently: EPUB and CBZ by reproducing the archive, CBT by re-emitting
  retained TAR headers, FB2 by splicing an edited `<description>` back into the
  original text, MOBI by returning record 0 untouched when nothing changed. If a new
  format cannot be given that test, the design is wrong, not the test.
- Prefer Core changes + tests over touching the UI. UI is the last step of a
  feature, not the first.
- When adding a format, the order is: builder → `TryOpen` → read → rules → write,
  all of it in one new file under `Formats/`. Never write before round-trip
  reading is proven.
- **`TryOpen` claims, it never parses and never throws.** Claiming a damaged file
  is the point — that is how it reaches the repair path instead of being reported
  as unsupported.
- **One file per format and per container.** Everything a format needs — its
  detection, its metadata document, its rules, its repairs — goes in its own file,
  so that `ls Formats/` answers "what is supported" and the file answers "what does
  it depend on". Do not split one back out because it got long; they are meant to
  be long.
- **A new rule has to fix something.** If all it can do is report, it does not get
  written — see **Repairs**. A correction goes in `Write`, must be provable from
  the file alone, and must log what it changed.
- When a change touches serialisation, run round-trip and golden-file tests
  first.
- Resist scope creep back toward the formats listed as out of scope. They were
  removed deliberately.
- If a task would require breaking a hard invariant above, stop and say so
  instead of finding a workaround.
