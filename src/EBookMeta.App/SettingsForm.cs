namespace EBookMeta.App;

/// <summary>
/// The settings dialog, reached from <b>File ▸ Settings</b>.
/// </summary>
/// <remarks>
/// Also the home of context-menu registration. There is no separate setup
/// program: a tool you unzip and run should be able to hook and unhook itself
/// from Explorer without an installer.
/// </remarks>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly CheckBox _keepBackup;
    private readonly CheckBox _validateFully;
    private readonly CheckBox _rememberGeometry;
    private readonly CheckedListBox _extensions;
    private readonly Button _contextMenu;

    /// <summary>Creates the dialog over the given settings.</summary>
    /// <param name="settings">The settings to edit. Mutated only on OK.</param>
    internal SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(420, 380);

        _keepBackup = new CheckBox
        {
            Text = "Keep a .bak backup beside the file after saving",
            Checked = settings.KeepBackupOnSave,
            Location = new Point(16, 16),
            AutoSize = true,
        };

        _validateFully = new CheckBox
        {
            Text = "Run all validation checks when a file opens",
            Checked = settings.ValidateFullyOnOpen,
            Location = new Point(16, 44),
            AutoSize = true,
        };

        var validateNote = new Label
        {
            // Say why the default is off, so the setting reads as a trade-off
            // rather than a missing feature.
            Text = "Some checks must read every entry in the archive. Leaving this off "
                 + "keeps opening instant; those checks run when you view the findings.",
            Location = new Point(34, 66),
            Size = new Size(370, 34),
            ForeColor = SystemColors.GrayText,
        };

        _rememberGeometry = new CheckBox
        {
            Text = "Remember the window size and position",
            Checked = settings.RememberWindowGeometry,
            Location = new Point(16, 106),
            AutoSize = true,
        };

        var extensionsLabel = new Label
        {
            Text = "Show \"Edit metadata\" in the right-click menu for:",
            Location = new Point(16, 142),
            AutoSize = true,
        };

        _extensions = new CheckedListBox
        {
            Location = new Point(16, 164),
            Size = new Size(388, 60),
            CheckOnClick = true,
            IntegralHeight = false,
        };

        foreach (string extension in ShellRegistration.SupportedExtensions)
        {
            _extensions.Items.Add(extension, settings.RegisteredExtensions.Contains(extension));
        }

        _contextMenu = new Button
        {
            Location = new Point(16, 234),
            Size = new Size(388, 32),
        };

        _contextMenu.Click += OnContextMenuClicked;
        UpdateContextMenuButton();

        var registrationNote = new Label
        {
            Text = "Registers under your account only. It does not change which "
                 + "application opens these files by default. On Windows 11 the entry "
                 + "appears under \"Show more options\".",
            Location = new Point(16, 272),
            Size = new Size(388, 48),
            ForeColor = SystemColors.GrayText,
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(248, 334),
            Size = new Size(75, 26),
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(329, 334),
            Size = new Size(75, 26),
        };

        ok.Click += (_, _) => Commit();

        Controls.AddRange(
        [
            _keepBackup, _validateFully, validateNote, _rememberGeometry,
            extensionsLabel, _extensions, _contextMenu, registrationNote, ok, cancel,
        ]);

        AcceptButton = ok;
        CancelButton = cancel;
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
            MessageBox.Show(this, error, "EBookMetaEditor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateContextMenuButton();
    }

    private void UpdateContextMenuButton()
    {
        bool registered = ShellRegistration.IsRegisteredForAny();

        _contextMenu.Text = registered
            ? "Remove from context menu"
            : "Add to context menu";

        _extensions.Enabled = !registered;
    }

    private void Commit()
    {
        _settings.KeepBackupOnSave = _keepBackup.Checked;
        _settings.ValidateFullyOnOpen = _validateFully.Checked;
        _settings.RememberWindowGeometry = _rememberGeometry.Checked;
        _settings.RegisteredExtensions = CheckedExtensions();

        // Keep the registry in step with the tick boxes, but only where the verb
        // is already in use — pressing OK should not silently register anything
        // the user did not ask for with the button.
        if (ShellRegistration.IsRegisteredForAny())
        {
            ShellRegistration.Apply(_settings.RegisteredExtensions);
        }

        string? error = _settings.TrySave();
        if (error is not null)
        {
            MessageBox.Show(this, error, "EBookMetaEditor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
