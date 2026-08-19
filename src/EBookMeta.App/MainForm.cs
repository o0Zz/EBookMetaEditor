using EBookMeta.Containers;
using EBookMeta.Model;

namespace EBookMeta.App;

/// <summary>The editor window: open a file, fix its metadata, save, close.</summary>
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

    /// <summary>Every text field paired with the model field it edits.</summary>
    private readonly (MetadataField Field, TextBox Box)[] _fields;

    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    /// <summary>How to re-label every piece of fixed prose in the window.</summary>
    private readonly List<Action> _relabel = [];

    private readonly ToolStripMenuItem _saveItem;

    /// <summary>
    /// The visible way out. File ▸ Exit does the same thing; this is for the
    /// right-click, fix, close workflow, which never opens the menu.
    /// </summary>
    private readonly Button _close = Dialogs.Action("button.close");

    /// <summary>
    /// Paths that arrived from other launches, waiting to be dealt with together.
    /// </summary>
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
        StartPosition = FormStartPosition.CenterScreen;
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

        // Only forwarded paths are debounced, never argv: delaying the ordinary
        // right-click would spend the whole startup budget on a maybe.
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
        help.DropDownItems.Add(Item("menu.help.log", () => Dialogs.ShowLog(this), Keys.Control | Keys.L));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Item("menu.help.about", () => Dialogs.ShowAbout(this)));

        var menu = new MenuStrip();
        menu.Items.Add(file);
        menu.Items.Add(help);

        _status.Items.Add(_statusText);

        BuildLayout(menu);

        // Opened from OnShown: a dialog needs a window to parent to, and a constructor
        // cannot BeginInvoke because the handle does not exist yet.
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

    /// <summary>A menu item whose label follows the interface language.</summary>
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

        // Close(), not Application.Exit(): OnFormClosing is what decides whether this
        // window may go, and it says no while a batch grid is still open.
        _close.Click += (_, _) => Close();

        // Fill first: WinForms docks in reverse child order, so the body has to be
        // added before the panels that box it in.
        Controls.Add(body);
        Controls.Add(Dialogs.ButtonStrip(_close));
        Controls.Add(_status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        Localize();
    }

    /// <summary>Puts every fixed label into the current language.</summary>
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
    /// The file-dialog filter, built from the format registry rather than listed, so
    /// a format added to Core appears here without this file or a language file
    /// being touched.
    /// </summary>
    internal static string BookFilter()
    {
        IBookFormat[] formats = [.. BookFormats.All.OrderBy(f => f.Id)];
        string all = Patterns(formats.SelectMany(f => f.Extensions));

        var parts = new List<string> { Strings.Format("filter.supported", all), all };

        foreach (IBookFormat format in formats)
        {
            string patterns = Patterns(format.Extensions);
            parts.Add($"{format.Id.DisplayName()} ({patterns})");
            parts.Add(patterns);
        }

        parts.Add(Strings.Get("filter.all"));
        parts.Add("*.*");

        return string.Join("|", parts);
    }

    private static string Patterns(IEnumerable<string> extensions) =>
        string.Join(";", extensions.Distinct(StringComparer.Ordinal).Select(e => "*" + e));

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

    /// <summary>Deals with everything that arrived while the timer was running.</summary>
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

            // This window may be hidden behind a grid it handed its last file to.
            Show();
            Activate();
            return;
        }

        string[] all = _book is null ? arrived : [_book.Path, .. arrived];

        // Ownership moves to the grid, not copied: a window still holding a Book the
        // grid owns would hand the same file to the next selection too.
        Release();

        // The grid *is* what the user right-clicked for, so this window has nothing
        // left to do once they close it.
        OpenBatch(all, thenClose: true);
    }

    /// <summary>Lets go of the open file, leaving the window as it starts up.</summary>
    private void Release()
    {
        _book = null;
        _saveItem.Enabled = false;

        foreach ((_, TextBox box) in _fields)
        {
            box.Text = string.Empty;
            box.Enabled = true;
            box.BackColor = SystemColors.Window;
        }

        ShowCover(null);

        Text = Strings.Get("app.name");
        SetStatus(Strings.Get("main.status.begin"));
    }

    /// <summary>Opens the batch grid over the given paths and steps out of its way.</summary>
    /// <param name="paths">The files to edit.</param>
    /// <param name="thenClose">
    /// Whether closing the grid ends the session. True when the grid is the whole
    /// reason the app started: this window was an artifact of Explorer launching
    /// one process per file, and reappearing empty afterwards is a window nobody
    /// asked for. False when the user opened the grid from a window they were
    /// already working in, which they get back.
    /// </param>
    private void OpenBatch(IEnumerable<string> paths, bool thenClose = false)
    {
        var batch = new BatchForm(_settings, paths);

        batch.FormClosed += (closed, _) =>
        {
            // Never while another grid is open: Application.Run was handed this
            // window, so either answer takes that grid's unsaved edits with it.
            if (IsDisposed || AnyBatchOpen(closed))
            {
                return;
            }

            // Not if a file was opened here in the meantime: the window has become
            // something of its own again, and closing it would discard that.
            if (thenClose && _book is null)
            {
                // Posted rather than called: this runs inside the grid's own close,
                // and ending the message loop from there ends it too.
                BeginInvoke(new Action(Close));
                return;
            }

            Show();
            Activate();
        };

        Hide();
        batch.Show();
    }

    /// <summary>
    /// Whether a batch window other than the one that just closed is still up.
    /// </summary>
    /// <param name="closing">
    /// The window whose <see cref="Form.FormClosed"/> is running. It can still be
    /// listed as open at that point, so it is excluded by identity rather than
    /// trusted to have been removed already.
    /// </param>
    private static bool AnyBatchOpen(object? closing) =>
        Application.OpenForms
            .OfType<BatchForm>()
            .Any(form => !ReferenceEquals(form, closing) && !form.IsDisposed);

    /// <inheritdoc />
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Application.Run was handed this window, so closing it while a grid is open
        // would end the message loop and discard that grid's unsaved edits. Hidden
        // means a grid asked to end the session, which is the one close that goes
        // through.
        if (e.CloseReason == CloseReason.UserClosing && Visible && AnyBatchOpen(null))
        {
            e.Cancel = true;
            Release();
            Hide();
            return;
        }

        base.OnFormClosing(e);
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
                "main.status.format", book.Detected.Format.DisplayName(), book.EntryCount));
            Log.Info($"Opened '{path}' — {book.EntryCount} entries.");
        }
        catch (UnsupportedFormatException ex)
        {
            Log.Warning(ex.Message);

            // Naming the format is the point: "this .cbz is really a PDF" beats
            // "unsupported file". The format name itself is never translated.
            string name = ex.Detected.Format.DisplayName();

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
    private void Populate(BookMetadata m)
    {
        foreach ((MetadataField field, TextBox box) in _fields)
        {
            box.Text = MetadataFields.Read(m, field);
        }

        ShowCover(m.Cover);
    }

    /// <summary>Decodes and shows the cover off the UI thread.</summary>
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

    /// <summary>Disables inputs the format cannot store.</summary>
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
    private void CollectInto(BookMetadata m)
    {
        foreach ((MetadataField field, TextBox box) in _fields)
        {
            MetadataFields.Apply(m, field, box.Text);
        }
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
}
