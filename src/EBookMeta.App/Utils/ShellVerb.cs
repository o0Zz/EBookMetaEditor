using Microsoft.Win32;

namespace EBookMeta.App.Utils;

/// <summary>
/// One verb in Explorer's right-click menu, per user and without elevation. Knows
/// nothing about what the verb is for: the caller says what it should read and which
/// extensions get it.
/// </summary>
internal sealed class ShellVerb
{
    private readonly string _keyName;

    /// <summary>Names the verb.</summary>
    /// <param name="keyName">
    /// The registry key the verb lives under, unique to the application — it is what
    /// tells this program's entry from every other program's.
    /// </param>
    internal ShellVerb(string keyName)
    {
        _keyName = keyName;
    }

    /// <summary>Whether the verb is registered for one extension.</summary>
    /// <param name="extension">The extension, with a leading dot.</param>
    /// <returns><see langword="true"/> if registered.</returns>
    internal bool IsRegistered(string extension)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PathFor(extension));
            return key is not null;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Puts the verb on one extension, pointing at this executable.</summary>
    /// <param name="extension">The extension, with a leading dot.</param>
    /// <param name="label">What Explorer should show in the menu.</param>
    /// <exception cref="IOException">The registry key could not be created.</exception>
    internal void Register(string extension, string label)
    {
        string exe = ExecutablePath();

        // SystemFileAssociations, never HKCU\Software\Classes\<.ext>, which would
        // hijack the user's choice of default application for the type.
        using RegistryKey verb = Registry.CurrentUser.CreateSubKey(PathFor(extension))
            ?? throw new IOException($"Could not create the registry key for {extension}.");

        // Explorer shows this text and cannot ask what it should say, so it is stored,
        // not looked up live — a change of language has to re-register.
        verb.SetValue(null, label);
        verb.SetValue("Icon", exe + ",0");

        // Asks Explorer to invoke the verb once with the whole selection instead of
        // once per file. A request, not a guarantee — past Explorer's own item limit
        // it forks per file.
        verb.SetValue("MultiSelectModel", "Player");

        using RegistryKey command = verb.CreateSubKey("command")
            ?? throw new IOException($"Could not create the command key for {extension}.");

        // %1 quoted: paths with spaces are the norm, not the exception, and an
        // unquoted %1 would hand the application a truncated path.
        command.SetValue(null, $"\"{exe}\" \"%1\"");
    }

    /// <summary>Takes the verb off one extension. Missing is not an error.</summary>
    /// <param name="extension">The extension, with a leading dot.</param>
    internal void Unregister(string extension) =>
        Registry.CurrentUser.DeleteSubKeyTree(PathFor(extension), throwOnMissingSubKey: false);

    private string PathFor(string extension) =>
        $@"Software\Classes\SystemFileAssociations\{extension}\shell\{_keyName}";

    private static string ExecutablePath()
    {
        string path = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty;

        // Fall back to the process path when the entry assembly has no location,
        // which happens under some hosting scenarios.
        return path.Length > 0
            ? path
            : System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }
}
