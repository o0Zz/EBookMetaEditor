using EBookMeta.Containers;
using EBookMeta.Formats;
using EBookMeta.Model;

namespace EBookMeta.App;

/// <summary>
/// The editor window: open a file, fix its metadata, save, close.
/// </summary>
/// <remarks>
/// Deliberately thin. Every decision about formats, validity and bytes belongs
/// to <c>EBookMeta.Core</c>; this is a form over it that could be replaced
/// without touching the logic. In particular the cover arrives as bytes and a
/// media type, and the <see cref="Bitmap"/> is constructed here — Core has no
/// idea what an image is.
/// </remarks>
internal sealed class MainForm : Form, IPathReceiver
{
    private readonly AppSettings _settings;
    private readonly TextBox _title = NewText();
    private readonly TextBox _sortTitle = NewText();
    private readonly TextBox _authors = NewText();
    private readonly TextBox _series = NewText();
    private readonly TextBox _seriesIndex = NewText();
    private readonly TextBox _publisher = NewText();
    private readonly TextBox _language = NewText();
    private readonly TextBox _published = NewText();
    private readonly TextBox _subjects = NewText();
    private readonly TextBox _description = NewText();
    private readonly PictureBox _cover = new()
    {
        SizeMode = PictureBoxSizeMode.Zoom,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = SystemColors.ControlLight,
    };

    /// <summary>
    /// Every text field paired with the model field it edits.
    /// </summary>
    /// <remarks>
    /// One list drives populating, capability gating and collecting, so a field
    /// cannot be shown but not saved, or saved but never disabled for a format
    /// that has nowhere to put it. Order matters: the series name carries the
    /// index, so it is applied first.
    /// </remarks>
    private readonly (MetadataField Field, TextBox Box)[] _fields;

    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripMenuItem _saveItem;

    /// <summary>
    /// Paths that arrived from other launches, waiting to be dealt with together.
    /// </summary>
    /// <remarks>
    /// Explorer starts one process per selected file, so a selection of thirty
    /// arrives here as thirty separate deliveries within a few hundred
    /// milliseconds. Acting on the first would open a batch of two and then fight
    /// the next twenty-nine, so they are collected behind a timer that restarts on
    /// each arrival and fires once the flurry stops.
    /// </remarks>
    private readonly List<string> _handoverPaths = [];

    // Fully qualified: the implicit usings bring in System.Threading, where there is
    // a different Timer that does not marshal to the UI thread.
    private readonly System.Windows.Forms.Timer _handover = new();

    private string? _initialPath;
    private string? _path;
    private BookMetadata? _metadata;
    private FormatCapabilities? _capabilities;
    private IFormatHandler? _handler;

    /// <summary>Creates the window, optionally opening a file immediately.</summary>
    /// <param name="settings">The loaded user settings.</param>
    /// <param name="initialPath">A file to open on launch — Explorer passes one.</param>
    internal MainForm(AppSettings settings, string? initialPath)
    {
        _settings = settings;

        _fields =
        [
            (MetadataField.Title, _title),
            (MetadataField.SortTitle, _sortTitle),
            (MetadataField.Creators, _authors),
            (MetadataField.Series, _series),
            (MetadataField.SeriesIndex, _seriesIndex),
            (MetadataField.Publisher, _publisher),
            (MetadataField.PublicationDate, _published),
            (MetadataField.Language, _language),
            (MetadataField.Subjects, _subjects),
            (MetadataField.Description, _description),
        ];

        Text = "EBookMetaEditor";
        AppIcon.Apply(this);
        ClientSize = new Size(880, 560);
        MinimumSize = new Size(700, 480);
        AllowDrop = true;

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] dropped)
            {
                AcceptPaths(dropped);
            }
        };

        // Only forwarded paths are debounced, never the one from the command line:
        // delaying the ordinary right-click by half a second to see whether more
        // files are coming would spend the entire startup budget waiting for
        // something that usually never arrives.
        _handover.Interval = 600;
        _handover.Tick += (_, _) => HandOverToBatch();

        _saveItem = new ToolStripMenuItem("&Save", null, (_, _) => Save())
        {
            ShortcutKeys = Keys.Control | Keys.S,
            Enabled = false,
        };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Open…", null, (_, _) => OpenWithDialog())
        {
            ShortcutKeys = Keys.Control | Keys.O,
        });
        file.DropDownItems.Add(new ToolStripMenuItem("&Batch edit folder…", null, (_, _) => OpenFolder())
        {
            ShortcutKeys = Keys.Control | Keys.B,
        });
        file.DropDownItems.Add(_saveItem);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("Se&ttings…", null, (_, _) => ShowSettings()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        // Labelled "?" as the help menu, the convention this app follows. Holds
        // the log and the About box.
        var help = new ToolStripMenuItem("?");
        help.DropDownItems.Add(new ToolStripMenuItem("&Log…", null, (_, _) => ShowLog())
        {
            ShortcutKeys = Keys.Control | Keys.L,
        });
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(new ToolStripMenuItem("&About EBookMetaEditor…", null, (_, _) => ShowAbout()));

        var menu = new MenuStrip();
        menu.Items.Add(file);
        menu.Items.Add(help);

        _status.Items.Add(_statusText);

        BuildLayout(menu);
        RestoreGeometry();

        // Opened from OnShown rather than here. The window must exist before a
        // message box or the repair dialog can be parented to it, and a
        // constructor cannot BeginInvoke: the handle is not created yet, so it
        // throws rather than deferring. Since argv[0] is how the Explorer verb
        // launches the app, that path is the normal one, not the exceptional one.
        _initialPath = initialPath;
    }

    /// <inheritdoc/>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_initialPath is null)
        {
            return;
        }

        string path = _initialPath;
        _initialPath = null;
        Open(path);
    }

    private void BuildLayout(MenuStrip menu)
    {
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(10),
            AutoScroll = true,
        };

        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(fields, "Title", _title);
        AddRow(fields, "Sort title", _sortTitle);
        AddRow(fields, "Authors", _authors);
        AddRow(fields, "Series", _series);
        AddRow(fields, "Series index", _seriesIndex);
        AddRow(fields, "Publisher", _publisher);
        AddRow(fields, "Published", _published);
        AddRow(fields, "Language", _language);
        AddRow(fields, "Subjects", _subjects);

        _description.Multiline = true;
        _description.ScrollBars = ScrollBars.Vertical;
        _description.Height = 90;
        AddRow(fields, "Description", _description);

        var right = new Panel { Dock = DockStyle.Right, Width = 220, Padding = new Padding(10) };
        _cover.Dock = DockStyle.Top;
        _cover.Height = 280;
        right.Controls.Add(_cover);

        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(fields);
        body.Controls.Add(right);

        Controls.Add(body);
        Controls.Add(_status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        SetStatus("Open a file to begin.");
    }

    private static void AddRow(TableLayoutPanel panel, string label, Control field)
    {
        panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) });
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(3, 4, 3, 4);
        panel.Controls.Add(field);
    }

    private static TextBox NewText() => new() { Width = 400 };

    private void OpenWithDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open a book or comic",
            Multiselect = true,
            Filter = "Supported files (*.epub;*.cbz)|*.epub;*.cbz|EPUB (*.epub)|*.epub|Comic archive (*.cbz)|*.cbz|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (dialog.FileNames.Length == 1)
        {
            Open(dialog.FileNames[0]);
            return;
        }

        OpenBatch(dialog.FileNames);
    }

    private void OpenFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Edit the metadata of every book and comic in a folder",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            OpenBatch([dialog.SelectedPath]);
        }
    }

    /// <inheritdoc />
    public void AcceptPaths(string[] paths)
    {
        Activate();

        if (paths.Length == 0)
        {
            return;
        }

        _handoverPaths.AddRange(paths);

        _handover.Stop();
        _handover.Start();
    }

    /// <summary>
    /// Deals with everything that arrived while the timer was running.
    /// </summary>
    /// <remarks>
    /// One file with nothing already open is just an open. Anything else is a batch,
    /// and the file this window is already showing joins it — a user who right-clicks
    /// a second book expects both, not a window that forgot the first.
    /// </remarks>
    private void HandOverToBatch()
    {
        _handover.Stop();

        if (_handoverPaths.Count == 0)
        {
            return;
        }

        string[] arrived = [.. _handoverPaths];
        _handoverPaths.Clear();

        if (arrived.Length == 1 && _path is null && !Directory.Exists(arrived[0]))
        {
            Open(arrived[0]);
            return;
        }

        OpenBatch(_path is null ? arrived : [_path, .. arrived]);
    }

    /// <summary>
    /// Opens the batch grid over the given paths and steps out of its way.
    /// </summary>
    /// <remarks>
    /// This window hides rather than closes, because it owns the message loop: the
    /// application ends when it does. It comes back when the grid is closed, which is
    /// also what gives the user somewhere to land rather than the process
    /// disappearing.
    /// </remarks>
    private void OpenBatch(IEnumerable<string> paths)
    {
        var batch = new BatchForm(_settings, paths);

        batch.FormClosed += (_, _) =>
        {
            if (!IsDisposed)
            {
                Show();
                Activate();
            }
        };

        Hide();
        batch.Show();
    }

    private void Open(string path)
    {
        Log.Info($"Opening '{path}'.");

        try
        {
            IFormatHandler? handler = BookFormats.Resolve(path, out DetectedFormat detected);

            if (handler is null)
            {
                Log.Warning(
                    $"'{path}' is {FormatIds.ToDisplayName(detected.Format)}, which this build cannot edit.");

                // Naming the format precisely is the point: "this .cbz is really
                // a RAR archive" is more useful than "unsupported file".
                MessageBox.Show(
                    this,
                    $"This file is {FormatIds.ToDisplayName(detected.Format)}"
                        + (detected.Detail is null ? "" : $" ({detected.Detail})") + ".\n\n"
                        + "EBookMetaEditor cannot edit that format.",
                    "EBookMetaEditor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            using ZipContainer container = ZipContainer.Open(path);

            _metadata = handler.Read(container);
            _capabilities = handler.Capabilities;
            _handler = handler;
            _path = path;

            Populate(_metadata);
            ApplyCapabilities(_capabilities);
            LogFindings(handler, container, _metadata);

            _saveItem.Enabled = _capabilities.CanWrite;
            Text = $"{Path.GetFileName(path)} — EBookMetaEditor";
            SetStatus($"{FormatIds.ToDisplayName(detected.Format)} · {container.Entries.Count} entries");
            Log.Info($"Opened '{path}' — {container.Entries.Count} entries.");
        }
        catch (Exception ex) when (ex is BookFormatException or BookIoException)
        {
            // Recoverable damage was already corrected on the way in, so reaching
            // here means the file is broken in a way this tool will not guess at.
            Log.Error($"Could not open '{path}'", ex);
            MessageBox.Show(this, ex.Message, "EBookMetaEditor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus(ex.Message);
        }
    }

    /// <summary>Fills the form from the model.</summary>
    /// <remarks>
    /// Field text comes from <see cref="MetadataFields"/> rather than from
    /// per-control code here, so this window and the batch grid show a value
    /// identically and write it back identically.
    /// </remarks>
    private void Populate(BookMetadata m)
    {
        foreach ((MetadataField field, TextBox box) in _fields)
        {
            box.Text = MetadataFields.Read(m, field);
        }

        ShowCover(m.Cover);
    }

    /// <summary>
    /// Decodes and shows the cover off the UI thread.
    /// </summary>
    /// <remarks>
    /// Decoding a multi-megabyte JPEG on the UI thread is exactly the kind of
    /// thing that turns a 400 ms launch into a visible stall, and it is avoidable
    /// because the cover is not needed for the window to be usable.
    /// </remarks>
    private void ShowCover(CoverImage? cover)
    {
        Image? previous = _cover.Image;
        _cover.Image = null;
        previous?.Dispose();

        if (cover is null)
        {
            return;
        }

        byte[] data = cover.Data;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(data, writable: false);
                var bitmap = new Bitmap(stream);

                BeginInvoke(new Action(() =>
                {
                    Image? old = _cover.Image;
                    _cover.Image = bitmap;
                    old?.Dispose();
                }));
            }
            catch (ArgumentException)
            {
                // A cover that is not a decodable image is a fact about the
                // file, not a crash. It is reported by validation, not here.
            }
        });
    }

    /// <summary>
    /// Disables inputs the format cannot store.
    /// </summary>
    /// <remarks>
    /// The reason <c>FormatCapabilities</c> exists: a user must never type into
    /// a box whose contents get silently discarded on save.
    /// </remarks>
    private void ApplyCapabilities(FormatCapabilities capabilities)
    {
        foreach ((MetadataField field, TextBox box) in _fields)
        {
            bool writable = capabilities.CanWriteAll(field);
            box.Enabled = writable;
            box.BackColor = writable ? SystemColors.Window : SystemColors.Control;
        }
    }

    /// <summary>Records what the handler found out about the file, in the log.</summary>
    /// <remarks>
    /// The window has no findings panel: rules go to the log, which is where a
    /// user looks when they want to know what happened rather than being told
    /// while they are trying to edit a title. The UI still knows nothing about
    /// what any rule means — it forwards findings and lets <c>Log</c> grade them.
    /// </remarks>
    private static void LogFindings(IFormatHandler handler, IContainer container, BookMetadata metadata)
    {
        int count = 0;

        foreach (Finding finding in handler.Validate(container, metadata))
        {
            Log.Finding(finding);
            count++;
        }

        if (count == 0)
        {
            Log.Debug("No findings from the format handler.");
        }
    }

    private void Save()
    {
        if (_path is null || _metadata is null || _handler is null || _capabilities is null || !_capabilities.CanWrite)
        {
            return;
        }

        try
        {
            CollectInto(_metadata);
            Log.Info($"Saving '{_path}' (keep backup: {_settings.KeepBackupOnSave}).");

            string source = _path;
            BookMetadata metadata = _metadata;
            IFormatHandler handler = _handler;

            AtomicFileWriter.Write(
                source,
                temp =>
                {
                    // Reopened inside the callback so the source handle is
                    // closed before File.Replace swaps the file underneath it.
                    using ZipContainer container = ZipContainer.Open(source);
                    handler.Write(container, metadata, temp);
                },
                _settings.KeepBackupOnSave);

            SetStatus(_settings.KeepBackupOnSave
                ? $"Saved. Previous version kept as {Path.GetFileName(source)}.bak"
                : "Saved.");

            Log.Info($"Saved '{source}'.");
        }
        catch (Exception ex) when (ex is BookFormatException or BookIoException)
        {
            Log.Error($"Could not save '{_path}'", ex);
            MessageBox.Show(this, ex.Message, "EBookMetaEditor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Reads the form back into the model.</summary>
    /// <remarks>
    /// Every field goes through <see cref="MetadataFields.Apply"/>, which leaves
    /// the model alone when the text has not changed. That is what makes opening a
    /// file and saving it produce identical bytes, and it is shared with the batch
    /// grid so both editors mean the same thing by an edit.
    /// </remarks>
    private void CollectInto(BookMetadata m)
    {
        foreach ((MetadataField field, TextBox box) in _fields)
        {
            MetadataFields.Apply(m, field, box.Text);
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

    private void ShowSettings()
    {
        using var dialog = new SettingsForm(_settings);
        dialog.ShowDialog(this);
    }

    private void SetStatus(string text) => _statusText.Text = text;

    private void RestoreGeometry()
    {
        if (!_settings.RememberWindowGeometry || _settings.WindowBounds == Rectangle.Empty)
        {
            StartPosition = FormStartPosition.CenterScreen;
            return;
        }

        // Only restore a position still visible on some screen: a window
        // remembered on a monitor that is no longer attached would open
        // off-screen and look like a failure to launch.
        Rectangle bounds = _settings.WindowBounds;
        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }

        if (_settings.WindowMaximised)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    /// <inheritdoc />
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_settings.RememberWindowGeometry)
        {
            _settings.WindowMaximised = WindowState == FormWindowState.Maximized;
            _settings.WindowBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.TrySave();
        }

        base.OnFormClosing(e);
    }
}
