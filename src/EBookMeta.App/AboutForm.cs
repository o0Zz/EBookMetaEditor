using System.Reflection;
using EBookMeta.Formats;

namespace EBookMeta.App;

/// <summary>
/// The About box: what this is, what version, and what it can open.
/// </summary>
internal sealed class AboutForm : Form
{
    /// <summary>Creates the About box.</summary>
    internal AboutForm()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        Text = Strings.Get("about.title");
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(460, 300);

        // Stacked by a layout panel rather than placed at coordinates, so a
        // translation that runs to four lines where English took three pushes
        // what follows down instead of disappearing underneath it.
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16, 14, 16, 8),
            AutoScroll = true,
        };

        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var name = new Label
        {
            Text = Strings.Get("app.name"),
            Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        };

        var detail = new Label
        {
            Text = Strings.Format("about.version", version),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(2, 0, 0, 12),
        };

        Label what = Paragraph(Strings.Get("about.what"), SystemColors.ControlText);

        var formatsLabel = new Label
        {
            Text = Strings.Get("about.formats"),
            AutoSize = true,
            Margin = new Padding(2, 8, 0, 2),
        };

        var formats = new Label
        {
            Text = BookFormats.All.Count == 0
                ? Strings.Get("about.formats.none")
                : string.Join(", ", BookFormats.All.Select(h => h.Id.DisplayName()).OrderBy(n => n)),
            AutoSize = true,
            Margin = new Padding(2, 0, 0, 12),
        };

        Label note = Paragraph(Strings.Get("about.note"), SystemColors.GrayText);

        var ok = new Button
        {
            Text = Strings.Get("button.ok"),
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(80, 27),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8, 4, 16, 10),
        };

        buttons.Controls.Add(ok);

        foreach (Control row in new Control[] { name, detail, what, formatsLabel, formats, note })
        {
            body.RowCount++;
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(row);
        }

        Controls.Add(body);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = ok;
    }

    /// <summary>A paragraph that wraps to the dialog's width and grows downwards.</summary>
    private static Label Paragraph(string text, Color colour) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(424, 0),
        ForeColor = colour,
        Margin = new Padding(2, 0, 0, 4),
    };
}
