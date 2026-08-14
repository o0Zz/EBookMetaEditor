# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this project is

`EBookMetaEditor` — a fast ebook and comic metadata editor for Windows, launched from
the Explorer right-click menu. Two things make it different from calibre,
Sigil, ComicTagger or epub-metadata-editor:

1. **It validates.** Every file is checked for structural and semantic
   consistency before and after editing, with findings reported by stable rule
   ID, file, line and column.
2. **It repairs, quietly.** Broken XML and inconsistent metadata are recovered
   on open, so a file that no other tool will load becomes editable. The
   correction lives in memory: the bytes on disk are untouched until the user
   saves, and saving writes the correction along with their edits.

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

That is the whole list, and it is deliberately short. Both formats are ZIP, so
there is exactly one container implementation and one write path to get right.
Scope was cut to these two on purpose: everything else costs a new container, a
new metadata document, or both.

**Explicitly out of scope**, say so rather than attempting: CBR, CB7, CBT,
MOBI, PRC, AZW, AZW3, KF8, KFX, AZW4, FB2, PDF, LIT, PDB, RB, DjVu, audiobook
formats.

Do not add one of these because it "looks close to CBZ". CBR needs a RAR
reader, CB7 needs 7z, CBT needs TAR, MOBI and AZW3 need PalmDB record surgery
with offset arithmetic, PDF needs incremental update. Each is a project of its
own, and none is in scope.

**Detection is not support, and the difference matters.** `FormatDetector` still
recognises RAR, 7z, TAR, PalmDB and PDF by content, and must keep doing so. A
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
intact. **Resolved by refusing:** `CbzHandler.Write` throws rather than saving
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
    BookFormats.cs         the handler registry: Register / For / Resolve
    BookExceptions.cs      BookFormatException, BookIoException
    AtomicFileWriter.cs    the only sanctioned way a user's file is replaced
    BatchSession.cs        many files read, edited and saved together
    MetadataFields.cs      the text projection of a field, shared by both editors
    Finding.cs             Finding + Severity
    NamespaceRepair.cs     recovery of missing xmlns declarations
    Log.cs                 the session log: Info / Warning / Error / Finding
    Compat.cs              everything net48 lacks, in one file
    Containers/        IContainer + ZipContainer
    Formats/           IFormatHandler, EpubHandler, CbzHandler, FormatDetector,
                       FormatCapabilities, FormatId, ReadOptions
    Documents/         OpfDocument, ComicInfoDocument, XmlSourceFormat
    Model/             BookMetadata, Creator, Identifier, SeriesInfo, CoverImage
  EBookMeta.App/        net48          — WinForms, single instance, argv = paths
                       MainForm, BatchForm, SettingsForm, LogForm, AboutForm,
                       ShellRegistration, SingleInstance, AppIcon,
                       Strings, EmbeddedAssemblies
    Languages/         one key = value file per interface language
tests/
  EBookMeta.Core.Tests/ net48
    Fixtures/          golden expected-byte files only
    Builders/          synthetic file generators (see Test corpus)
```

**Four folders, and a file gets one only when several files share a subject.**
A directory holding one file is pure navigation cost, so anything that stands
alone lives at the Core root. Resist adding a folder for a single class, and
resist splitting one feature across six files — a feature is a file until it
genuinely is not.

**Adding a format is one handler plus one line.** Implement `IFormatHandler`,
call `BookFormats.Register`, done: nothing in the UI or the open path changes,
because the UI asks the registry which handler to use and never names one.
Detection stays outside the handlers on purpose — the app must be able to say
"this .cbz is really a RAR archive", which is an answer no registered handler
could give.

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

Only ZIP is implemented. `IsWritable` stays on the interface because a
read-only container is a real concept the callers should keep handling.

### Format handlers

```csharp
interface IFormatHandler {
    FormatId Id { get; }
    FormatCapabilities Capabilities { get; }
    BookMetadata Read(IContainer c);
    void Write(IContainer c, BookMetadata m, string targetPath);
    IEnumerable<Finding> Validate(IContainer c, BookMetadata m);
}
```

`FormatCapabilities` declares which model fields the format can store, and
whether writing is supported. **The UI reads this to disable fields**, so that
a user never types into a box whose content will be discarded. Adding a model
field means updating every handler's capabilities — that is intentional
friction.

### Detection

`FormatDetector` decides by content, never by extension. In collections,
extensions lie constantly — a `.cbz` that is really RAR is common.

- `PK\x03\x04` → ZIP; then inspect entries: `mimetype` containing
  `application/epub+zip` → EPUB; `ComicInfo.xml` or only image files → comic
- `Rar!\x1a\x07` (v4) or `Rar!\x1a\x07\x01\x00` (v5) → RAR — recognised, not supported
- `7z\xBC\xAF\x27\x1C` → 7z — recognised, not supported
- `ustar` at offset 257 → TAR — recognised, not supported
- PDB type+creator at offset 60: `BOOKMOBI` or `TEXtREAd` → MOBI family — recognised, not supported
- `%PDF-` → PDF — recognised, not supported

Read at most the first 8 KB for the magic-number pass. Distinguishing the
ZIP-based formats needs one more step, since EPUB and CBZ are both ZIPs: the
first local file header sits at offset 0 and settles it for conformant files
(an EPUB must store `mimetype` first). Only when that is inconclusive may the
sniffer fall back to reading the central directory — entry names only, never
content. If extension and content disagree, report it (`GEN-W002`).

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
- **Only edited files are written.** `BatchEntry` snapshots the text of every
  field on read, so dirtiness is "differs from what is on disk" rather than
  "somebody touched this row". Typing a value and typing it back writes nothing.
  A file nobody edited is not rewritten byte-identically — it is not opened.
- **Covers are not read** (`ReadOptions.WithoutCover`). A grid of titles has no
  use for three hundred full-size images.
- **Capabilities gate per cell, not per window.** A row's format decides which of
  its cells are editable, so `Sort title` is dead on a comic and live on a book
  in the same column. `BatchEntry.Apply` refuses a field the format cannot store
  even if a caller asks, which is what stops a bulk "apply to every selected row"
  from writing into files that would discard it.
- `Load` reads only `Pending` entries, so adding files later is cheap and calling
  it twice cannot discard unsaved edits by re-reading the files they were made
  against. There is deliberately no reload.
- Validation is separate and on demand. Validating a folder means opening every
  archive again, which is not something to do before the first row appears.

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
   no handler opens the target for writing directly.
2. **Never open an archive with `ZipArchiveMode.Update`.**
3. Entries other than the metadata document are copied **byte for byte**.
   Never round-trip XHTML, CSS or images through a parser.
4. **Preserve entry order and per-entry compression method.** Do not re-sort,
   do not recompress. Detect stored vs deflated on read and reproduce it.
5. Reject and report absolute paths and `..` traversal in entry names and
   manifest hrefs rather than following them.
6. Round-tripping is a no-op: open a file, save without editing, get identical
   bytes. There is a test per format; keep them green.

Accepted limitation: `System.IO.Compression` does not preserve ZIP extra fields,
original timestamps, or the archive comment. Round-trip byte-identity therefore
holds for archives whose structure can be reproduced — including every
builder-generated fixture — and may not for third-party files carrying extra
fields. Document it; do not hand-roll a ZIP writer.

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
14. **A repair never writes a file by itself.** Recovery happens on open and is
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
    `WellKnownNamespaces` is that list, and a prefix absent from it is reported,
    never bound. Inventing a plausible URI would fabricate metadata that was
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
- `Log.Finding` for a `Finding`, which picks the level from its severity.
- `Log.Debug` for detail that only matters once something has gone wrong.

Core logs; Core never writes to the console. `Log` holds no opinion about
presentation — the UI decides that, and `LogForm` renders `Log.Entries` directly
rather than reading the file back.

## Validation rules

The validator is the core value of this project. Rules are data, not scattered
`if` statements: each is a class implementing `IRule` with a stable ID,
severity, message and optional autofix. Adding a rule must not require
touching the engine.

**That engine does not exist yet.** Today each handler's `Validate` builds a
`List<Finding>` directly, and the rule IDs below are the stable part. Follow the
existing shape when adding a rule rather than inventing half an engine for it;
introducing `IRule` properly is its own change.

IDs are namespaced by format. `F` = fatal (cannot edit), `E` = error,
`W` = warning.

### General

| ID | Sev | Check |
|---|---|---|
| GEN-F001 | fatal | Container unreadable or truncated |
| GEN-W002 | warn | Extension disagrees with sniffed content |
| GEN-E003 | error | Entry name is absolute or contains `..` |
| GEN-W004 | warn | Format is recognised but not supported |

### EPUB

| ID | Sev | Check |
|---|---|---|
| EPUB-F001 | fatal | OPF is not well-formed XML |
| EPUB-F002 | fatal | `META-INF/container.xml` missing, unparseable, or `rootfile/@full-path` points nowhere |
| EPUB-E010 | error | `package/@unique-identifier` absent |
| EPUB-E011 | error | No `dc:identifier` whose `@id` matches `@unique-identifier` |
| EPUB-E012 | error | `dc:title` missing or empty |
| EPUB-E013 | error | `dc:language` missing |
| EPUB-W014 | warn | `dc:language` not a plausible BCP 47 tag |
| EPUB-E020 | error | `spine/itemref/@idref` has no matching manifest `@id` |
| EPUB-E021 | error | Manifest item `@href` not present in the container |
| EPUB-E022 | error | Duplicate `@id` in manifest |
| EPUB-W023 | warn | Container entry not referenced by the manifest |
| EPUB-E030 | error | Cover metadata points to a nonexistent manifest id |
| EPUB-W031 | warn | No cover declared |
| EPUB-W032 | warn | Cover declared in only one of the two conventions |
| EPUB-E040 | error | `mimetype` missing, not first, compressed, or wrong content |
| EPUB-E050 | error | Declared encoding does not match actual bytes |
| EPUB-W060 | warn | `meta/@refines` targets a nonexistent id |
| EPUB-W061 | warn | Series present in only one of the two conventions |
| EPUB-W070 | warn | Undeclared namespace prefix used |

### Comic archives

| ID | Sev | Check |
|---|---|---|
| CBZ-F001 | fatal | `ComicInfo.xml` present but not well-formed |
| CBZ-W010 | warn | No `ComicInfo.xml` — metadata will be created on save |
| CBZ-E011 | error | `ComicInfo.xml` not at archive root |
| CBZ-W012 | warn | Multiple metadata conventions present and disagreeing |
| CBZ-E020 | error | `PageCount` disagrees with the actual image count |
| CBZ-W021 | warn | `<Page>` entries do not match archive images |
| CBZ-W022 | warn | Image filenames do not sort into a stable reading order |
| CBZ-W023 | warn | Non-image, non-metadata entries present |
| CBZ-W030 | warn | `Number` present without `Series` |
| CBZ-W031 | warn | `Year`/`Month`/`Day` form an impossible date |
| CBZ-W032 | warn | `LanguageISO` not a valid ISO 639-1 code |

When adding a rule, add a fixture that triggers it in isolation.

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
- **The log and every `Finding` stay English.** They are diagnostics, pasted
  into bug reports, and keyed by stable rule IDs. Core knows nothing about the
  interface language and must not learn.
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

Required coverage:

- valid + byte-identical round-trip for both formats
- one fixture per validation rule
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
- **AngleSharp.Xml** (MIT) — second-stage tolerant XML parse. Load lazily,
  only when strict parsing has already failed, so it stays off the hot path.
- **xunit** — tests only.

**The project is Apache-2.0**, so every dependency must be compatible with it.
Do **not** add iText (AGPL) or any GPL-licensed library. With MOBI out of scope,
the calibre licensing problem that once constrained this project no longer
applies: `MetadataUpdater` in `calibre/ebooks/metadata/mobi.py` is GPL-3.0, but
there is no longer any reason to go near it. MIT dependencies are fine;
copyleft ones are not.

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
`.epub` and `.cbz`; do not register formats the app cannot open.

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
- Detect from the first 8 KB. Parse only the metadata document. Do not
  enumerate, hash or decompress the whole archive on open.
- Cross-checks that require full enumeration (`EPUB-E021`, `EPUB-W023`,
  `CBZ-E020`, `CBZ-W021`) run **lazily**, on request rather than on open.
  A 300-page CBZ must not be walked on launch.
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

- Both formats are implemented. Never touch a serialisation path while the
  round-trip or golden-byte tests for either one are red — they are the only thing
  standing between a bug and a corrupted library.
- Prefer Core changes + tests over touching the UI. UI is the last step of a
  feature, not the first.
- When adding a format, the order is: builder → sniffer → read → validate →
  write. Never write before round-trip reading is proven.
- When a change touches serialisation, run round-trip and golden-file tests
  first.
- Resist scope creep back toward the formats listed as out of scope. They were
  removed deliberately.
- If a task would require breaking a hard invariant above, stop and say so
  instead of finding a workaround.
