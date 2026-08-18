using Microsoft.Win32;

namespace EBookMeta.App;

/// <summary>
/// Adds and removes the "Edit metadata" entry in Explorer's right-click menu.
/// </summary>
internal static class ShellRegistration
{
    private const string VerbKeyName = "EBookMetaEditorEdit";

    /// <summary>The extensions EBookMetaEditor can actually open.</summary>
    internal static IReadOnlyList<string> SupportedExtensions { get; } =
    [
        .. BookFormats.All
            .SelectMany(f => f.Extensions)

            // SystemFileAssociations keys on a single extension, so a compound one
            // like .fb2.zip could only be registered as ".zip" — which would put this
            // app's verb on every archive on the machine.
            .Where(e => e.IndexOf('.', 1) < 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.Ordinal),
    ];

    /// <summary>Registers the verb for the given extensions and removes it from the rest.</summary>
    /// <param name="extensions">The extensions to register, each with a leading dot.</param>
    /// <returns>An error message, or <see langword="null"/> on success.</returns>
    internal static string? Apply(IEnumerable<string> extensions)
    {
        var wanted = new HashSet<string>(
            extensions.Select(e => e.Trim().ToLowerInvariant()), StringComparer.Ordinal);

        try
        {
            foreach (string extension in SupportedExtensions)
            {
                if (wanted.Contains(extension))
                {
                    Register(extension);
                }
                else
                {
                    Unregister(extension);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Strings.Format("shell.updateFailed", ex.Message);
        }
    }

    /// <summary>Removes the verb for every supported extension.</summary>
    /// <returns>An error message, or <see langword="null"/> on success.</returns>
    internal static string? RemoveAll() => Apply([]);

    /// <summary>Whether the verb is currently registered for any supported extension.</summary>
    /// <returns><see langword="true"/> if at least one registration exists.</returns>
    internal static bool IsRegisteredForAny() =>
        SupportedExtensions.Any(IsRegistered);

    /// <summary>Whether the verb is registered for one extension.</summary>
    /// <param name="extension">The extension, with a leading dot.</param>
    /// <returns><see langword="true"/> if registered.</returns>
    internal static bool IsRegistered(string extension)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(VerbPath(extension));
            return key is not null;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void Register(string extension)
    {
        string exe = ExecutablePath();

        using RegistryKey verb = Registry.CurrentUser.CreateSubKey(VerbPath(extension))
            ?? throw new IOException($"Could not create the registry key for {extension}.");

        // Explorer shows this text and cannot ask what it should say, so it is stored,
        // not looked up live — changing language re-registers in SettingsForm.Commit.
        verb.SetValue(null, Strings.Get("shell.verb"));
        verb.SetValue("Icon", exe + ",0");

        // Asks Explorer to invoke the verb once with the whole selection instead of
        // once per file. A request, not a guarantee — past Explorer's own item limit
        // it forks per file, and SingleInstance forwarding covers that.
        verb.SetValue("MultiSelectModel", "Player");

        using RegistryKey command = verb.CreateSubKey("command")
            ?? throw new IOException($"Could not create the command key for {extension}.");

        // %1 quoted: paths with spaces are the norm, not the exception, and an
        // unquoted %1 would hand the app a truncated path.
        command.SetValue(null, $"\"{exe}\" \"%1\"");
    }

    private static void Unregister(string extension) =>
        Registry.CurrentUser.DeleteSubKeyTree(VerbPath(extension), throwOnMissingSubKey: false);

    private static string VerbPath(string extension) =>
        $@"Software\Classes\SystemFileAssociations\{extension}\shell\{VerbKeyName}";

    private static string ExecutablePath()
    {
        string path = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty;

        // Fall back to the process path when the entry assembly has no location,
        // which happens under some hosting scenarios.
        return path.Length > 0
            ? path
            : System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "EBookMetaEditor.exe";
    }
}
