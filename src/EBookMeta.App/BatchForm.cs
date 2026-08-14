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
/// Two things it does not do, deliberately. It does not show the description or the
/// cover: both need room a row does not have, and both are what the single-file
/// editor is for. And it does not validate on open — a folder of four hundred comics
/// would mean four hundred archives cross-checked before the first row appeared, so
/// that is a button.
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
    /// <remarks>
    /// Two keys per column because a grid header and a picker entry want
    /// different lengths of the same name: the series index is "#" over a 45 px
    /// column and "Series index" in a list where there is room to read it.
    /// </remarks>
    private static readonly (MetadataField Field, string HeaderKey, string NameKey, int Width)[] FieldColumns =
    [
        (MetadataField.Title, "field.title", "field.title", 200),
        (MetadataField.Creators, "field.authors", "field.authors", 150),
        (MetadataField.Series, "field.series", "field.series", 140),
        (MetadataField.SeriesIndex, "field.seriesIndexShort", "field.seriesIndex", 45),
        (MetadataField.Publisher, "field.publisher", "field.publisher", 120),
        (MetadataField.PublicationDate, "field.published", "field.published", 90),
        (MetadataField.Language, "field.language", "field.language", 70),
        (MetadataField.Subjects, "field.subjects", "field.subjects", 160),
        (MetadataField.SortTitle, "field.sortTitle", "field.sortTitle", 150),
    ];

    private const int StatusColumn = 0;
    private const int FileColumn = 1;
    private const int FormatColumn = 2;
    private const int FindingsColumn = 3;
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

    private readonly ComboBox _bulkField = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 110,
    };

    // AutoSize throughout rather than fixed widths: "Apply to selection" is "Auf
    // Auswahl anwenden" in German, which does not fit in 130 px.
    private readonly TextBox _bulkValue = new() { Width = 220 };
    private readonly Button _bulkApply = Action("batch.bulk.apply", 132);
    private readonly Button _saveAll = Action("batch.button.saveAll", 96);
    private readonly Button _validateAll = Action("batch.button.validateAll", 96);
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
        KeyPreview = true;

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
        _grid.KeyDown += OnGridKeyDown;

        _bulkApply.Click += (_, _) => ApplyToSelection();
        _saveAll.Click += (_, _) => SaveAll();
        _validateAll.Click += (_, _) => ValidateAll();
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
        file.DropDownItems.Add(Item("menu.file.validateAll", ValidateAll));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("menu.file.close", Close));

        ToolStripMenuItem help = Item("menu.help");
        help.DropDownItems.Add(Item("menu.help.log", ShowLog, Keys.Control | Keys.L));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Item("menu.help.about", ShowAbout));

        // Named explicitly: a bare MenuStrip inherits its accessible name from the
        // nearest label, which here is the hint about Ctrl+D — so a screen reader
        // would announce the menu bar as a sentence about filling cells down.
        var menu = new MenuStrip { AccessibleName = Strings.Get("menu.accessible") };
        menu.Items.Add(file);
        menu.Items.Add(help);
        return menu;
    }

    private void BuildLayout(MenuStrip menu)
    {
        foreach ((MetadataField field, _, string nameKey, _) in FieldColumns)
        {
            _bulkField.Items.Add(new FieldChoice(field, Strings.Get(nameKey)));
        }

        _bulkField.SelectedIndex = 0;

        // A flow rather than coordinates: every control on this bar is as wide as
        // its own translation, so placing the next one means measuring the last.
        var bulk = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 6, 8, 6),
        };

        var label = new Label
        {
            Text = Strings.Get("batch.bulk.set"),
            AutoSize = true,
            Margin = new Padding(3, 8, 6, 3),
        };

        _bulkValue.Margin = new Padding(6, 5, 6, 3);
        _bulkField.Margin = new Padding(3, 4, 3, 3);

        var hint = new Label
        {
            Text = Strings.Get("batch.bulk.hint"),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(14, 8, 3, 3),
        };

        bulk.Controls.AddRange([label, _bulkField, _bulkValue, _bulkApply, hint]);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(8, 6, 8, 6),
        };

        buttons.Controls.AddRange([_saveAll, _validateAll, _cancel]);

        _status.Items.Add(_statusText);
        _status.Items.Add(_progress);

        // Fill first: WinForms docks in reverse child order, so the grid has to be
        // added before the panels that box it in.
        Controls.Add(_grid);
        Controls.Add(buttons);
        Controls.Add(_status);
        Controls.Add(bulk);
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(ReadOnlyColumn(Strings.Get("column.status"), 110));
        _grid.Columns.Add(ReadOnlyColumn(Strings.Get("column.file"), 200));
        _grid.Columns.Add(ReadOnlyColumn(Strings.Get("column.format"), 70));
        _grid.Columns.Add(ReadOnlyColumn(Strings.Get("column.findings"), 70));

        foreach ((MetadataField field, string headerKey, _, int width) in FieldColumns)
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
        row.Cells[FindingsColumn].Value = entry.FindingCount?.ToString() ?? string.Empty;

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

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.D)
        {
            FillDown();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Copies the current cell's value into that column on every selected row.
    /// </summary>
    /// <remarks>
    /// The keyboard half of bulk editing, and the reason a batch grid beats thirty
    /// windows: get one row right, select the rest, Ctrl+D.
    /// </remarks>
    private void FillDown()
    {
        if (_busy || _grid.CurrentCell is not { } current || current.ColumnIndex < FirstFieldColumn)
        {
            return;
        }

        var field = (MetadataField)_grid.Columns[current.ColumnIndex].Tag;
        string what = Strings.Format("batch.what.copied", _grid.Columns[current.ColumnIndex].HeaderText);

        Apply(field, current.Value?.ToString() ?? string.Empty, what);
    }

    private void ApplyToSelection()
    {
        if (_busy || _bulkField.SelectedItem is not FieldChoice choice)
        {
            return;
        }

        Apply(choice.Field, _bulkValue.Text, Strings.Format("batch.what.set", choice.Header));
    }

    /// <summary>
    /// Writes one value onto every selected row that can hold it.
    /// </summary>
    /// <remarks>
    /// Rows whose format cannot store the field are counted and reported rather than
    /// silently skipped. "Publisher set on 27 rows" when the user selected thirty is
    /// a question they deserve an answer to.
    /// </remarks>
    private void Apply(MetadataField field, string value, string what)
    {
        int applied = 0;
        int skipped = 0;

        foreach (DataGridViewRow row in SelectedRows())
        {
            var entry = (BatchEntry)row.Tag;

            if (entry.Capabilities?.CanWriteAll(field) != true)
            {
                skipped++;
                continue;
            }

            entry.Apply(field, value);
            RefreshRow(row);
            applied++;
        }

        if (applied == 0 && skipped == 0)
        {
            SetStatus(Strings.Get("batch.selectRows"));
            return;
        }

        SetStatus(skipped == 0
            ? Strings.Plural("batch.applied", applied, what, applied)
            : Strings.Plural("batch.appliedSkipped", applied, what, applied, skipped));

        UpdateStatus(keepMessage: true);
    }

    /// <summary>The rows the selection touches, each once, in grid order.</summary>
    private IEnumerable<DataGridViewRow> SelectedRows()
    {
        var seen = new HashSet<int>();

        foreach (DataGridViewCell cell in _grid.SelectedCells)
        {
            if (seen.Add(cell.RowIndex) && _grid.Rows[cell.RowIndex].Tag is BatchEntry)
            {
                yield return _grid.Rows[cell.RowIndex];
            }
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

    private void ValidateAll()
    {
        if (_busy)
        {
            return;
        }

        RunInBackground(
            "batch.verb.validating",
            "Validating",
            (progress, token) =>
            {
                _session.Validate(progress, token);

                int findings = _session.Entries.Sum(e => e.FindingCount ?? 0);
                return Strings.Plural("batch.findings", findings, findings);
            });
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
        _bulkApply.Enabled = !busy;
        _validateAll.Enabled = !busy;
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

    /// <summary>A field as it appears in the bulk-apply picker.</summary>
    /// <remarks>
    /// A class rather than a record: <c>init</c> accessors need a support type that
    /// only Core polyfills, and the UI project has no business declaring compiler
    /// plumbing of its own.
    /// </remarks>
    private sealed class FieldChoice(MetadataField field, string header)
    {
        internal MetadataField Field { get; } = field;

        internal string Header { get; } = header;

        /// <summary>The label the combo box shows.</summary>
        public override string ToString() => Header;
    }
}
