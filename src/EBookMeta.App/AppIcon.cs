using System.Reflection;

namespace EBookMeta.App;

/// <summary>
/// The application icon, embedded in the executable and shared by every window.
/// </summary>
/// <remarks>
/// <para>
/// Loaded from a managed resource rather than a file beside the exe: the product
/// is a single executable, so there is no loose <c>icon.ico</c> to read at run
/// time. It is loaded once, lazily, on first window creation — a few hundred
/// kilobytes of memcpy plus the GDI handles WinForms derives from it, which is
/// affordable against the startup budget only because it happens once.
/// </para>
/// <para>
/// A missing or unreadable icon returns <see langword="null"/> and leaves the
/// default WinForms icon in place. Failing a launch over decoration would be
/// absurd.
/// </para>
/// </remarks>
internal static class AppIcon
{
    private static Icon? _shared;
    private static bool _loaded;

    /// <summary>
    /// The icon to assign to <see cref="Form.Icon"/>, or <see langword="null"/>
    /// if it could not be loaded.
    /// </summary>
    /// <remarks>
    /// The instance is shared and never disposed: it lives as long as the
    /// process, and the forms that use it must not dispose it either.
    /// </remarks>
    internal static Icon? Shared
    {
        get
        {
            if (_loaded)
            {
                return _shared;
            }

            _loaded = true;

            try
            {
                using Stream? stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("EBookMetaEditor.icon.ico");

                if (stream is not null)
                {
                    _shared = new Icon(stream);
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Application icon could not be loaded: {ex.Message}");
            }

            return _shared;
        }
    }

    /// <summary>Gives a window the application icon, if there is one.</summary>
    /// <param name="form">The window to decorate.</param>
    internal static void Apply(Form form)
    {
        Icon? icon = Shared;

        if (icon is not null)
        {
            form.Icon = icon;
        }
    }
}
