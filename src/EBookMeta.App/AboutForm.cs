using System.Reflection;
using EBookMeta.Formats;

namespace EBookMeta.App;

/// <summary>
/// The About box: what this is, what version, and what it can open.
/// </summary>
/// <remarks>
/// The format list is read from <see cref="BookFormats"/> rather than typed out,
/// so it cannot drift from what the build actually supports. Registering a handler
/// updates this box for free.
/// </remarks>
internal sealed class AboutForm : Form
{
    /// <summary>Creates the About box.</summary>
    internal AboutForm()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        Text = "About EBookMetaEditor";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(430, 260);

        var name = new Label
        {
            Text = "EBookMetaEditor",
            Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
            Location = new Point(16, 16),
            AutoSize = true,
        };

        var detail = new Label
        {
            Text = $"Version {version}   ·   .NET Framework 4.8",
            Location = new Point(18, 46),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };

        var what = new Label
        {
            Text = "A fast metadata editor for ebooks and comics. Right-click a file, "
                 + "fix the metadata, close. Broken XML is repaired on open; the file on "
                 + "disk is only changed when you save.",
            Location = new Point(18, 76),
            Size = new Size(394, 56),
        };

        var formatsLabel = new Label
        {
            Text = "Editable formats:",
            Location = new Point(18, 140),
            AutoSize = true,
        };

        var formats = new Label
        {
            Text = BookFormats.All.Count == 0
                ? "(none registered)"
                : string.Join(", ", BookFormats.All.Select(h => FormatIds.ToDisplayName(h.Id)).OrderBy(n => n)),
            Location = new Point(18, 160),
            Size = new Size(394, 20),
        };

        var note = new Label
        {
            Text = "Other formats are identified but not opened, so a .cbz that is "
                 + "really a RAR archive is named rather than mangled.",
            Location = new Point(18, 184),
            Size = new Size(394, 34),
            ForeColor = SystemColors.GrayText,
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(337, 224),
            Size = new Size(75, 26),
        };

        Controls.AddRange([name, detail, what, formatsLabel, formats, note, ok]);

        AcceptButton = ok;
        CancelButton = ok;
    }
}
