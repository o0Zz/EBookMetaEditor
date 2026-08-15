# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this project is

`EBookMetaEditor` — a fast ebook and comic metadata editor for Windows, launched
from the Explorer right-click menu.

1. **It repairs without being asked.** Broken XML is recovered on open, in memory.
   Disk bytes are untouched until the user saves.
2. **It fixes what it can prove and says nothing about the rest.** No Validate
   button, no findings panel — see **Repairs**.

## Format support

Two independent axes — **container** and **metadata document**. Conflating them is
the main design risk in this codebase.

| Format | Container | Metadata document |
|---|---|---|
| EPUB 2 / 3 | ZIP | OPF |
| CBZ | ZIP | `ComicInfo.xml` |
| CBT | TAR | `ComicInfo.xml` |
| FB2 | none (raw XML) | `<description>` |
| FB2.ZIP | ZIP | `<description>` |
| MOBI / PRC | PalmDB | EXTH |
| AZW / AZW3 | PalmDB | EXTH (one or two) |

All seven are writable. **A format that reuses an existing metadata document is a
container; one that needs a new document is a project.** `CbzFormat`, `Fb2Format`
and `MobiFormat` are each registered under two `FormatId`s, and none of them names
a container.

Comic archives may also carry CoMet (`comet.xml`) or a ComicBookLover JSON blob in
the ZIP comment. Read all three, write `ComicInfo.xml`, leave the others untouched.
`System.IO.Compression` cannot write a ZIP comment back, so `CbzFormat.Write` logs
`CBZ-W012` and throws rather than dropping the blob. It fires on write, not on
open — reading such a file is safe.

## Target and deployment

- **.NET Framework 4.8**: ships with Windows 10 (1903+) and 11, so the app runs on
  a clean machine. No Windows preinstalls the .NET 5+ runtime, so a modern target
  would either fail to launch or cost ~150 MB for a right-click utility.
- **A single executable**, `EBookMetaEditor.exe`. No CLI, no setup program;
  context-menu registration lives in the Settings form.
- **WinForms.** Never add `PublishAot` or `PublishTrimmed`. On `net48`
  `UseWindowsForms` does not apply — `EBookMeta.App` uses classic `<Reference>`
  items.
- The UI stays thin: a port to Avalonia must not touch `EBookMeta.Core`.

## Layout

```
src/EBookMeta.Core/      net48 — all logic. ZERO UI dependencies.
  README.md              where to start reading, and in what order
  IBookFormat.cs         seam 1 (metadata-document axis) + its whole vocabulary:
                         FormatId, MetadataField, FormatCapabilities, ReadOptions,
                         BookSource, FormatClaim, MatchConfidence
  IContainer.cs          seam 2 (physical axis) + its vocabulary: ContainerKind,
                         ContainerEntry, PendingEntry, ZipCompressionMethods,
                         SectionStream, ReadAllBytes
  BookFormats.cs         registry of seam 1 and the open path
  BookContainers.cs      factory for seam 2: Open / IsSupported / Sniff
  Book.cs                one open file: Load and Save
  BookExceptions.cs      BookFormatException, BookIoException, UnsupportedFormatException
  AtomicFileWriter.cs    the only sanctioned way a user's file is replaced
  BatchSession.cs        many files read, edited and saved together
  MetadataFields.cs      the text projection of a field, shared by both editors
  NaturalNameComparer.cs so 2.jpg sorts before 10.jpg
  Log.cs, Compat.cs
  Containers/            ZipContainer, TarContainer, PalmDbContainer, RawContainer
  Formats/               EpubFormat, CbzFormat (CBZ+CBT), Fb2Format (FB2+FB2.ZIP),
                         MobiFormat (MOBI/PRC + AZW/AZW3) — each holding its own
                         detection, read, write, repairs and metadata document
  Xml/                   XmlEncodingDetector, XmlSourceFormat, XmlExactWriter,
                         XmlLineIndex
  Model/                 BookMetadata, Creator, Identifier, SeriesInfo, CoverImage,
                         UnmappedField, BookDate (which owns date parsing)
src/EBookMeta.App/       net48 — WinForms, single instance, argv = paths
  Program, MainForm, BatchForm, SettingsForm, LogForm, AboutForm, Dialogs,
  AppSettings, ShellRegistration, SingleInstance, AppIcon, Strings,
  EmbeddedAssemblies
  Languages/             one key = value file per interface language
tests/EBookMeta.Core.Tests/  net48
  Builders/              synthetic file generators (see Test corpus)
```

Four layout rules:

1. **The two seams live at the Core root**, each with its registry beside it.
   Nothing outside `BookFormats` / `BookContainers` names a concrete
   implementation — `Book.Load` opens an `IContainer`, never a `ZipContainer`.
2. **One file per format, one per container**, and those folders hold nothing
   else, so `ls Formats/` answers "what does this build support". Format files are
   long (EPUB ~1900 lines); that is the accepted cost. Do not split one back out,
   and do not park a shared type in the folder.
3. **A shared type goes in the seam file for its axis**, decided by which registry
   consumes it, not by which folder is closest.
4. **A file gets a folder only when several files share a subject.** The test is
   how many formats call it: one means it belongs inside that format, however
   general its name sounds; two or more means `Xml/`, `Model/` or the root.

**Adding a format is one implementation plus one `BookFormats.Register` call.** It
brings its own `Extensions` — the source of the Settings form's context-menu list —
and its own `TryOpen`.

`EBookMeta.Core` referencing `System.Windows.Forms`, `System.Drawing` or any UI
package is a build-breaking error, enforced by `GuardCoreHasNoUiDependencies` in
`EBookMeta.Core.csproj`. Cover art crosses as `byte[]` plus a media type, never as
a `Bitmap`.

## Commands

```bash
dotnet build
dotnet test
dotnet build src/EBookMeta.App -c Release
```

There is no CLI, so **the xunit corpus is the entire verification surface**. Every
new Core capability gets tests in the same change.

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

It knows nothing about books. `ContainerEntry` carries name, length and
**compression method as read**, so `Rebuild` can reproduce it — `ZipArchiveEntry`
does not expose that, so `ZipContainer` parses the central directory itself and
pairs it with `ZipArchive` **by index**, because ZIP names are not unique.

`ContainerEntry` / `PendingEntry` are a read/write pair; content is a `Func<Stream>`
so a rebuild streams rather than holding a 300-page comic in memory.
`PendingEntry.Source` points back at the entry being reproduced — **use
`PendingEntry.Replacing`, not `FromBytes`, whenever new content stands in for an
existing entry.** `FromBytes` is for content with no original; choosing it by
mistake silently discards what the source container was holding on to.

- `RawContainer` presents a bare `.fb2` as a container of one entry named after the
  file. That small lie is what lets `Fb2Format`, `Book`, `AtomicFileWriter` and the
  batch grid not care whether it is a loose file or a ZIP member.
- `PalmDbContainer` exposes numbered PalmDB records as `record0`, `record1`, … It
  refuses a rebuild whose record *count* differs from the source, because record
  numbers are referenced from inside the file and this build cannot find every such
  pointer. Resizing a record is fine and recomputes the offset table.
- `TarContainer` retains each entry's raw 512-byte header blocks and re-emits them
  byte for byte, patching only the length and checksum of the entry that changed —
  which preserves the mode, uid, gid, uname and gname this build has no field for.
  **Do not replace it with SharpCompress's `TarWriter`**: it takes only a name, size
  and timestamp, and pads with two zero blocks where `tar` pads to ten kilobytes, so
  every save would rewrite every header. Reading TAR is hand-rolled for the same
  reason — the retained headers must come from somewhere.

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

No `Validate`: reading reports what it noticed and writing reports what it
corrected, so there is nowhere for one to live. Implementations are **stateless
singletons** — the registry hands the same instance to every caller, including
parallel batch threads. Note what is *not* in `Write`'s signature: no source path.
No format touches the user's file; it produces a complete new file at a path
`AtomicFileWriter` supplies.

`FormatCapabilities` declares which model fields a format can store, and **the UI
reads it to disable fields** so a user never types into a box whose content will be
discarded. Adding a model field means updating every format — intentional friction.

### Opening a file: every format is asked

```csharp
using BookSource? source = BookFormats.TryOpen(path, out DetectedFormat detected);
```

The file is opened **once** and the same `IContainer` is shared with every format
asked. On success it stays open and `Book.Load` reads straight through it.

**`TryOpen` claims; it does not parse, and it never throws.** A format checks the
marker that identifies it and nothing more. A damaged file is still that format's
file — an EPUB whose OPF will not parse is exactly what the repair path exists for,
and declining it would leave it claimed by nobody and reported as unsupported. An
exception would abandon the loop before the remaining formats are asked.
`DetectionTests.A_damaged_file_is_still_claimed_by_its_own_format` keeps this true.

### Detection

By content, never by extension — in real collections a `.cbz` that is really RAR is
common. Two passes; which one a check belongs to is decided by whether more than
one format shares the answer.

**`BookContainers.Sniff`, on the first 8 KB:** `PK\x03\x04` → ZIP; `Rar!\x1a\x07`
→ RAR; `7z\xBC\xAF\x27\x1C` → 7z; `ustar` at offset 257 → TAR; `BOOKMOBI` or
`TEXtREAd` at offset 60 → PalmDB; anything else → Raw.

**`IBookFormat.TryOpen`, each format with the shared `BookSource`:**

| Format | Claims on | Confidence |
|---|---|---|
| EPUB | ZIP with a `mimetype` entry reading `application/epub+zip` | Certain |
| EPUB | ZIP whose `mimetype` is wrong, compressed or misplaced | Strong |
| CBZ | ZIP holding `ComicInfo.xml` or `comet.xml` | Strong |
| CBZ | ZIP of nothing but images — the ComicRack convention | Weak |
| CBT | TAR, which no other supported format uses | Strong |
| FB2.ZIP | ZIP holding a `.fb2` entry | Strong |
| FB2 | a `<FictionBook` root element in the first 2 KB of text | Certain |
| MOBI | PalmDB | Strong |

The strongest `MatchConfidence` wins, so the answer never depends on registration
order. The second EPUB row is the important one: **refusing to open files you know
how to repair is the failure mode to watch for here.** A `mimetype` that is wrong,
compressed, misplaced or unreadable inline still claims the file one step down,
because that is precisely what `EPUB-E040` corrects on save.

FB2 is the only format recognised from text, because it has no magic number. The
search is bounded to the first 2 KB and gated on a leading `<`, so it costs nothing
for non-XML and opens no `RawContainer` to decline a file.

Two answers no format can give, both in `BookFormats`: **which container the bytes
are** (`BookContainers.Sniff`, run first), and **"recognised but not openable"** —
RAR → CBR, 7z → CB7, `%PDF-` → PDF. Saying a `.cbz` is really a RAR (`GEN-W002`,
`GEN-W004`) is a headline feature; naming a format costs a few magic-number
comparisons, supporting it costs a container and a document.

Never decompress an entry to decide what a file is. Beyond the 8 KB header a format
may read **entry names** (already supplied by the ZIP central directory) and at
most one entry's content — the EPUB `mimetype`, twenty stored bytes.

### Metadata model

`BookMetadata` covers the common 80%: title, sort title, creators (name, sort name,
role), series + index, description, publisher, dates, language, subjects,
identifiers, rights, cover.

- **Never lose a field you do not understand.** For XML formats this is achieved by
  *not touching the node*: the document retains the parsed tree and mutates only
  elements a field actually changed, so an unrecognised `<meta>` survives because
  nothing went near it. `UnmappedFields` records such fields for the UI; it is not
  the preservation mechanism.
- **Role mapping is lossy and that is accepted.** `ComicInfo`'s Writer / Penciller
  / Inker / Colorist / Letterer / CoverArtist do not map cleanly onto MARC
  relators. Keep the native role string alongside the mapped one and prefer it when
  writing back to the originating format.

### Editing many files at once

`BatchSession` uses the same machinery as the single-file window: one
`AtomicFileWriter.Write` per file, container reopened inside the callback. There is
no batch write path. A row is a `Book`; `SaveOne` is a call to `Book.Save`.

- **No transaction across files.** Twenty files are twenty independent saves; one
  failure leaves its own file untouched and says why on its own row.
- **A file is written because it is ticked, and editing it ticks it.** `BatchEntry`
  snapshots every field's text on read, so `IsDirty` means "differs from disk". But
  dirtiness alone was the wrong gate: a repair found on open lives in memory and
  changes no field, so the file that most needed saving was the one the grid
  refused to save. Hence `WillSave => IsWritable && (IsDirty || SaveRequested)`,
  with `SaveRequested` as the grid's leftmost column. It only ever *adds* files —
  an edited row is ticked and its box locked, so no click can drop an edit.
- **Covers are not read** (`ReadOptions.WithoutCover`).
- **Capabilities gate per cell, not per window**, so `Sort title` is dead on a
  comic and live on a book in the same column. `BatchEntry.Apply` refuses a field
  the format cannot store even if a caller asks, and every refusal is counted and
  reported — "pasted into 27 cells; 3 could not store it".
- `Load` reads only `Pending` entries, so adding files later is cheap and calling
  it twice cannot discard unsaved edits. There is deliberately no reload.

**Both editors share `MetadataFields`** — authors split on semicolons, subjects on
commas, a date kept as the characters the file used, a sort name carried forward
only when its author did not change. Its early-return checks are what make "open a
file and save it without editing" byte-identical; a second implementation would
keep that for one editor and quietly lose it for the other.

## Hard invariants

Not style preferences. Violating these corrupts users' libraries.

**Writing, all formats**

1. **Never modify in place.** Build into a sibling `.tmp`, then `File.Replace` with
   a `.bak`. `AtomicFileWriter` is the only sanctioned path.
2. **Never open an archive with `ZipArchiveMode.Update`.**
3. Entries other than the metadata document are copied **byte for byte**. Never
   round-trip XHTML, CSS or images through a parser.
4. Reject and report absolute paths and `..` traversal in entry names and manifest
   hrefs rather than following them.
5. **Round-tripping a valid file is a no-op**: open, save unedited, get identical
   bytes. There is a test per format; keep them green. An *invalid* file
   round-trips to a corrected one, and that is the point — the property to protect
   is "saving does not gratuitously rewrite", not "saving never changes anything".
   A change with no logged rule behind it is the bug.

   *Accepted limitation, ZIP only:* `System.IO.Compression` does not preserve ZIP
   extra fields, timestamps or the archive comment, so byte-identity holds for
   archives whose structure can be reproduced (every fixture) and may not for
   third-party files carrying extra fields. Do not hand-roll a ZIP writer. TAR has
   no such caveat — a header is 512 bytes of octal ASCII with no index to keep
   consistent, so `TarContainer` reproduces a `tar`-written archive exactly.

**EPUB**

6. **`mimetype` is the first entry, stored uncompressed**, containing exactly
   `application/epub+zip` — no trailing newline, no BOM. Readers reject files that
   get this wrong.
7. **Write both EPUB 2 and EPUB 3 conventions on save**, regardless of the declared
   `package/@version`. This is what calibre does and the only way to be read
   correctly by both old and new readers.

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
10. **Detect the real encoding from the bytes** (BOM, then declaration, then UTF-8)
    and flag mismatches. Do not trust `XDocument` to have guessed right.
11. Never invent namespace prefixes. Reuse the prefixes bound in the source;
    `opf:` and `dc:` are conventional, not guaranteed.

**Repair**

12. Original bytes of every parsed document are retained for the session.
13. **A repair never writes a file by itself.** `Book.Load` recovers in memory,
    `Book.Save` persists, and there is nothing in between. There is deliberately no
    repair-specific write path, which makes "the file on disk is what the user last
    saved" true by construction. Every repair is logged as a warning, but it does
    not interrupt the user to ask.
14. Recovery uses a tolerant parse (`XmlTextReader` with `Namespaces = false`,
    `DtdProcessing.Ignore`, `XmlResolver = null`). A recovered document that still
    fails to parse is reported as unrepairable — do not guess further, and do not
    apply a partial repair.
15. **A repair is an edit, not a reserialisation.** Repairs are offset-and-length
    edits against the original text, so everything outside the edited span is
    copied byte for byte. The tolerant parse is for *diagnosis*; it must not become
    the thing that writes the file. Re-emitting through a strict writer does fix
    the document, and rewrites every line to do it.
16. **Never infer what a name means.** Supplying a missing namespace URI is
    legitimate only for prefixes fixed by a published specification;
    `EpubFormat.KnownNamespaces` is that list, and a prefix absent from it is
    reported, never bound.
17. Diagnosis reads the markup, never a framework exception message.
    `XmlException` text is localised, so a regex over "'opf' is an undeclared
    prefix" works on an English machine and silently stops matching elsewhere.

## Repairs

**This tool fixes; it does not lecture.** No Validate button, no findings panel, no
`Finding` type.

> **A rule earns its place only if it changes the file.** If all it can do is
> report, it does not go in. That is a deliberate reversal of an earlier design
> with ~45 read-time rules; they were deleted, not disabled, and re-adding one
> needs a better reason than "epubcheck flags it".

The test is *provable, or merely likely?* A page count recomputed from the images
present is provable. A missing `dc:title` is not — nothing in the file says what it
should have been — so there is no such rule.

### Corrections

Provable from the file alone, and logged as a warning through `Log.Rule`.

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
| CBZ-W012 | The archive carries a ZIP comment a rebuild cannot write back |
| FB2-F001 / F002 | Not well-formed, or no `<description>` to edit |
| MOBI-F001 | No MOBI header in record 0 |
| MOBI-F002 | DRM-encrypted — rewriting the header produces a file no reader opens |
| GEN-W002 | The extension disagrees with the content |
| GEN-W004 | The format is recognised but unsupported |

`ZipContainer` also refuses outright when the central directory and `ZipArchive`
disagree on entry count — rebuilding would risk pairing the wrong compression
method onto the wrong entry. It throws `BookFormatException` without logging a rule.

**GEN-E003 is the one rule that only reports, and invariant 5 is why it is allowed
to.** An entry name that is absolute or contains `..` is logged by `Book.Load` and
nothing else happens: no correction, because nothing says what the name should have
been, and no refusal, because Core never extracts to disk. It is the exception the
invariant names, not a door left open.

**MOBI-W020 was considered and rejected.** When the two halves of a joint file
disagree, neither is provably right, and copying the KF8 half over the MOBI 6 one
would delete every field only the older half carries. So a save propagates *the
fields the user edited* and nothing else: `MobiFormat.Merge` overlays the difference
between what `Read` handed out and what came back onto each header's own metadata.
Applying the edited `BookMetadata` wholesale to both headers is the obvious
implementation and is wrong — it turns an unedited save of a mismatched file into
data loss, which
`MobiTests.Saving_a_joint_file_does_not_overwrite_one_half_with_the_other` catches.

**Where a repair lives:** in the format's own file, in `Write`, next to the other
corrections — `RepairMimetype` and `RepairNcxIdentifier` are the models to copy.
EPUB-W070 is the one repair that happens on *open* instead, because a document that
will not parse cannot be read at all otherwise.

## Logging

`Log.Info` progress · `Log.Warning` anything handled but notable, **including every
repair** · `Log.Error(message, exception)` failures · `Log.Rule` a repair or
refusal, rule ID first · `Log.Debug` detail that matters only after something went
wrong.

Core logs; Core never writes to the console and holds no opinion about
presentation. `LogForm` renders `Log.Entries` directly rather than reading the file
back.

## Interface language

`Strings` serves every piece of UI text from one `key = value` file per language in
`src/EBookMeta.App/Languages/` (`en`, `de`, `es`, `fr`, `it`), embedded in the exe.
`en.lang` is the master and the per-key fallback, so a half-finished translation
shows English rather than raw key names.

- **Not .resx.** Satellite assemblies are DLLs in subfolders and this app is one
  file. A plain text file is also something a translator can open.
- **Adding a language is adding a file.** The picker is built from what is
  embedded and the csproj globs `Languages\*.lang`. `WithCulture=false` on that
  item is load-bearing — without it MSBuild builds a satellite assembly.
- **The log stays English, rule IDs included.** Core knows nothing about the
  interface language and must not learn.
- **`Strings.Use` sets `CurrentUICulture`, never `CurrentCulture`.** Core parses
  and writes metadata using the latter; a date that round-tripped differently
  because the window is in German would be the interface reaching the user's file.
- **Two plural forms**, `key.one` and `key.many`, via `Strings.Plural`.
- **Lay windows out with panels, not coordinates.** German runs about a third
  longer than English.

## Test corpus

Small and synthetic. **Never commit a real copyrighted book or comic.** Fixtures
are generated by builders in `Builders/` — one XHTML page and a 1×1 PNG cover for
ebooks, three 1×1 PNGs for comics — written to a temp directory at test time, so no
binaries are committed. Broken fixtures are named after the rule they trigger, e.g.
`broken-epub-w070-undeclared-prefix.epub`.

`CbzBuilder.WriteTo` takes a `ContainerKind`, so one set of comic fixtures serves
CBZ and CBT — do not fork it into a second builder.

`RawTarBuilder` and `MobiBuilder` deliberately do *not* use `TarContainer` or
`PalmDbContainer`. They assemble bytes the way `tar` and kindlegen do — a mode, an
owner and a ten-kilobyte tail; a record table and an EXTH block with records this
build has no field for. **A fixture generated by the code under test cannot prove
that code reads real files**, and for these two formats that is the whole question.

Required coverage:

- byte-identical round-trip for every format
- a CBT whose headers carry a real archive's mode, uid, gid, uname and gname, and a
  blocking factor above the minimum — what keeps `TarContainer` honest
- a MOBI carrying EXTH records this build does not map, asserted to survive a write
- a MOBI whose header record is resized both ways, asserted to leave every later
  record readable
- a joint MOBI/KF8 file: read from the KF8 half, write an edit to both, leave each
  half's own unedited fields alone
- a DRM-encrypted MOBI, asserted to be refused
- an FB2 with a large body, byte-identical from `<body>` onwards after an edit
- the repair path: an undeclared prefix, an unknown prefix that must *not* be
  bound, an unclosed tag
- a compressed `mimetype`; a Latin-1 file declaring UTF-8
- `rar-disguised-as-cbz.cbz` — a format recognised but not supported
- a 300-page comic, for order preservation and to prove open does not read pages

Repair and write tests assert on exact resulting bytes, so an accidental reformat
fails loudly.

## Dependencies

- **Microsoft.NETFramework.ReferenceAssemblies** (in `Directory.Build.props`) —
  build-time only. Lets `net48` build without Visual Studio.
- **System.Memory** — `Span<T>` and `BinaryPrimitives` on `net48`.
- **SharpCompress** (MIT) — **ZIP writing only.** Not a convenience: on .NET
  Framework `System.IO.Compression` cannot emit a stored ZIP entry at all
  (`CompressionLevel.NoCompression` produces deflate at level 0, method 8, not
  method 0), which makes a spec-compliant EPUB impossible since `mimetype` must be
  stored. The identical code emits method 0 on .NET 5+. Reading stays on
  `ZipArchive`. SharpCompress also reads RAR/7z/TAR and writes TAR; none of that is
  used, and the package supporting a format is not a reason to route it there.
- **xunit** — tests only.

**The project is Apache-2.0.** MIT dependencies are fine; copyleft ones are not. Do
not add iText (AGPL) or any GPL library.

**MOBI brings the calibre licensing problem, so state it plainly.**
`MetadataUpdater` in `calibre/ebooks/metadata/mobi.py` does what `MobiDocument`
does and is GPL-3.0. `MobiDocument` was written from the published description of
the PalmDB, MOBI and EXTH layouts — the record table at offset 78, the MOBI header
at record 0 offset 16, the `EXTH` block at `16 + headerLength` when bit `0x40` of
the EXTH flags is set. That is a specification, not an implementation. **Do not
read calibre's MOBI code and do not port it.** If a MOBI question cannot be
answered from the format description, say so rather than going to look.

MOBI, FB2 and CBT together added no dependencies — each is a byte-level reader
written against a documented layout, the cheapest kind of format to add.

## Shell integration

Per-user, `HKCU`, no elevation, registered from the Settings form:

```
HKCU\Software\Classes\SystemFileAssociations\<.ext>\shell\EBookMetaEditorEdit
  (default)          = "Edit metadata"
  Icon               = "<exe>,0"
  MultiSelectModel   = "Player"
  \command (default) = "\"<exe>\" \"%1\""
```

Use `SystemFileAssociations`, not `HKCU\Software\Classes\<.ext>` — the latter
hijacks the user's default association. Never write to `HKLM`, and never touch
`HKCU\...\Explorer\FileExts`, which is the user's choice of default app. An
`IExplorerCommand` COM handler for the top-level Windows 11 menu is out of scope.

Registration is opt-in per format group (ebooks / comics).
`ShellRegistration.SupportedExtensions` is built from the registered formats'
`IBookFormat.Extensions` and the Settings form builds its checkboxes from that, so
the list exists in no second place. `.fb2.zip` is deliberately absent:
`SystemFileAssociations` keys on a single extension, so registering it would mean
registering `.zip` and putting this verb on every archive on the machine.

`MultiSelectModel = "Player"` asks Explorer to invoke the verb **once** with the
whole selection. It is a request, not a guarantee — Explorer still falls back to one
process per file, and hides the verb past its own item limit (around fifteen).
`SingleInstance` covers the fallback; Open-folder and drag-and-drop cover the limit.

## Startup budget

Cold launch to visible, populated window: **under 400 ms** for a 5 MB file. This is
a product requirement — the whole point is right-click, fix, close.

- No DI container, no reflection-heavy configuration, no logging framework on the
  hot path. `Log` is a static list behind a lock for exactly this reason.
- **Logging must not touch the disk on a clean run.** `Log.FilePath` is set at
  launch but nothing is opened; the file appears only once a warning or worse is
  logged, and then carries the whole session. An eager open can cost an antivirus
  scan.
- **Opening a file costs one open, not two.** `BookFormats.TryOpen` shares one
  container with every format and hands it to the winner still open.
- **Almost nothing runs on open, which is most of why this is fast.** A read parses
  the metadata document and stops; the corrections all happen in `Write`, where the
  archive is being rebuilt anyway. Never decompress or hash entries on open.
  `DetectionTests.A_long_comic_opens_without_reading_its_pages` keeps this true.
- **FB2 could most easily break this budget:** metadata and the whole book are one
  XML file with illustrations base64-encoded in, so ten megabytes is ordinary.
  `Fb2Format` locates `<description>`, parses that alone, and splices its serialised
  form back at the offsets it came from. Do not "simplify" it into a whole-document
  parse — that breaks the budget and invariant 15 in one change.
- MOBI reads record 0 and nothing else; the record table gives every other record's
  length without touching it.
- Decode the cover image off the UI thread.
- Single instance: a named mutex decides who is first; later launches hand their
  paths over a named pipe and exit. Both names are per-user and per-session.
  Forwarding failure is never fatal — a duplicate window is a smaller problem than
  a file the user asked for that never appeared.
- **The batch grid is exempt.** A folder of five hundred books cannot be read in
  400 ms and must not pretend to be, so the grid shows its rows immediately and
  fills them in as reads complete.

## Style

- Nullable enabled, `TreatWarningsAsErrors=true` in Core.
- No `async void` outside event handlers.
- Core throws typed exceptions and never writes to the console.
- Comments explain *why*, especially around ZIP and encoding — they look like
  mistakes to anyone who has not been bitten.
- Public Core API documented. `GenerateDocumentationFile` plus
  `TreatWarningsAsErrors` means a missing doc comment fails the build.

## Working style for Claude

- **Never touch a serialisation path while any round-trip or golden-byte test is
  red.** They are the only thing between a bug and a corrupted library.
- **Every format has a byte-identity test, and it is the load-bearing one.** EPUB
  and CBZ reach it by reproducing the archive, CBT by re-emitting retained TAR
  headers, FB2 by splicing an edited `<description>` back into the original text,
  MOBI by returning record 0 untouched when nothing changed. If a new format cannot
  be given that test, the design is wrong, not the test.
- Prefer Core changes + tests over touching the UI. UI is the last step of a
  feature, not the first.
- When adding a format the order is builder → `TryOpen` → read → repairs → write,
  all in one new file under `Formats/`. Never write before round-trip reading is
  proven.
- **A new rule has to fix something.** A correction goes in `Write`, must be
  provable from the file alone, and must log what it changed.
- Resist scope creep back toward formats that are out of scope; they were removed
  deliberately.
- If a task would require breaking a hard invariant, stop and say so instead of
  finding a workaround.
