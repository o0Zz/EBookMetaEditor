# EBookMeta.Core

All the logic: opening a book, reading its metadata, repairing what is provably
broken, and writing it back without disturbing anything else. Zero UI dependencies
— that is enforced by the `GuardCoreHasNoUiDependencies` target in the csproj, not
by review.

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

**A comic archive is smaller still.** `CbzFormat.Flavours` pairs a `FormatId` with a
`ContainerKind` and an extension; a new row plus the container is the whole change,
and `BookFormats` does not need editing.

`TryOpen` **claims; it does not parse, and it never throws.** A damaged file is
still that format's file — an EPUB whose OPF will not parse is exactly the file the
repair path exists for. Parsing happens in `Read`, after a winner is picked.
