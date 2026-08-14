using System.Text;

namespace EBookMeta.App;

/// <summary>
/// Shows what the application has done this session.
/// </summary>
/// <remarks>
/// <para>
/// An edit box rather than a list view, on purpose: a log is something you select
/// and paste into a bug report, and a read-only multiline text box supports
/// Ctrl+A and Ctrl+C for free. A grid looks tidier and fights you the moment you
/// want to hand the contents to somebody.
/// </para>
/// <para>
/// It reads <see cref="Log.Entries"/> directly and appends live through
/// <see cref="Log.Written"/>, so nothing has to be read back off disk to be
/// displayed. The handler marshals to the UI thread because Core raises the event
/// on whichever thread logged.
/// </para>
/// </remarks>
internal sealed class LogForm : Form
{
    private readonly TextBox _text = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Dock = DockStyle.Fill,
        BackColor = SystemColors.Window,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private readonly CheckBox _includeDebug = new()
    {
        Text = Strings.Get("log.includeDebug"),
        AutoSize = true,
        Checked = true,
    };

    private readonly Label _fileNote = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = SystemColors.GrayText,
        AutoEllipsis = true,
    };

    private readonly Action<LogEntry> _onWritten;

    /// <summary>Creates the log window.</summary>
    internal LogForm()
    {
        Text = Strings.Get("log.title");
        AppIcon.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(860, 480);
        MinimumSize = new Size(520, 300);
        ShowInTaskbar = false;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(8, 6, 8, 6),
        };

        // AutoSize with a floor rather than a fixed size: "Speichern unter…" does
        // not fit in the 90 px that "Save as…" needed.
        var close = Action("button.close");
        var save = Action("log.saveAs");
        var copy = Action("log.copy");

        close.DialogResult = DialogResult.OK;

        save.Click += (_, _) => SaveAs();
        copy.Click += (_, _) => CopyAll();

        buttons.Controls.Add(close);
        buttons.Controls.Add(save);
        buttons.Controls.Add(copy);

        var options = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            ColumnCount = 2,
            Padding = new Padding(8, 4, 8, 0),
        };

        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.Controls.Add(_includeDebug, 0, 0);
        options.Controls.Add(_fileNote, 1, 0);

        _includeDebug.CheckedChanged += (_, _) => Reload();

        Controls.Add(_text);
        Controls.Add(options);
        Controls.Add(buttons);

        AcceptButton = close;
        CancelButton = close;

        _onWritten = OnWritten;
        Log.Written += _onWritten;

        Reload();
    }

    private static Button Action(string key) => new()
    {
        Text = Strings.Get(key),
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(84, 27),
        Margin = new Padding(4, 3, 4, 3),
    };

    /// <inheritdoc/>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // The log outlives this window, so a subscription left behind would keep
        // it alive and keep appending to a disposed control.
        Log.Written -= _onWritten;
        base.OnFormClosed(e);
    }

    private void OnWritten(LogEntry entry)
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (Visible && Include(entry))
                {
                    Append(entry);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The window went away between the check and the marshal. Nothing to
            // show it on, and a log viewer must never be the thing that crashes.
        }
    }

    private bool Include(LogEntry entry) =>
        _includeDebug.Checked || entry.Level > LogLevel.Debug;

    private void Reload()
    {
        var builder = new StringBuilder(8192);

        foreach (LogEntry entry in Log.Entries)
        {
            if (Include(entry))
            {
                builder.Append(entry).Append(Environment.NewLine);
            }
        }

        _text.Text = builder.Length == 0 ? Strings.Get("log.empty") : builder.ToString();
        ScrollToEnd();

        _fileNote.Text = Log.FilePath is null
            ? Strings.Get("log.memoryOnly")
            : Log.FileWritten
                ? Strings.Format("log.written", Log.FilePath)
                : Strings.Format("log.willWrite", Log.FilePath);
    }

    private void Append(LogEntry entry)
    {
        // Compared against the placeholder itself rather than against a prefix of
        // the English one, which stopped being a safe assumption the moment the
        // window learned to speak anything else.
        if (_text.Text == Strings.Get("log.empty"))
        {
            _text.Clear();
        }

        _text.AppendText(entry + Environment.NewLine);
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        _text.SelectionStart = _text.TextLength;
        _text.ScrollToCaret();
    }

    private void CopyAll()
    {
        if (_text.TextLength == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_text.Text);
            Log.Debug("Log copied to the clipboard.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process holds the clipboard. Not worth an error dialog.
            Log.Warning("The clipboard was unavailable, so the log was not copied.");
        }
    }

    private void SaveAs()
    {
        using var dialog = new SaveFileDialog
        {
            Title = Strings.Get("log.save.title"),
            Filter = $"{Strings.Get("filter.log")}|*.log"
                + $"|{Strings.Get("filter.text")}|*.txt"
                + $"|{Strings.Get("filter.all")}|*.*",
            FileName = "ebookmetaeditor.log",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, Log.Format(), new UTF8Encoding(false));
            Log.Info($"Log saved to '{dialog.FileName}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                Strings.Format("log.saveFailed", ex.Message),
                Strings.Get("app.name"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
