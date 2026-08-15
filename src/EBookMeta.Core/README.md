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
| 5 | `BookContainers.cs` | The container factory, and the magic-number sniff. |
| 6 | `Formats/CbzFormat.cs` | The smallest real format. Read one before reading four. |

After that, `Formats/` and `Containers/` are four files each and can be read in any
order, or not at all until you need one.

## Adding a format

One implementation plus one line. Implement `IBookFormat`, call
`BookFormats.Register` — nothing in the UI or the open path changes, because both
ask the registry and neither names a format. The format brings its own
`Extensions`, which is where the Settings form's context-menu list comes from.

Build it in this order: fixture builder → `TryOpen` → `Read` → rules → `Write`.
Never write before round-trip reading is proven.

`TryOpen` **claims; it does not parse, and it never throws.** A damaged file is
still that format's file — an EPUB whose OPF will not parse is exactly the file the
repair path exists for. Parsing happens in `Read`, after a winner is picked.
