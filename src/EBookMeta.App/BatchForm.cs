using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EBookMeta.App;

/// <summary>
/// The batch editor: one row per file, one column per field, one save for all of
/// them.
/// </summary>
internal sealed class BatchForm : Form, IPathReceiver
{
    /// <summary>The editable columns, in order.</summary>
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

    private const int SaveColumn = 0;
    private const int StatusColumn = 1;
    private const int FileColumn = 2;
    private const int FormatColumn = 3;
    private const int FirstFieldColumn = 4;

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
    private readonly Button _saveAll = Dialogs.Action("batch.button.saveAll", 96);
    private readonly Button _refresh = Dialogs.Action("batch.button.refresh");
    private readonly Button _cancel = Dialogs.Action("button.cancel");

    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private readonly ToolStripProgressBar _progress = new() { Visible = false, Width = 160 };

    private CancellationTokenSource? _work;
    private bool _busy;

    /// <summary>The column the grid is ordered by, or null while it is in the order the files arrived.</summary>
    private DataGridViewColumn? _sortedColumn;

    private bool _sortDescending;

    /// <summary>
    /// Whether the grid is being written to by this form rather than by the user.
    /// </summary>
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
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        BuildLayout(BuildMenu());
        BuildColumns();

        // CellValueChanged, not CellEndEdit: it fires however the value arrived,
        // including from an accessibility tool, where CellEndEdit never runs.
        _grid.CellValueChanged += OnCellValueChanged;

        // A tick box otherwise holds its new value until the cell loses focus, so
        // the click above would appear to do nothing until the user clicked away.
        _grid.CurrentCellDirtyStateChanged += OnCurrentCellDirtyStateChanged;
        _grid.ColumnHeaderMouseClick += OnColumnHeaderMouseClick;
        _grid.CellFormatting += OnCellFormatting;
        _grid.CellDoubleClick += OnCellDoubleClick;
        _grid.CellMouseDown += OnCellMouseDown;
        _grid.ContextMenuStrip = BuildCellMenu();

        _saveAll.Click += (_, _) => SaveAll();
        _refresh.Click += (_, _) => RefreshFromDisk();
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

    /// <summary>Turns whatever the shell handed over into a list of files.</summary>
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

        // F5 lives on the menu item rather than in ProcessCmdKey: the base
        // implementation dispatches menu shortcuts, and the grid does not want F5.
        file.DropDownItems.Add(Item("menu.file.refresh", RefreshFromDisk, Keys.F5));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("menu.file.saveAll", SaveAll, Keys.Control | Keys.S));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("menu.file.close", Close));

        ToolStripMenuItem edit = Item("menu.edit");
        edit.DropDownItems.Add(WithShortcut(Item("menu.edit.copy", CopySelection), "Ctrl+C"));
        edit.DropDownItems.Add(WithShortcut(Item("menu.edit.paste", PasteIntoSelection), "Ctrl+V"));
        edit.DropDownItems.Add(WithShortcut(Item("menu.edit.clear", ClearSelection), "Del"));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("menu.edit.number", NumberSelection, Keys.Control | Keys.I));

        ToolStripMenuItem help = Item("menu.help");
        help.DropDownItems.Add(Item("menu.help.log", () => Dialogs.ShowLog(this), Keys.Control | Keys.L));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Item("menu.help.about", () => Dialogs.ShowAbout(this)));

        // Named explicitly: a bare MenuStrip takes its accessible name from the
        // nearest label, which is whatever text happens to sit next to it — so a
        // screen reader announces the menu bar as something that is not a menu bar.
        var menu = new MenuStrip { AccessibleName = Strings.Get("menu.accessible") };
        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(help);
        return menu;
    }

    /// <summary>Shows a shortcut beside a menu item without registering it.</summary>
    private static ToolStripMenuItem WithShortcut(ToolStripMenuItem item, string shortcut)
    {
        item.ShortcutKeyDisplayString = shortcut;
        return item;
    }

    /// <summary>The right-click menu on a cell.</summary>
    private ContextMenuStrip BuildCellMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(WithShortcut(Item("menu.edit.copy", CopySelection), "Ctrl+C"));
        menu.Items.Add(WithShortcut(Item("menu.edit.paste", PasteIntoSelection), "Ctrl+V"));
        menu.Items.Add(WithShortcut(Item("menu.edit.clear", ClearSelection), "Del"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(WithShortcut(Item("menu.edit.number", NumberSelection), "Ctrl+I"));

        return menu;
    }

    private void BuildLayout(MenuStrip menu)
    {
        FlowLayoutPanel buttons = Dialogs.ButtonStrip(_saveAll, _refresh, _cancel);

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
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = Strings.Get("column.save"),
            ToolTipText = Strings.Get("batch.saveTip"),
            Width = 52,
            Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        _grid.Columns.Add(ReadOnlyColumn(Strings.Get("column.status"), 110));
        _grid.Columns.Add(ReadOnlyColumn(Strings.Get("column.file"), 200));
        _grid.Columns.Add(ReadOnlyColumn(Strings.Get("column.format"), 70));

        foreach ((MetadataField field, string headerKey, int width) in FieldColumns)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = Strings.Get(headerKey),
                Width = width,
                // Programmatic, not Automatic: sorting the cells' text puts 10 before
                // 2 and a bare year after a full date. Core owns the comparison.
                SortMode = DataGridViewColumnSortMode.Programmatic,
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
        SortMode = DataGridViewColumnSortMode.Programmatic,
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
        SetTick(row, entry);

        row.Cells[StatusColumn].Value = StatusTextOf(entry);
        row.Cells[FormatColumn].Value = entry.Detected is { } detected
            ? detected.Format.DisplayName()
            : string.Empty;

        foreach (DataGridViewCell cell in FieldCells(row))
        {
            var field = (MetadataField)_grid.Columns[cell.ColumnIndex].Tag;

            cell.Value = entry.Read(field);

            // Dead, not ignored on save: a user must never type into a cell whose
            // contents get discarded. Per row, so one column can be both.
            bool writable = entry.Capabilities?.CanWriteAll(field) == true;
            cell.ReadOnly = !writable;
            cell.Style.BackColor = writable ? SystemColors.Window : SystemColors.Control;
        }

        if (entry.Error is { } error)
        {
            row.Cells[StatusColumn].ToolTipText = error;
        }
    }

    /// <summary>Shows whether a row will be written, and whether that is the user's to decide.</summary>
    private static void SetTick(DataGridViewRow row, BatchEntry entry)
    {
        DataGridViewCell tick = row.Cells[SaveColumn];

        tick.Value = entry.WillSave;
        tick.ReadOnly = !entry.IsWritable || entry.IsDirty;
        tick.Style.BackColor = tick.ReadOnly ? SystemColors.Control : SystemColors.Window;
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

    /// <summary>Marks the cells whose value differs from the file on disk.</summary>
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

    private void OnCurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.ColumnIndex == SaveColumn)
        {
            _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_filling || e.RowIndex < 0 || e.ColumnIndex < SaveColumn ||
            _grid.Rows[e.RowIndex].Tag is not BatchEntry entry)
        {
            return;
        }

        DataGridViewRow row = _grid.Rows[e.RowIndex];

        if (e.ColumnIndex == SaveColumn)
        {
            entry.SaveRequested = row.Cells[SaveColumn].Value is true;
            UpdateStatus();
            return;
        }

        if (e.ColumnIndex < FirstFieldColumn)
        {
            return;
        }

        var field = (MetadataField)_grid.Columns[e.ColumnIndex].Tag;
        string typed = row.Cells[e.ColumnIndex].Value?.ToString() ?? string.Empty;

        entry.Apply(field, typed);

        // Read back rather than trusting what was typed: the model normalises, and
        // only the stored value tells the truth about what a save will write.
        _filling = true;

        try
        {
            row.Cells[e.ColumnIndex].Value = entry.Read(field);
            row.Cells[StatusColumn].Value = StatusTextOf(entry);

            // An edit is what ticks the box, so the row says it will be saved from
            // the same keystroke that made it worth saving.
            SetTick(row, entry);
        }
        finally
        {
            _filling = false;
        }

        UpdateStatus();
    }

    /// <summary>Opens the double-clicked file in the single-file editor.</summary>
    private void OnCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        // Not the tick column: a double click there is two clicks on a box, and
        // opening a second window on top of them is not what either one asked for.
        if (e.RowIndex < 0 || e.ColumnIndex == SaveColumn || e.ColumnIndex >= FirstFieldColumn ||
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

    /// <summary>Handles the grid's own shortcuts before anything else sees them.</summary>
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

                // Backspace as well as Delete: the grid does not start an edit on
                // it, so it would otherwise do nothing at all in a spreadsheet.
                case Keys.Delete:
                case Keys.Back:
                    ClearSelection();
                    return true;

                // Only over the tick column: everywhere else a space bar starts an
                // edit with a space in it, which is what EditOnKeystrokeOrF2 is for.
                case Keys.Space when _grid.CurrentCell?.ColumnIndex == SaveColumn:
                    ToggleSelectedTicks();
                    return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Ticks or unticks every row selected in the tick column.</summary>
    private void ToggleSelectedTicks()
    {
        List<DataGridViewRow> rows = [.. _grid.SelectedCells
            .Cast<DataGridViewCell>()
            .Where(c => c.ColumnIndex == SaveColumn)
            .Select(c => _grid.Rows[c.RowIndex])
            .Distinct()];

        if (rows.Count == 0 && _grid.CurrentCell is { } current)
        {
            rows.Add(_grid.Rows[current.RowIndex]);
        }

        SetTicks(rows);
    }

    /// <summary>
    /// Ticks or unticks every row in one click of the tick column's header, and
    /// orders the grid by any other.
    /// </summary>
    private void OnColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (_busy || e.ColumnIndex < 0)
        {
            return;
        }

        if (e.ColumnIndex == SaveColumn)
        {
            SetTicks([.. _grid.Rows.Cast<DataGridViewRow>()]);
            return;
        }

        SortBy(_grid.Columns[e.ColumnIndex]);
    }

    /// <summary>
    /// Orders the grid by a column, reversing when it is already the one in use.
    /// </summary>
    private void SortBy(DataGridViewColumn column)
    {
        _sortDescending = ReferenceEquals(column, _sortedColumn) && !_sortDescending;
        _sortedColumn = column;

        ApplySort();
    }

    /// <summary>Puts the rows in the order the last header click asked for.</summary>
    private void ApplySort()
    {
        if (_sortedColumn is null || _grid.Rows.Count == 0)
        {
            return;
        }

        // A cell being edited holds its own copy of the value and would write it
        // back into whichever row landed underneath it.
        _grid.EndEdit();

        IComparer<BatchEntry> order = _sortedColumn.Tag is MetadataField field
            ? BatchEntryComparer.ByField(field, _sortDescending)
            : BatchEntryComparer.ByText(KeyOf(_sortedColumn.Index), _sortDescending);

        _grid.Sort(new RowComparer(order));

        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.HeaderCell.SortGlyphDirection = SortOrder.None;
        }

        _sortedColumn.HeaderCell.SortGlyphDirection =
            _sortDescending ? SortOrder.Descending : SortOrder.Ascending;
    }

    /// <summary>The text a column that holds no metadata field is ordered by.</summary>
    private static Func<BatchEntry, string> KeyOf(int column) => column switch
    {
        FileColumn => entry => entry.FileName,
        FormatColumn => entry => entry.Detected is { } detected
            ? detected.Format.DisplayName()
            : string.Empty,
        StatusColumn => StatusTextOf,
        _ => _ => string.Empty,
    };

    /// <summary>Orders grid rows by the entries behind them.</summary>
    private sealed class RowComparer(IComparer<BatchEntry> entries) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            BatchEntry? left = EntryOf(x);
            BatchEntry? right = EntryOf(y);

            // A row with no entry behind it has nothing to be compared by. The
            // interface is the net48 one, whose parameters are not annotated, so
            // this is the check rather than a suppression.
            if (left is null || right is null)
            {
                return left is null && right is null ? 0 : left is null ? 1 : -1;
            }

            return entries.Compare(left, right);
        }

        private static BatchEntry? EntryOf(object? row) => (row as DataGridViewRow)?.Tag as BatchEntry;
    }

    /// <summary>
    /// Turns a set of rows all one way: on unless every one of them is already on.
    /// </summary>
    private void SetTicks(IReadOnlyList<DataGridViewRow> rows)
    {
        List<(DataGridViewRow Row, BatchEntry Entry)> targets = [.. rows
            .Where(r => r.Tag is BatchEntry { IsWritable: true, IsDirty: false })
            .Select(r => (Row: r, Entry: (BatchEntry)r.Tag))];

        if (targets.Count == 0)
        {
            return;
        }

        bool wanted = targets.Any(t => !t.Entry.SaveRequested);

        // A box being edited holds its own copy of the value and would write it back
        // over this one when it closed.
        _grid.EndEdit();

        _filling = true;

        try
        {
            foreach ((DataGridViewRow row, BatchEntry entry) in targets)
            {
                entry.SaveRequested = wanted;
                SetTick(row, entry);
            }
        }
        finally
        {
            _filling = false;
        }

        // Assigning a cell's value does not repaint the current one, which would
        // leave the row under the cursor looking like the one that was missed.
        _grid.InvalidateColumn(SaveColumn);

        UpdateStatus();
    }

    /// <summary>Puts the selected cells on the clipboard.</summary>
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

        (int pasted, int skipped) = ApplyToCells(targets, cell => ValueFor(block, cell, targets[0]));

        SetStatus(skipped == 0
            ? Strings.Plural("batch.pasted", pasted, pasted)
            : Strings.Plural("batch.pastedSkipped", pasted, pasted, skipped));

        UpdateStatus(keepMessage: true);
    }

    /// <summary>Empties the selected cells.</summary>
    private void ClearSelection()
    {
        if (_busy)
        {
            return;
        }

        List<DataGridViewCell> targets = EditableSelection();

        if (targets.Count == 0)
        {
            SetStatus(Strings.Get("batch.paste.selectCell"));
            return;
        }

        (int cleared, int skipped) = ApplyToCells(targets, _ => string.Empty);

        SetStatus(skipped == 0
            ? Strings.Plural("batch.cleared", cleared, cleared)
            : Strings.Plural("batch.clearedSkipped", cleared, cleared, skipped));

        UpdateStatus(keepMessage: true);
    }

    /// <summary>
    /// Numbers the selected rows' series index, counting up from a first value.
    /// </summary>
    private void NumberSelection()
    {
        if (_busy)
        {
            return;
        }

        _grid.EndEdit();

        List<DataGridViewRow> rows = SelectedRows();

        if (rows.Count == 0)
        {
            SetStatus(Strings.Get("batch.number.selectRows"));
            return;
        }

        using var dialog = new NumberDialog();

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        decimal next = dialog.Start;
        int numbered = 0;
        int skipped = 0;
        var touched = new List<DataGridViewRow>();

        foreach (DataGridViewRow row in rows)
        {
            var entry = (BatchEntry)row.Tag;

            // The model cannot hold an index without a series, so a row with no series
            // name is left alone rather than swallowing the number.
            if (entry.Capabilities?.CanWriteAll(MetadataField.SeriesIndex) != true ||
                entry.Read(MetadataField.Series).Length == 0)
            {
                skipped++;
                continue;
            }

            // Invariant culture, as the model stores it: a French machine would
            // otherwise write 2,5 into a file no reader parses.
            entry.Apply(MetadataField.SeriesIndex, next.ToString(CultureInfo.InvariantCulture));

            // Only a row that took a number consumes one, so the sequence the user
            // asked for has no gaps where a comic without a series sat.
            next += dialog.Step;
            numbered++;
            touched.Add(row);
        }

        foreach (DataGridViewRow row in touched)
        {
            RefreshRow(row);
        }

        SetStatus(skipped == 0
            ? Strings.Plural("batch.numbered", numbered, numbered)
            : Strings.Plural("batch.numberedSkipped", numbered, numbered, skipped));

        UpdateStatus(keepMessage: true);
    }

    /// <summary>
    /// The rows holding a selected cell, in the order the grid is showing them.
    /// </summary>
    private List<DataGridViewRow> SelectedRows()
    {
        List<DataGridViewRow> rows = [.. _grid.SelectedCells
            .Cast<DataGridViewCell>()
            .Select(cell => _grid.Rows[cell.RowIndex])
            .Distinct()
            .Where(row => row.Tag is BatchEntry)
            .OrderBy(row => row.Index)];

        if (rows.Count == 0 && _grid.CurrentCell is { } current &&
            _grid.Rows[current.RowIndex].Tag is BatchEntry)
        {
            rows.Add(_grid.Rows[current.RowIndex]);
        }

        return rows;
    }

    /// <summary>
    /// Writes a value into every target cell whose format can store it, refreshes
    /// the rows that changed, and reports how many were written and how many were
    /// refused — a refusal is counted rather than hidden, because a user who
    /// selected thirty cells deserves to know three of them kept their value.
    /// </summary>
    private (int Written, int Skipped) ApplyToCells(
        List<DataGridViewCell> targets,
        Func<DataGridViewCell, string> value)
    {
        int written = 0;
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

            entry.Apply(field, value(cell));
            touched.Add(row);
            written++;
        }

        foreach (DataGridViewRow row in touched)
        {
            RefreshRow(row);
        }

        return (written, skipped);
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

    /// <summary>The cells a block covers, anchored at the top-left of the selection.</summary>
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

    /// <summary>Reads the clipboard as a grid of values.</summary>
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

    /// <summary>Reads every row not read yet, in the background.</summary>
    /// <param name="completed">
    /// What to leave in the status bar afterwards, for a caller with something to say
    /// that the per-file progress would otherwise scroll away.
    /// </param>
    private void StartLoad(string? completed = null)
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
                return completed;
            });
    }

    /// <summary>
    /// Reads the files again so the grid shows what is on disk now — after something
    /// else has edited them, or to retry the rows that failed.
    /// </summary>
    private void RefreshFromDisk()
    {
        if (_busy)
        {
            return;
        }

        BatchRefreshReport report = _session.Refresh();

        // Cleared before the read starts, not after it finishes: a row showing a
        // stale title beside one already re-read is worse than a blank one.
        foreach (DataGridViewRow row in _grid.Rows)
        {
            RefreshRow(row);
        }

        if (report.Rereading == 0)
        {
            // Everything is edited, so there is nothing a refresh may touch.
            SetStatus(Strings.Plural("batch.refresh.kept", report.Kept, report.Kept));
            return;
        }

        StartLoad(report.Kept == 0
            ? null
            : Strings.Plural("batch.refresh.kept", report.Kept, report.Kept));
    }

    private void SaveAll()
    {
        if (_busy)
        {
            return;
        }

        if (_session.PendingSaveCount == 0)
        {
            SetStatus(Strings.Get("batch.nothingToSave"));
            return;
        }

        bool keepBackup = _settings.KeepBackupOnSave;

        RunInBackground(
            "batch.verb.saving",
            "Saving",
            (progress, token) => Describe(_session.Save(keepBackup, progress, token)));
    }

    /// <summary>
    /// The save report as a status line. <c>BatchSaveReport.ToString</c> is the log's
    /// English form; this one is the window's.
    /// </summary>
    private static string Describe(BatchSaveReport report)
    {
        var parts = new List<string>
        {
            Strings.Plural("batch.saveReport.saved", report.Saved, report.Saved),
        };

        if (report.Skipped > 0)
        {
            parts.Add(Strings.Plural("batch.saveReport.unchanged", report.Skipped, report.Skipped));
        }

        if (report.Failed > 0)
        {
            parts.Add(Strings.Plural("batch.saveReport.failed", report.Failed, report.Failed));
        }

        return string.Join(" · ", parts);
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
        // Marshalled by hand: the window can be closing as a batch finishes, and
        // BeginInvoke on a dead handle must be survived, not thrown from a stray task.
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

                // Rows that were blank when the sort ran now have values, and rows
                // added since are sitting at the bottom in the order they arrived.
                ApplySort();

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
        _saveAll.Enabled = !busy && _session.PendingSaveCount > 0;
        _refresh.Enabled = !busy;
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
        int pending = _session.PendingSaveCount;
        _saveAll.Enabled = !_busy && pending > 0;

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

        // Counted from what a save would write rather than from what was edited: a
        // row ticked by hand is not an edit, and saying "no edits" while Save all is
        // lit would be the window contradicting itself.
        parts.Add(pending == 0
            ? Strings.Get("batch.summary.noEdits")
            : Strings.Format("batch.summary.toSave", pending));

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

    /// <summary>Asks what the first row's number is and how far apart the rest are.</summary>
    private sealed class NumberDialog : Form
    {
        private readonly NumericUpDown _start = Spin(1);
        private readonly NumericUpDown _step = Spin(1, minimum: 1);

        internal NumberDialog()
        {
            Dialogs.Chrome(this, "dialog.number.title", new Size(340, 190));

            Controls.Add(BuildLayout());
            Controls.Add(BuildButtons());
        }

        /// <summary>The number the first row takes.</summary>
        internal decimal Start => _start.Value;

        /// <summary>How much each row adds to the one before it.</summary>
        internal decimal Step => _step.Value;

        /// <summary>
        /// A whole-number spinner. Series indexes like 2.5 exist and the model
        /// keeps them, but nothing counts a shelf in halves.
        /// </summary>
        /// <param name="value">What it starts at.</param>
        /// <param name="minimum">
        /// The floor. Zero for the first number, because comics number a prologue
        /// issue 0; one for the step, because counting in noughts is not a request.
        /// </param>
        private static NumericUpDown Spin(decimal value, decimal minimum = 0) => new()
        {
            Minimum = minimum,
            Maximum = 9999,
            Value = value,
            Width = 70,
            TextAlign = HorizontalAlignment.Right,
        };

        private TableLayoutPanel BuildLayout()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(14, 12, 14, 6),
            };

            // The label column takes whatever its translation needs and the
            // spinners sit against it, so a German caption pushes rather than clips.
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddRow(layout, "dialog.number.start", _start);
            AddRow(layout, "dialog.number.step", _step);

            var hint = new Label
            {
                Text = Strings.Get("dialog.number.hint"),
                AutoSize = true,
                MaximumSize = new Size(296, 0),
                Margin = new Padding(0, 10, 0, 0),
                ForeColor = SystemColors.GrayText,
            };

            layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(hint, 0, layout.RowCount - 1);
            layout.SetColumnSpan(hint, 2);

            return layout;
        }

        private static void AddRow(TableLayoutPanel layout, string labelKey, Control control)
        {
            layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            layout.Controls.Add(
                new Label
                {
                    Text = Strings.Get(labelKey),
                    AutoSize = true,
                    Margin = new Padding(0, 6, 10, 3),
                },
                0,
                layout.RowCount - 1);

            layout.Controls.Add(control, 1, layout.RowCount - 1);
        }

        private FlowLayoutPanel BuildButtons()
        {
            Button ok = Dialogs.Action("button.ok", DialogResult.OK);
            Button cancel = Dialogs.Action("button.cancel", DialogResult.Cancel);

            // Rightmost first: the strip flows right to left.
            FlowLayoutPanel buttons = Dialogs.ButtonStrip(cancel, ok);

            AcceptButton = ok;
            CancelButton = cancel;

            return buttons;
        }
    }
}
