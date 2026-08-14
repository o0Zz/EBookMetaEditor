using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EBookMeta.Formats;

namespace EBookMeta.App;

/// <summary>
/// The batch editor: one row per file, one column per field, one save for all of
/// them.
/// </summary>
/// <remarks>
/// <para>
/// The window that makes the product's own pitch true — fixing a publisher across
/// a series without opening thirty files. Everything about which files exist, what
/// they hold and what happens when they are written belongs to
/// <see cref="BatchSession"/>; this is a grid over it.
/// </para>
/// <para>
/// One thing it does not do, deliberately: it does not show the description or the
/// cover. Both need room a row does not have, and both are what the single-file
/// editor is for.
/// </para>
/// <para>
/// There is no validate button, here or anywhere. Reading a file reports what is
/// wrong with it and saving corrects what can be corrected, so every row's problems
/// are already in the log by the time it appears.
/// </para>
/// </remarks>
internal sealed class BatchForm : Form, IPathReceiver
{
    /// <summary>
    /// The editable columns, in order.
    /// </summary>
    /// <remarks>
    /// Description is absent on purpose: a paragraph does not belong in a grid cell.
    /// Everything else <see cref="MetadataFields"/> can project as text is here, and
    /// each cell is enabled per row according to what that row's format can store.
    /// </remarks>
    private static readonly (MetadataField Field, string HeaderKey, int Width)[] FieldColumns =
    [
        (MetadataField.Title, "field.title", 200),
        (MetadataField.Creators, "field.authors", 150),
        (MetadataField.Series, "field.series", 140),
        (MetadataField.SeriesIndex, "field.seriesIndexShort", 45),
        (MetadataField.Publisher, "field.publisher", 120),
        (MetadataField.PublicationDate, "field.published", 90),
        (MetadataField.Language, "field.language", 70),
        (MetadataField.Subjects, "field.subjects", 160),
        (MetadataField.SortTitle, "field.sortTitle", 150),
    ];

    private const int StatusColumn = 0;
    private const int FileColumn = 1;
    private const int FormatColumn = 2;
    private const int FirstFieldColumn = 3;

    private readonly AppSettings _settings;
    private readonly BatchSession _session;

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = true,
        AutoGenerateColumns = false,
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
    };

    // AutoSize throughout rather than fixed widths: "Save all" is "Alle speichern"
    // in German, which does not fit in a width measured against English.
    private readonly Button _saveAll = Action("batch.button.saveAll", 96);
    private readonly Button _cancel = Action("button.cancel", 84);

    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private readonly ToolStripProgressBar _progress = new() { Visible = false, Width = 160 };

    private CancellationTokenSource? _work;
    private bool _busy;

    /// <summary>
    /// Whether the grid is being written to by this form rather than by the user.
    /// </summary>
    /// <remarks>
    /// <see cref="OnCellValueChanged"/> writes the stored value back into the cell
    /// it just read, and filling a row writes every cell, both of which raise the
    /// event again. Without this the commit would recurse until the stack ran out.
    /// </remarks>
    private bool _filling;

    /// <summary>Creates the batch window over the given files and folders.</summary>
    /// <param name="settings">The loaded user settings.</param>
    /// <param name="paths">Files to edit, and folders to look in.</param>
    internal BatchForm(AppSettings settings, IEnumerable<string> paths)
    {
        _settings = settings;
        _session = BatchSession.Create(Expand(paths));

        _saveAll.Enabled = false;
        _cancel.Visible = false;

        Text = Strings.Get("batch.title");
        AppIcon.Apply(this);
        ClientSize = new Size(1100, 560);
        MinimumSize = new Size(760, 400);
        AllowDrop = true;

        BuildLayout(BuildMenu());
        BuildColumns();
        RestoreGeometry();

        // CellValueChanged rather than CellEndEdit: it fires for a value that
        // arrives any way at all, including from an accessibility tool driving the
        // grid, where CellEndEdit never runs and the edit would be lost in silence.
        // For someone typing, the two are the same moment.
        _grid.CellValueChanged += OnCellValueChanged;
        _grid.CellFormatting += OnCellFormatting;
        _grid.CellDoubleClick += OnCellDoubleClick;
        _grid.CellMouseDown += OnCellMouseDown;
        _grid.ContextMenuStrip = BuildCellMenu();

        _saveAll.Click += (_, _) => SaveAll();
        _cancel.Click += (_, _) => _work?.Cancel();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        AddRows(_session.Entries);
    }

    /// <inheritdoc />
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Rows exist and show their file names already, so the window is useful
        // before anything has been read. Reading fills them in as it goes.
        StartLoad();
    }

    /// <inheritdoc />
    public void AcceptPaths(string[] paths)
    {
        Activate();

        if (paths.Length == 0)
        {
            return;
        }

        AddRows(_session.Add(Expand(paths)));
        StartLoad();
    }

    /// <summary>
    /// Turns whatever the shell handed over into a list of files.
    /// </summary>
    /// <remarks>
    /// A folder is expanded here rather than deeper down, so the batch itself only
    /// ever deals in files and a row always corresponds to something that can be
    /// saved. Subfolders are not walked: a user who drops a folder means that
    /// folder, and quietly recursing into a whole library would be a surprise with
    /// a save button attached.
    /// </remarks>
    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                IReadOnlyList<string> found;

                try
                {
                    found = BatchSession.FindBooks(path);
                }
                catch (BookIoException ex)
                {
                    Log.Error($"Could not list '{path}'", ex);
                    continue;
                }

                foreach (string file in found)
                {
                    yield return file;
                }
            }
            else if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>A button labelled from the language files, wide enough for its text.</summary>
    private static Button Action(string key, int minimumWidth) => new()
    {
        Text = Strings.Get(key),
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(minimumWidth, 26),
        Margin = new Padding(4, 3, 4, 3),
    };

    private static ToolStripMenuItem Item(string key, Action? action = null, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(Strings.Get(key));

        if (action is not null)
        {
            item.Click += (_, _) => action();
        }

        if (shortcut != Keys.None)
        {
            item.ShortcutKeys = shortcut;
        }

        return item;
    }

    private MenuStrip BuildMenu()
    {
        ToolStripMenuItem file = Item("menu.file");
        file.DropDownItems.Add(Item("menu.file.addFiles", AddFiles, Keys.Control | Keys.O));
        file.DropDownItems.Add(Item("menu.file.addFolder", AddFolder));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("menu.file.saveAll", SaveAll, Keys.Control | Keys.S));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("menu.file.close", Close));

        ToolStripMenuItem edit = Item("menu.edit");
        edit.DropDownItems.Add(WithShortcut(Item("menu.edit.copy", CopySelection), "Ctrl+C"));
        edit.DropDownItems.Add(WithShortcut(Item("menu.edit.paste", PasteIntoSelection), "Ctrl+V"));

        ToolStripMenuItem help = Item("menu.help");
        help.DropDownItems.Add(Item("menu.help.log", ShowLog, Keys.Control | Keys.L));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Item("menu.help.about", ShowAbout));

        // Named explicitly: a bare MenuStrip takes its accessible name from the
        // nearest label, which is whatever text happens to sit next to it — so a
        // screen reader announces the menu bar as something that is not a menu bar.
        var menu = new MenuStrip { AccessibleName = Strings.Get("menu.accessible") };
        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(help);
        return menu;
    }

    /// <summary>
    /// Shows a shortcut beside a menu item without registering it.
    /// </summary>
    /// <remarks>
    /// <c>ShortcutKeys</c> would make the menu answer the key everywhere in the
    /// window, including inside a cell being edited and inside the value box, where
    /// Ctrl+C has to keep meaning "copy this text". The keys are handled in
    /// <see cref="ProcessCmdKey"/>, which can tell those cases apart; this only puts
    /// the label where someone looking for the feature will find it.
    /// </remarks>
    private static ToolStripMenuItem WithShortcut(ToolStripMenuItem item, string shortcut)
    {
        item.ShortcutKeyDisplayString = shortcut;
        return item;
    }

    /// <summary>The right-click menu on a cell.</summary>
    /// <remarks>
    /// The same two commands as the Edit menu. Right-click is where people look for
    /// copy and paste first, and a feature nobody can find is not delivered.
    /// </remarks>
    private ContextMenuStrip BuildCellMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(WithShortcut(Item("menu.edit.copy", CopySelection), "Ctrl+C"));
        menu.Items.Add(WithShortcut(Item("menu.edit.paste", PasteIntoSelection), "Ctrl+V"));

        return menu;
    }

    private void BuildLayout(MenuStrip menu)
    {
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(8, 6, 8, 6),
        };

        buttons.Controls.AddRange([_saveAll, _cancel]);

        _status.Items.Add(_statusText);
        _status.Items.Add(_progress);

        // Fill first: WinForms docks in reverse child order, so the grid has to be
        // added before the panels that box it in.
        Controls.Add(_grid);
        Controls.Add(buttons);
        Controls.Add(_status);
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(ReadOnlyColumn(Strings.Get("column.status"), 110));
        _grid.Columns.Add(ReadOnlyColumn(Strings.Get("column.file"), 200));
        _grid.Columns.Add(ReadOnlyColumn(Strings.Get("column.format"), 70));

        foreach ((MetadataField field, string headerKey, int width) in FieldColumns)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = Strings.Get(headerKey),
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                Tag = field,
                MaxInputLength = 4000,
            });
        }
    }

    private static DataGridViewTextBoxColumn ReadOnlyColumn(string header, int width) => new()
    {
        HeaderText = header,
        Width = width,
        ReadOnly = true,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        DefaultCellStyle = new DataGridViewCellStyle { BackColor = SystemColors.Control },
    };

    private void AddRows(IReadOnlyList<BatchEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        _grid.SuspendLayout();

        foreach (BatchEntry entry in entries)
        {
            int index = _grid.Rows.Add();
            DataGridViewRow row = _grid.Rows[index];

            row.Tag = entry;
            row.Cells[FileColumn].Value = entry.FileName;
            row.Cells[FileColumn].ToolTipText = entry.Path;

            RefreshRow(row);
        }

        _grid.ResumeLayout();
        UpdateStatus();
    }

    /// <summary>Rewrites a row from its entry.</summary>
    private void RefreshRow(DataGridViewRow row)
    {
        var entry = (BatchEntry)row.Tag;

        // Filling is not editing: these values come from the model, so committing
        // them back through the changed-value path would be a no-op at best and a
        // loop at worst.
        _filling = true;

        try
        {
            Fill(row, entry);
        }
        finally
        {
            _filling = false;
        }
    }

    private void Fill(DataGridViewRow row, BatchEntry entry)
    {
        row.Cells[StatusColumn].Value = StatusTextOf(entry);
        row.Cells[FormatColumn].Value = entry.Detected is { } detected
            ? FormatIds.ToDisplayName(detected.Format)
            : string.Empty;

        foreach (DataGridViewCell cell in FieldCells(row))
        {
            var field = (MetadataField)_grid.Columns[cell.ColumnIndex].Tag;

            cell.Value = entry.Read(field);

            // A cell the format cannot store is dead rather than merely ignored on
            // save: the reason FormatCapabilities exists is that a user must never
            // type into a box whose contents get discarded. A comic has nowhere to
            // put a sort title, so that cell is grey on a comic's row and live on a
            // book's, in the same column.
            bool writable = entry.Capabilities?.CanWriteAll(field) == true;
            cell.ReadOnly = !writable;
            cell.Style.BackColor = writable ? SystemColors.Window : SystemColors.Control;
        }

        if (entry.Error is { } error)
        {
            row.Cells[StatusColumn].ToolTipText = error;
        }
    }

    private static string StatusTextOf(BatchEntry entry) => entry.Status switch
    {
        BatchEntryStatus.Pending => Strings.Get("batch.state.reading"),
        BatchEntryStatus.Loaded => entry.IsDirty ? Strings.Get("batch.state.edited") : string.Empty,
        BatchEntryStatus.Saved => Strings.Get(entry.IsDirty ? "batch.state.edited" : "batch.state.saved"),
        BatchEntryStatus.Unsupported => Strings.Get("batch.state.cannotEdit"),
        BatchEntryStatus.Failed => Strings.Get("batch.state.failed"),
        _ => string.Empty,
    };

    private IEnumerable<DataGridViewCell> FieldCells(DataGridViewRow row)
    {
        for (int i = FirstFieldColumn; i < _grid.Columns.Count; i++)
        {
            yield return row.Cells[i];
        }
    }

    /// <summary>
    /// Marks the cells whose value differs from the file on disk.
    /// </summary>
    /// <remarks>
    /// Bold rather than coloured, so it survives a high-contrast theme and does not
    /// compete with the grey of a cell that cannot be edited at all.
    /// </remarks>
    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < FirstFieldColumn ||
            _grid.Rows[e.RowIndex].Tag is not BatchEntry entry)
        {
            return;
        }

        var field = (MetadataField)_grid.Columns[e.ColumnIndex].Tag;
        bool changed = entry.ChangedFields.Contains(field);

        e.CellStyle.Font = changed
            ? new Font(_grid.Font, FontStyle.Bold)
            : _grid.Font;
    }

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_filling || e.RowIndex < 0 || e.ColumnIndex < FirstFieldColumn ||
            _grid.Rows[e.RowIndex].Tag is not BatchEntry entry)
        {
            return;
        }

        DataGridViewRow row = _grid.Rows[e.RowIndex];
        var field = (MetadataField)_grid.Columns[e.ColumnIndex].Tag;
        string typed = row.Cells[e.ColumnIndex].Value?.ToString() ?? string.Empty;

        entry.Apply(field, typed);

        // Read back rather than trusting what was typed: the model normalises — a
        // list gets its separators tidied, an index its decimal point — and showing
        // the stored value is the only way the grid tells the truth about what a
        // save will write.
        _filling = true;

        try
        {
            row.Cells[e.ColumnIndex].Value = entry.Read(field);
            row.Cells[StatusColumn].Value = StatusTextOf(entry);
        }
        finally
        {
            _filling = false;
        }

        UpdateStatus();
    }

    /// <summary>Opens the double-clicked file in the single-file editor.</summary>
    /// <remarks>
    /// Read-only in the sense that matters: the grid holds this file's unsaved edits,
    /// so opening a second editor over the same path would give two windows a claim
    /// on it. Offered only for a row with nothing pending.
    /// </remarks>
    private void OnCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex >= FirstFieldColumn ||
            _grid.Rows[e.RowIndex].Tag is not BatchEntry entry)
        {
            return;
        }

        if (entry.IsDirty)
        {
            SetStatus(Strings.Get("batch.rowDirty"));
            return;
        }

        var editor = new MainForm(_settings, entry.Path);
        editor.Show(this);
    }

    /// <summary>
    /// Handles the grid's own shortcuts before anything else sees them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In <c>ProcessCmdKey</c> rather than a <c>KeyDown</c> handler because the grid
    /// answers Ctrl+C itself and would otherwise consume it before the form ever
    /// heard about it — leaving copy and paste implemented in two different places
    /// with two different ideas of what a selection is.
    /// </para>
    /// <para>
    /// Both guards are load-bearing. Inside a cell being edited, Ctrl+C and Ctrl+V
    /// mean the ordinary text-editing thing, and taking them over there would make
    /// it impossible to copy part of a title. The focus check keeps them out of
    /// anything else that might one day hold text on this window.
    /// </para>
    /// </remarks>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_busy && _grid.Focused && !_grid.IsCurrentCellInEditMode)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.C:
                    CopySelection();
                    return true;

                case Keys.Control | Keys.V:
                    PasteIntoSelection();
                    return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Puts the selected cells on the clipboard.</summary>
    /// <remarks>
    /// Through the grid's own <c>GetClipboardContent</c>, which writes text and HTML
    /// flavours of the same selection — so a block copied out of here pastes into
    /// Excel as a table rather than as one run-on line.
    /// </remarks>
    private void CopySelection()
    {
        // Commit first, so copy and paste both see the value the model holds rather
        // than whatever is half-typed in an open cell editor. Reachable from the
        // menu while a cell is being edited, which is where this matters.
        _grid.EndEdit();

        if (_grid.GetClipboardContent() is not { } content)
        {
            SetStatus(Strings.Get("batch.nothingToCopy"));
            return;
        }

        int count = _grid.SelectedCells.Count;

        try
        {
            Clipboard.SetDataObject(content, copy: true);
            SetStatus(Strings.Plural("batch.copied", count, count));
        }
        catch (ExternalException ex)
        {
            // Another application holds the clipboard open. Nothing is wrong with
            // the grid, and saying so is more use than a stack trace.
            Log.Warning($"The clipboard could not be written: {ex.Message}");
            SetStatus(Strings.Get("batch.clipboardBusy"));
        }
    }

    /// <summary>Writes the clipboard into the grid.</summary>
    /// <remarks>
    /// <para>
    /// Two shapes, because both are things people actually do. <b>One value</b> goes
    /// into every selected cell that can hold it — copy a publisher once, select the
    /// column down thirty rows, paste. <b>A block</b> — several values, from another
    /// row here or from a spreadsheet — lands anchored at the top-left of the
    /// selection and fills right and down from there, which is what every grid does
    /// and therefore what people expect.
    /// </para>
    /// <para>
    /// Cells whose format cannot store the field are counted and reported, never
    /// written — the same rule every other edit here follows, and why pasting a
    /// column of sort titles across a mixed selection of books and comics does the
    /// right thing for the books and says so for the comics.
    /// </para>
    /// </remarks>
    private void PasteIntoSelection()
    {
        if (_busy)
        {
            return;
        }

        _grid.EndEdit();

        string[][] block = ClipboardBlock();

        if (block.Length == 0)
        {
            SetStatus(Strings.Get("batch.paste.empty"));
            return;
        }

        List<DataGridViewCell> targets = block.Length == 1 && block[0].Length == 1
            ? EditableSelection()
            : BlockTargets(block);

        if (targets.Count == 0)
        {
            SetStatus(Strings.Get("batch.paste.selectCell"));
            return;
        }

        int pasted = 0;
        int skipped = 0;
        var touched = new HashSet<DataGridViewRow>();

        foreach (DataGridViewCell cell in targets)
        {
            DataGridViewRow row = _grid.Rows[cell.RowIndex];

            if (row.Tag is not BatchEntry entry)
            {
                continue;
            }

            var field = (MetadataField)_grid.Columns[cell.ColumnIndex].Tag;

            if (entry.Capabilities?.CanWriteAll(field) != true)
            {
                skipped++;
                continue;
            }

            entry.Apply(field, ValueFor(block, cell, targets[0]));
            touched.Add(row);
            pasted++;
        }

        foreach (DataGridViewRow row in touched)
        {
            RefreshRow(row);
        }

        SetStatus(skipped == 0
            ? Strings.Plural("batch.pasted", pasted, pasted)
            : Strings.Plural("batch.pastedSkipped", pasted, pasted, skipped));

        UpdateStatus(keepMessage: true);
    }

    /// <summary>
    /// The value a target cell takes: the only one there is, or the one at its
    /// offset from the anchor.
    /// </summary>
    private static string ValueFor(string[][] block, DataGridViewCell cell, DataGridViewCell anchor)
    {
        if (block.Length == 1 && block[0].Length == 1)
        {
            return block[0][0];
        }

        int row = cell.RowIndex - anchor.RowIndex;
        int column = cell.ColumnIndex - anchor.ColumnIndex;

        return row >= 0 && row < block.Length && column >= 0 && column < block[row].Length
            ? block[row][column]
            : string.Empty;
    }

    /// <summary>The selected cells that belong to an editable column, in grid order.</summary>
    private List<DataGridViewCell> EditableSelection()
    {
        List<DataGridViewCell> cells = [.. _grid.SelectedCells
            .Cast<DataGridViewCell>()
            .Where(c => c.ColumnIndex >= FirstFieldColumn)
            .OrderBy(c => c.RowIndex)
            .ThenBy(c => c.ColumnIndex)];

        // Nothing selected in a field column, but the cursor is sitting in one:
        // treat that as the target rather than making the user select it again.
        if (cells.Count == 0 && _grid.CurrentCell is { } current && current.ColumnIndex >= FirstFieldColumn)
        {
            cells.Add(current);
        }

        return cells;
    }

    /// <summary>
    /// The cells a block covers, anchored at the top-left of the selection.
    /// </summary>
    /// <remarks>
    /// Clipped to the grid rather than extended: a block that runs off the right or
    /// the bottom pastes what fits. Growing the grid is not an option — a row is a
    /// file on disk.
    /// </remarks>
    private List<DataGridViewCell> BlockTargets(string[][] block)
    {
        List<DataGridViewCell> selection = EditableSelection();

        if (selection.Count == 0)
        {
            return [];
        }

        int firstRow = selection.Min(c => c.RowIndex);
        int firstColumn = selection.Min(c => c.ColumnIndex);
        int width = block.Max(line => line.Length);

        var cells = new List<DataGridViewCell>();

        for (int row = 0; row < block.Length && firstRow + row < _grid.Rows.Count; row++)
        {
            for (int column = 0; column < width && firstColumn + column < _grid.Columns.Count; column++)
            {
                cells.Add(_grid.Rows[firstRow + row].Cells[firstColumn + column]);
            }
        }

        return cells;
    }

    /// <summary>
    /// Reads the clipboard as a grid of values.
    /// </summary>
    /// <remarks>
    /// Tab-separated rows, which is what this grid, Excel and every other
    /// spreadsheet put on the clipboard, so copying from any of them works without
    /// asking the user which format they meant. Trailing newlines are dropped
    /// because a copied selection ends with one and would otherwise paste an empty
    /// row over real metadata.
    /// </remarks>
    private static string[][] ClipboardBlock()
    {
        string text;

        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (ExternalException ex)
        {
            Log.Warning($"The clipboard could not be read: {ex.Message}");
            return [];
        }

        text = text.TrimEnd('\r', '\n');

        return text.Length == 0
            ? []
            : [.. text
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Select(line => line.Split('\t'))];
    }

    /// <summary>
    /// Moves the cursor to a right-clicked cell, unless it is already selected.
    /// </summary>
    /// <remarks>
    /// Without this the context menu acts on wherever the cursor happened to be,
    /// which is not where the user just clicked. Right-clicking inside an existing
    /// selection leaves it alone, because collapsing a selection of thirty rows the
    /// moment somebody reaches for the menu that acts on them would be perverse.
    /// </remarks>
    private void OnCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        DataGridViewCell cell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];

        if (!cell.Selected)
        {
            _grid.CurrentCell = cell;
        }
    }

    private void StartLoad()
    {
        if (_busy || _session.Entries.All(e => e.Status != BatchEntryStatus.Pending))
        {
            return;
        }

        RunInBackground(
            "batch.verb.reading",
            "Reading",
            (progress, token) =>
            {
                _session.Load(progress, token);
                return null;
            });
    }

    private void SaveAll()
    {
        if (_busy)
        {
            return;
        }

        if (_session.DirtyCount == 0)
        {
            SetStatus(Strings.Get("batch.nothingEdited"));
            return;
        }

        bool keepBackup = _settings.KeepBackupOnSave;

        RunInBackground(
            "batch.verb.saving",
            "Saving",
            (progress, token) => _session.Save(keepBackup, progress, token).ToString());
    }

    /// <summary>
    /// Runs a batch operation off the UI thread, with progress and a working cancel.
    /// </summary>
    /// <param name="verbKey">The status line's verb, in the interface language.</param>
    /// <param name="logVerb">
    /// The same verb in English, for the log. Two words rather than one, because
    /// a log is a diagnostic that gets pasted into a bug report: it stays in one
    /// language whatever the window is showing.
    /// </param>
    /// <param name="operation">
    /// The work. Returns the message to show when it finishes, or null for the
    /// default.
    /// </param>
    /// <remarks>
    /// <para>
    /// A folder of four hundred files takes seconds to read and longer to write, and
    /// a frozen window during either is indistinguishable from a crash. So the work
    /// runs on a task and reports back through <c>BeginInvoke</c> — the same shape as
    /// the cover decode in the single-file editor, and no <c>async void</c> outside
    /// an event handler.
    /// </para>
    /// <para>
    /// The grid is disabled while a batch runs. Editing a row that is being written
    /// is not a race worth having.
    /// </para>
    /// </remarks>
    private void RunInBackground(
        string verbKey,
        string logVerb,
        Func<IProgress<BatchProgress>, CancellationToken, string?> operation)
    {
        _work?.Dispose();
        _work = new CancellationTokenSource();
        CancellationToken token = _work.Token;

        string what = Strings.Get(verbKey);

        SetBusy(true, what);

        var progress = new Progress<BatchProgress>(report =>
        {
            _progress.Maximum = Math.Max(1, report.Total);
            _progress.Value = Math.Min(report.Completed, _progress.Maximum);
            SetStatus(Strings.Format(
                "batch.progress", what, report.Completed, report.Total, Path.GetFileName(report.Path)));
        });

        Task.Run(() => operation(progress, token))
            .ContinueWith(
                task => Finish(what, logVerb, task),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
    }

    private void Finish(string what, string logVerb, Task<string?> task)
    {
        // Marshalled by hand rather than continued on the UI scheduler: the window
        // can be closing while a batch finishes, and BeginInvoke on a dead handle is
        // the case that has to be survived rather than thrown from a task nobody
        // awaits.
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                SetBusy(false, what);

                foreach (DataGridViewRow row in _grid.Rows)
                {
                    RefreshRow(row);
                }

                bool cancelled = task.IsCanceled ||
                    task.Exception?.InnerExceptions.Any(e => e is OperationCanceledException) == true;

                if (cancelled)
                {
                    SetStatus(Strings.Format("batch.cancelled", what));
                }
                else if (task.Exception is { } failure)
                {
                    Exception cause = failure.InnerExceptions[0];
                    Log.Error($"{logVerb} failed", cause);
                    SetStatus(Strings.Format("batch.failed", what, cause.Message));
                }
                else if (task.Result is { } message)
                {
                    SetStatus(message);
                }

                // Operations that have nothing to report leave the summary showing,
                // which after a read is more useful than the word "finished".
                UpdateStatus(keepMessage: cancelled || task.IsFaulted || task.Result is not null);

                if (!cancelled)
                {
                    // Files that arrived while this was running are still unread, and
                    // this is the first moment there is a thread free to read them.
                    // Not after a cancellation: that would restart what was stopped.
                    StartLoad();
                }
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Log.Debug($"Batch {logVerb.ToLowerInvariant()} finished after the window closed.");
        }
    }

    private void SetBusy(bool busy, string what)
    {
        _busy = busy;

        _grid.Enabled = !busy;
        _saveAll.Enabled = !busy && _session.DirtyCount > 0;
        _cancel.Visible = busy;
        _progress.Visible = busy;

        if (busy)
        {
            _progress.Value = 0;
            SetStatus(Strings.Format("batch.busy", what));
        }
    }

    private void UpdateStatus(bool keepMessage = false)
    {
        int dirty = _session.DirtyCount;
        _saveAll.Enabled = !_busy && dirty > 0;

        if (keepMessage || _busy)
        {
            return;
        }

        int loaded = _session.Entries.Count(e => e.Status is BatchEntryStatus.Loaded or BatchEntryStatus.Saved);
        int unusable = _session.Entries.Count(
            e => e.Status is BatchEntryStatus.Unsupported or BatchEntryStatus.Failed);

        var parts = new List<string> { Strings.Plural("batch.summary.files", loaded, loaded) };

        if (unusable > 0)
        {
            parts.Add(Strings.Format("batch.summary.unusable", unusable));
        }

        parts.Add(dirty == 0
            ? Strings.Get("batch.summary.noEdits")
            : Strings.Format("batch.summary.edited", dirty));

        SetStatus(string.Join(" · ", parts));
    }

    private void SetStatus(string text) => _statusText.Text = text;

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Strings.Get("dialog.addFiles.title"),
            Multiselect = true,
            Filter = MainForm.BookFilter(),
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AcceptPaths(dialog.FileNames);
        }
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = Strings.Get("dialog.folder.add") };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AcceptPaths([dialog.SelectedPath]);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && !_busy
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (!_busy && e.Data?.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            AcceptPaths(dropped);
        }
    }

    private void ShowLog()
    {
        using var form = new LogForm();
        form.ShowDialog(this);
    }

    private void ShowAbout()
    {
        using var form = new AboutForm();
        form.ShowDialog(this);
    }

    private void RestoreGeometry()
    {
        if (!_settings.RememberWindowGeometry || _settings.BatchWindowBounds == Rectangle.Empty)
        {
            StartPosition = FormStartPosition.CenterScreen;
            return;
        }

        // Only restore a position still visible on some screen: a window remembered
        // on a monitor that is no longer attached would open off-screen and look
        // like a failure to launch.
        Rectangle bounds = _settings.BatchWindowBounds;

        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }

        if (_settings.BatchWindowMaximised)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    /// <inheritdoc />
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            // Closing mid-write would leave the remaining files unwritten with no
            // record of which. Cancelling first is bounded: a save stops between
            // files, never inside one.
            DialogResult stop = MessageBox.Show(
                this,
                Strings.Get("batch.close.busy"),
                Strings.Get("app.name"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (stop != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _work?.Cancel();
        }
        else if (_session.DirtyCount > 0)
        {
            DialogResult answer = MessageBox.Show(
                this,
                Strings.Plural("batch.close.dirty", _session.DirtyCount, _session.DirtyCount),
                Strings.Get("app.name"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            Log.Warning($"Batch closed with {_session.DirtyCount} unsaved file(s).");
        }

        if (_settings.RememberWindowGeometry)
        {
            _settings.BatchWindowMaximised = WindowState == FormWindowState.Maximized;
            _settings.BatchWindowBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.TrySave();
        }

        base.OnFormClosing(e);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _work?.Dispose();
        }

        base.Dispose(disposing);
    }

}
