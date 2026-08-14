using Microsoft.Win32;

namespace EBookMeta.App;

/// <summary>
/// Adds and removes the "Edit metadata" entry in Explorer's right-click menu.
/// </summary>
/// <remarks>
/// <para>
/// Per-user only. Everything here is written under <c>HKCU</c>, so registering
/// needs no administrator rights and no installer — which is the point of a tool
/// you unzip and run.
/// </para>
/// <para>
/// Three rules that are easy to get wrong and unkind to the user when you do:
/// </para>
/// <para>
/// <b>Use <c>SystemFileAssociations</c>, not <c>HKCU\Software\Classes\.ext</c>.</b>
/// The latter hijacks the file's default association, so double-clicking a book
/// would open EBookMetaEditor instead of the user's reader. This adds a verb to the
/// menu and changes nothing else.
/// </para>
/// <para>
/// <b>Never touch <c>HKCU\...\Explorer\FileExts</c>.</b> That key records the
/// user's explicit choice of default application. It is theirs.
/// </para>
/// <para>
/// <b>Never write to <c>HKLM</c>.</b> It would require elevation and affect
/// every account on the machine.
/// </para>
/// </remarks>
internal static class ShellRegistration
{
    private const string VerbKeyName = "EBookMetaEditorEdit";
    private const string VerbLabel = "Edit metadata";

    /// <summary>The extensions EBookMetaEditor can actually open.</summary>
    /// <remarks>
    /// Only these two. Registering a verb for a format the app would refuse to
    /// open is a worse experience than not offering it.
    /// </remarks>
    internal static IReadOnlyList<string> SupportedExtensions { get; } = [".epub", ".cbz"];

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
            return $"The context menu could not be updated: {ex.Message}";
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

        verb.SetValue(null, VerbLabel);
        verb.SetValue("Icon", exe + ",0");

        using RegistryKey command = verb.CreateSubKey("command")
            ?? throw new IOException($"Could not create the command key for {extension}.");

        // %1 quoted: paths with spaces are the norm, not the exception, and an
        // unquoted %1 would hand the app a truncated path.
        command.SetValue(null, $"\"{exe}\" \"%1\"");
    }

    private static void Unregister(string extension)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(VerbPath(extension), throwOnMissingSubKey: false);
        }
        catch (ArgumentException)
        {
            // Already absent, which is the state we wanted.
        }
    }

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
