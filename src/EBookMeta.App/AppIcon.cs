using System.Reflection;

namespace EBookMeta.App;

/// <summary>
/// The application icon, embedded in the executable and shared by every window.
/// </summary>
internal static class AppIcon
{
    private static Icon? _shared;
    private static bool _loaded;

    /// <summary>
    /// The icon to assign to <see cref="Form.Icon"/>, or <see langword="null"/>
    /// if it could not be loaded.
    /// </summary>
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
