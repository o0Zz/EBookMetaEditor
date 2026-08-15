using System.Reflection;

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

        Dialogs.Chrome(this, "about.title", new Size(460, 300));

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

        Label author = Paragraph(Strings.Get("about.author"), SystemColors.GrayText);

        Button ok = Dialogs.Action("button.ok", DialogResult.OK);
        FlowLayoutPanel buttons = Dialogs.ButtonStrip(ok);

        foreach (Control row in new Control[] { name, detail, what, formatsLabel, formats, author })
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
