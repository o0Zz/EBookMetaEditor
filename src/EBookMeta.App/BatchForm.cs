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
    private static readonly (MetadataField Field, string Header, int Width)[] FieldColumns =
    [
        (MetadataField.Title, "Title", 200),
        (MetadataField.Creators, "Authors", 150),
        (MetadataField.Series, "Series", 140),
        (MetadataField.SeriesIndex, "#", 45),
        (MetadataField.Publisher, "Publisher", 120),
        (MetadataField.PublicationDate, "Published", 90),
        (MetadataField.Language, "Language", 70),
        (MetadataField.Subjects, "Subjects", 160),
        (MetadataField.SortTitle, "Sort title", 150),
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

    private readonly TextBox _bulkValue = new() { Width = 220 };
    private readonly Button _bulkApply = new() { Text = "Apply to selection", Width = 130, Height = 24 };
    private readonly Button _saveAll = new() { Text = "&Save all", Width = 90, Height = 26, Enabled = false };
    private readonly Button _validateAll = new() { Text = "&Validate all", Width = 90, Height = 26 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 80, Height = 26, Visible = false };

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

        Text = "Batch edit — EBookMetaEditor";
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

    private MenuStrip BuildMenu()
    {
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("Add &files…", null, (_, _) => AddFiles())
        {
            ShortcutKeys = Keys.Control | Keys.O,
        });
        file.DropDownItems.Add(new ToolStripMenuItem("Add f&older…", null, (_, _) => AddFolder()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("&Save all", null, (_, _) => SaveAll())
        {
            ShortcutKeys = Keys.Control | Keys.S,
        });
        file.DropDownItems.Add(new ToolStripMenuItem("&Validate all", null, (_, _) => ValidateAll()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("&Close", null, (_, _) => Close()));

        var help = new ToolStripMenuItem("?");
        help.DropDownItems.Add(new ToolStripMenuItem("&Log…", null, (_, _) => ShowLog())
        {
            ShortcutKeys = Keys.Control | Keys.L,
        });
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(new ToolStripMenuItem("&About EBookMetaEditor…", null, (_, _) => ShowAbout()));

        // Named explicitly: a bare MenuStrip inherits its accessible name from the
        // nearest label, which here is the hint about Ctrl+D — so a screen reader
        // would announce the menu bar as a sentence about filling cells down.
        var menu = new MenuStrip { AccessibleName = "Main menu" };
        menu.Items.Add(file);
        menu.Items.Add(help);
        return menu;
    }

    private void BuildLayout(MenuStrip menu)
    {
        foreach ((MetadataField field, string header, _) in FieldColumns)
        {
            _bulkField.Items.Add(new FieldChoice(field, header));
        }

        _bulkField.SelectedIndex = 0;

        var bulk = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 7, 8, 7) };

        var label = new Label
        {
            Text = "Set",
            AutoSize = true,
            Location = new Point(8, 11),
        };

        _bulkField.Location = new Point(36, 8);
        _bulkValue.Location = new Point(152, 8);
        _bulkApply.Location = new Point(380, 7);

        var hint = new Label
        {
            Text = "…on every selected row.  Ctrl+D copies the current cell down the selection.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(520, 11),
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
        _grid.Columns.Add(ReadOnlyColumn("Status", 110));
        _grid.Columns.Add(ReadOnlyColumn("File", 200));
        _grid.Columns.Add(ReadOnlyColumn("Format", 60));
        _grid.Columns.Add(ReadOnlyColumn("Findings", 60));

        foreach ((MetadataField field, string header, int width) in FieldColumns)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
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
        BatchEntryStatus.Pending => "Reading…",
        BatchEntryStatus.Loaded => entry.IsDirty ? "Edited" : string.Empty,
        BatchEntryStatus.Saved => entry.IsDirty ? "Edited" : "Saved",
        BatchEntryStatus.Unsupported => "Cannot edit",
        BatchEntryStatus.Failed => "Failed",
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
            SetStatus("Save this row before opening it on its own — it has unsaved edits here.");
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
        Apply(field, current.Value?.ToString() ?? string.Empty, $"{_grid.Columns[current.ColumnIndex].HeaderText} copied");
    }

    private void ApplyToSelection()
    {
        if (_busy || _bulkField.SelectedItem is not FieldChoice choice)
        {
            return;
        }

        Apply(choice.Field, _bulkValue.Text, $"{choice.Header} set");
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
            SetStatus("Select the rows to change first.");
            return;
        }

        SetStatus(skipped == 0
            ? $"{what} on {applied} row{Plural(applied)}."
            : $"{what} on {applied} row{Plural(applied)}; {skipped} could not store it.");

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
            SetStatus("Nothing has been edited.");
            return;
        }

        bool keepBackup = _settings.KeepBackupOnSave;

        RunInBackground(
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
            "Validating",
            (progress, token) =>
            {
                _session.Validate(progress, token);
                return $"{_session.Entries.Sum(e => e.FindingCount ?? 0)} finding(s) — see the log.";
            });
    }

    /// <summary>
    /// Runs a batch operation off the UI thread, with progress and a working cancel.
    /// </summary>
    /// <param name="what">The verb for the status line.</param>
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
    private void RunInBackground(string what, Func<IProgress<BatchProgress>, CancellationToken, string?> operation)
    {
        _work?.Dispose();
        _work = new CancellationTokenSource();
        CancellationToken token = _work.Token;

        SetBusy(true, what);

        var progress = new Progress<BatchProgress>(report =>
        {
            _progress.Maximum = Math.Max(1, report.Total);
            _progress.Value = Math.Min(report.Completed, _progress.Maximum);
            SetStatus($"{what} {report.Completed} of {report.Total} — {Path.GetFileName(report.Path)}");
        });

        Task.Run(() => operation(progress, token))
            .ContinueWith(
                task => Finish(what, task),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
    }

    private void Finish(string what, Task<string?> task)
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
                    SetStatus($"{what} cancelled.");
                }
                else if (task.Exception is { } failure)
                {
                    Exception cause = failure.InnerExceptions[0];
                    Log.Error($"{what} failed", cause);
                    SetStatus($"{what} failed: {cause.Message}");
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
            Log.Debug($"Batch {what.ToLowerInvariant()} finished after the window closed.");
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
            SetStatus($"{what}…");
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

        var parts = new List<string> { $"{loaded} file{Plural(loaded)}" };

        if (unusable > 0)
        {
            parts.Add($"{unusable} cannot be edited");
        }

        parts.Add(dirty == 0 ? "no edits" : $"{dirty} edited");

        SetStatus(string.Join(" · ", parts));
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private void SetStatus(string text) => _statusText.Text = text;

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Add books or comics",
            Multiselect = true,
            Filter = "Supported files (*.epub;*.cbz)|*.epub;*.cbz|EPUB (*.epub)|*.epub"
                + "|Comic archive (*.cbz)|*.cbz|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AcceptPaths(dialog.FileNames);
        }
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Add every book and comic in a folder" };

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
                "A batch operation is still running. Stop it and close?",
                "EBookMetaEditor",
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
                $"{_session.DirtyCount} file(s) have unsaved edits. Close without saving?",
                "EBookMetaEditor",
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
