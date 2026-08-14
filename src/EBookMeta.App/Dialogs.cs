namespace EBookMeta.App;

/// <summary>
/// The chrome every window in this application shares.
/// </summary>
/// <remarks>
/// Four windows were each building the same fixed dialog, the same bottom button
/// strip and the same auto-sizing button, with the sizes drifting apart by a
/// pixel or two as they went. The drift is the reason this exists: a button that
/// is 26 px tall in one dialog and 27 in the next is not a decision anyone made.
/// <para>
/// Every helper here is AutoSize with a floor rather than a fixed width, because
/// German runs about a third longer than English and a fixed <c>Size</c> turns
/// that into a clipped word.
/// </para>
/// </remarks>
internal static class Dialogs
{
    /// <summary>Applies the fixed-size, centred-on-parent modal dialog chrome.</summary>
    /// <param name="form">The dialog to configure.</param>
    /// <param name="titleKey">The <see cref="Strings"/> key for its title.</param>
    /// <param name="size">The client size.</param>
    internal static void Chrome(Form form, string titleKey, Size size)
    {
        form.Text = Strings.Get(titleKey);
        AppIcon.Apply(form);
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.StartPosition = FormStartPosition.CenterParent;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
        form.ShowInTaskbar = false;
        form.AutoScaleMode = AutoScaleMode.Font;
        form.ClientSize = size;
    }

    /// <summary>Builds the right-aligned strip that holds a window's buttons.</summary>
    /// <param name="controls">The buttons, in right-to-left order.</param>
    /// <returns>A panel docked to the bottom of its parent.</returns>
    /// <remarks>
    /// <see cref="FlowDirection.RightToLeft"/> means the first control added sits
    /// rightmost, so callers list the affirmative button last.
    /// </remarks>
    internal static FlowLayoutPanel ButtonStrip(params Control[] controls)
    {
        var strip = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8, 6, 14, 10),
        };

        strip.Controls.AddRange(controls);
        return strip;
    }

    /// <summary>Builds a button that grows to fit its own translated text.</summary>
    /// <param name="key">The <see cref="Strings"/> key for its caption.</param>
    /// <param name="minimumWidth">The width below which it will not shrink.</param>
    /// <returns>The button.</returns>
    internal static Button Action(string key, int minimumWidth = 84) => new()
    {
        Text = Strings.Get(key),
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(minimumWidth, 27),
        Margin = new Padding(4, 3, 4, 3),
    };

    /// <summary>Builds a button that closes its dialog with a result.</summary>
    /// <param name="key">The <see cref="Strings"/> key for its caption.</param>
    /// <param name="result">The result to close with.</param>
    /// <returns>The button.</returns>
    internal static Button Action(string key, DialogResult result)
    {
        Button button = Action(key);
        button.DialogResult = result;
        return button;
    }
}
