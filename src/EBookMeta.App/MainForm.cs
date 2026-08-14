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

    /// <summary>
    /// How to re-label every piece of fixed prose in the window.
    /// </summary>
    /// <remarks>
    /// Closures rather than a list of controls, because a menu item and a label
    /// have no common base type worth casting to for the sake of one property.
    /// Collected so <see cref="Localize"/> can run twice: once while building,
    /// and again when the settings dialog changes the language, which would
    /// otherwise leave the window labelled in the language the user just left.
    /// Anything computed — the status line, the title once a file is open — is
    /// not in here, because it is rebuilt from its own state instead.
    /// </remarks>
    private readonly List<Action> _relabel = [];

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

    // One object, not a quartet of parallel nullables. Core owns what it means to
    // have a file open; the window only needs to know whether there is one.
    private Book? _book;

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

        Text = Strings.Get("app.name");
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

        _saveItem = Item("menu.file.save", () => Save(), Keys.Control | Keys.S);
        _saveItem.Enabled = false;

        ToolStripMenuItem file = Item("menu.file");
        file.DropDownItems.Add(Item("menu.file.open", OpenWithDialog, Keys.Control | Keys.O));
        file.DropDownItems.Add(Item("menu.file.batch", OpenFolder, Keys.Control | Keys.B));
        file.DropDownItems.Add(_saveItem);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("menu.file.settings", ShowSettings));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("menu.file.exit", Close));

        // Labelled "?" as the help menu, the convention this app follows. Holds
        // the log and the About box.
        ToolStripMenuItem help = Item("menu.help");
        help.DropDownItems.Add(Item("menu.help.log", ShowLog, Keys.Control | Keys.L));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Item("menu.help.about", ShowAbout));

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

    /// <summary>
    /// A menu item whose label follows the interface language.
    /// </summary>
    /// <remarks>
    /// The text is set through <see cref="_relabel"/> rather than here, so that
    /// building the menu and re-labelling it later are the same code and cannot
    /// drift into disagreeing about which key an item uses.
    /// </remarks>
    private ToolStripMenuItem Item(string key, Action? action = null, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem();

        if (action is not null)
        {
            item.Click += (_, _) => action();
        }

        if (shortcut != Keys.None)
        {
            item.ShortcutKeys = shortcut;
        }

        _relabel.Add(() => item.Text = Strings.Get(key));
        return item;
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

        // AutoSize rather than a fixed 110 px: "Series index" is "Nummer in der
        // Reihe" in German, and a fixed label column would clip it.
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(fields, "field.title", _title);
        AddRow(fields, "field.sortTitle", _sortTitle);
        AddRow(fields, "field.authors", _authors);
        AddRow(fields, "field.series", _series);
        AddRow(fields, "field.seriesIndex", _seriesIndex);
        AddRow(fields, "field.publisher", _publisher);
        AddRow(fields, "field.published", _published);
        AddRow(fields, "field.language", _language);
        AddRow(fields, "field.subjects", _subjects);

        _description.Multiline = true;
        _description.ScrollBars = ScrollBars.Vertical;
        _description.Height = 90;
        AddRow(fields, "field.description", _description);

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

        Localize();
    }

    /// <summary>
    /// Puts every fixed label into the current language.
    /// </summary>
    /// <remarks>
    /// Run again after the settings dialog closes, which is the only place the
    /// language can change. The batch window is not reachable while that dialog
    /// is open, so there is no second window to keep in step and no need for a
    /// change notification anybody could forget to unsubscribe from.
    /// </remarks>
    private void Localize()
    {
        foreach (Action relabel in _relabel)
        {
            relabel();
        }

        if (_book is null)
        {
            Text = Strings.Get("app.name");
            SetStatus(Strings.Get("main.status.begin"));
        }
    }

    private void AddRow(TableLayoutPanel panel, string key, Control field)
    {
        var label = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 12, 3),
        };

        _relabel.Add(() => label.Text = Strings.Get(key));

        panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(label);
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(3, 4, 3, 4);
        panel.Controls.Add(field);
    }

    private static TextBox NewText() => new() { Width = 400 };

    private void OpenWithDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Strings.Get("dialog.open.title"),
            Multiselect = true,
            Filter = BookFilter(),
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

    /// <summary>
    /// The file-dialog filter, assembled rather than translated whole.
    /// </summary>
    /// <remarks>
    /// A filter string is descriptions and glob patterns separated by bars, and
    /// handing that structure to a translator invites a misplaced bar that makes
    /// the dialog show nothing. Only the descriptions are translated; the
    /// patterns are built here.
    /// </remarks>
    internal static string BookFilter() =>
        $"{Strings.Get("filter.supported")}|*.epub;*.cbz"
        + $"|{Strings.Get("filter.epub")}|*.epub"
        + $"|{Strings.Get("filter.cbz")}|*.cbz"
        + $"|{Strings.Get("filter.all")}|*.*";

    private void OpenFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Strings.Get("dialog.folder.batch"),
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

        if (arrived.Length == 1 && _book is null && !Directory.Exists(arrived[0]))
        {
            Open(arrived[0]);
            return;
        }

        OpenBatch(_book is null ? arrived : [_book.Path, .. arrived]);
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
            // Everything the open needs — detection, recovery of what is
            // recoverable, and reporting what was noticed — happens in here. The
            // window's job starts once it has a Book.
            Book book = Book.Load(path);
            _book = book;

            Populate(book.Metadata);
            ApplyCapabilities(book.Capabilities);

            _saveItem.Enabled = book.CanSave;
            Text = Strings.Format("app.title.file", Path.GetFileName(path));
            SetStatus(Strings.Format(
                "main.status.format", FormatIds.ToDisplayName(book.Detected.Format), book.EntryCount));
            Log.Info($"Opened '{path}' — {book.EntryCount} entries.");
        }
        catch (UnsupportedFormatException ex)
        {
            Log.Warning(ex.Message);

            // Naming the format precisely is the point: "this .cbz is really
            // a RAR archive" is more useful than "unsupported file". The
            // format's own name is not translated — it is what the format is
            // called everywhere.
            string name = FormatIds.ToDisplayName(ex.Detected.Format);

            MessageBox.Show(
                this,
                Strings.Format(
                    "main.unsupported",
                    ex.Detected.Detail is null
                        ? name
                        : Strings.Format("main.formatWithDetail", name, ex.Detected.Detail)),
                Strings.Get("app.name"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is BookFormatException or BookIoException)
        {
            // Recoverable damage was already corrected on the way in, so reaching
            // here means the file is broken in a way this tool will not guess at.
            Log.Error($"Could not open '{path}'", ex);
            MessageBox.Show(this, ex.Message, Strings.Get("app.name"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private void Save()
    {
        if (_book is not { CanSave: true } book)
        {
            return;
        }

        try
        {
            CollectInto(book.Metadata);
            Log.Info($"Saving '{book.Path}' (keep backup: {_settings.KeepBackupOnSave}).");

            // Whatever was recovered on the way in is written here, along with the
            // user's edits, by the one save path. Until this line runs, the file on
            // disk is exactly as they found it.
            book.Save(_settings.KeepBackupOnSave);

            SetStatus(_settings.KeepBackupOnSave
                ? Strings.Format("main.status.savedBackup", Path.GetFileName(book.Path))
                : Strings.Get("main.status.saved"));

            Log.Info($"Saved '{book.Path}'.");
        }
        catch (Exception ex) when (ex is BookFormatException or BookIoException or NotSupportedException)
        {
            Log.Error($"Could not save '{book.Path}'", ex);
            MessageBox.Show(this, ex.Message, Strings.Get("app.name"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        // The one place the language can change, so the one place this window has
        // to re-read its own labels.
        Localize();
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
