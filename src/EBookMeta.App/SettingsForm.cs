namespace EBookMeta.App;

/// <summary>
/// The settings dialog, reached from <b>File ▸ Settings</b>.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly ComboBox _language;
    private readonly CheckBox _keepBackup;
    private readonly CheckedListBox _extensions;
    private readonly Button _contextMenu;

    /// <summary>Creates the dialog over the given settings.</summary>
    /// <param name="settings">The settings to edit. Mutated only on OK.</param>
    internal SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Dialogs.Chrome(this, "settings.title", new Size(460, 405));

        _language = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220,
            Margin = new Padding(3, 3, 3, 10),
        };

        FillLanguages();

        _keepBackup = new CheckBox
        {
            Text = Strings.Get("settings.keepBackup"),
            Checked = settings.KeepBackupOnSave,
            AutoSize = true,
            Margin = new Padding(3, 3, 3, 10),
        };

        var extensionsLabel = new Label
        {
            Text = Strings.Get("settings.extensions"),
            AutoSize = true,
        };

        _extensions = new CheckedListBox
        {
            Height = 58,
            Width = 420,
            CheckOnClick = true,
            IntegralHeight = false,
        };

        foreach (string extension in ShellRegistration.SupportedExtensions)
        {
            _extensions.Items.Add(extension, settings.RegisteredExtensions.Contains(extension));
        }

        _contextMenu = new Button
        {
            Width = 420,
            Height = 32,
            Margin = new Padding(3, 8, 3, 3),
        };

        _contextMenu.Click += OnContextMenuClicked;
        UpdateContextMenuButton();

        Label registrationNote = new()
        {
            Text = Strings.Get("settings.registrationNote"),
            AutoSize = true,
            MaximumSize = new Size(414, 0),
            Margin = new Padding(3, 2, 3, 6),
            ForeColor = SystemColors.GrayText,
        };

        Controls.Add(BuildLayout(extensionsLabel, registrationNote));
        Controls.Add(BuildButtons());
    }

    /// <summary>
    /// One column of controls, each sized to whatever its translation needs.
    /// </summary>
    private TableLayoutPanel BuildLayout(Label extensionsLabel, Label registrationNote)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(14, 12, 14, 6),
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var languageRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
        };

        languageRow.Controls.Add(new Label
        {
            Text = Strings.Get("settings.language"),
            AutoSize = true,
            Margin = new Padding(3, 7, 8, 3),
        });

        languageRow.Controls.Add(_language);

        Control[] rows =
        [
            languageRow, _keepBackup,
            extensionsLabel, _extensions, _contextMenu, registrationNote,
        ];

        foreach (Control row in rows)
        {
            layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(row);
        }

        return layout;
    }

    private FlowLayoutPanel BuildButtons()
    {
        Button ok = Dialogs.Action("button.ok", DialogResult.OK);
        Button cancel = Dialogs.Action("button.cancel", DialogResult.Cancel);

        ok.Click += (_, _) => Commit();

        // Rightmost first: the strip flows right to left.
        FlowLayoutPanel buttons = Dialogs.ButtonStrip(cancel, ok);

        AcceptButton = ok;
        CancelButton = cancel;

        return buttons;
    }

    /// <summary>
    /// Offers every language embedded in the exe, plus following Windows.
    /// </summary>
    private void FillLanguages()
    {
        // Deliberately first and deliberately the default: the right answer for
        // most people is the language their computer is already in.
        _language.Items.Add(new Strings.Language(string.Empty, Strings.Get("settings.language.auto")));

        foreach (Strings.Language language in Strings.Available)
        {
            _language.Items.Add(language);
        }

        int chosen = 0;

        for (int i = 1; i < _language.Items.Count; i++)
        {
            if (((Strings.Language)_language.Items[i]).Code == _settings.Language)
            {
                chosen = i;
                break;
            }
        }

        _language.SelectedIndex = chosen;
    }

    /// <summary>
    /// The chosen extensions, read from the list rather than from settings, so
    /// the button acts on what the user has ticked right now.
    /// </summary>
    private List<string> CheckedExtensions() =>
        _extensions.CheckedItems.Cast<string>().ToList();

    private void OnContextMenuClicked(object? sender, EventArgs e)
    {
        // One button whose meaning follows the current state: it adds the entry
        // when absent and removes it when present, so registration is reversible
        // without a second control.
        string? error = ShellRegistration.IsRegisteredForAny()
            ? ShellRegistration.RemoveAll()
            : ShellRegistration.Apply(CheckedExtensions());

        if (error is not null)
        {
            MessageBox.Show(this, error, Strings.Get("app.name"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateContextMenuButton();
    }

    private void UpdateContextMenuButton()
    {
        bool registered = ShellRegistration.IsRegisteredForAny();

        _contextMenu.Text = Strings.Get(registered
            ? "settings.contextMenu.remove"
            : "settings.contextMenu.add");

        _extensions.Enabled = !registered;
    }

    private void Commit()
    {
        // Language first: everything below it that produces text — a registration
        // failure, the verb written into the registry — should come out in the
        // language the user has just chosen, not the one they are leaving.
        _settings.Language = _language.SelectedItem is Strings.Language chosen ? chosen.Code : string.Empty;
        Strings.Use(_settings.Language);

        _settings.KeepBackupOnSave = _keepBackup.Checked;
        _settings.RegisteredExtensions = CheckedExtensions();

        // Keep the registry in step with the tick boxes, but only where the verb
        // is already in use — pressing OK should not silently register anything
        // the user did not ask for with the button. This is also what re-labels
        // the Explorer entry when the language changed.
        if (ShellRegistration.IsRegisteredForAny())
        {
            ShellRegistration.Apply(_settings.RegisteredExtensions);
        }

        string? error = _settings.TrySave();
        if (error is not null)
        {
            MessageBox.Show(this, error, Strings.Get("app.name"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
