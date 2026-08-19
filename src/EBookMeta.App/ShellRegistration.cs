namespace EBookMeta.App;

/// <summary>
/// What this application puts in Explorer's right-click menu: the "Edit metadata"
/// verb, on the extensions the registered formats claim. The registry mechanics are
/// <see cref="Utils.ShellVerb"/>; everything here is the policy over them.
/// </summary>
internal static class ShellRegistration
{
    /// <summary>
    /// The key the verb lives under. Unique to this application, and never changed:
    /// a new name would orphan every registration a previous version made.
    /// </summary>
    private static readonly ShellVerb Verb = new("EBookMetaEditorEdit");

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
                    Verb.Register(extension, Strings.Get("shell.verb"));
                }
                else
                {
                    Verb.Unregister(extension);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Strings.Format("shell.updateFailed", ex.Message);
        }
    }

    /// <summary>Removes the verb from every supported extension.</summary>
    /// <returns>An error message, or <see langword="null"/> on success.</returns>
    internal static string? RemoveAll() => Apply([]);

    /// <summary>Whether the verb is currently registered for any supported extension.</summary>
    /// <returns><see langword="true"/> if at least one registration exists.</returns>
    internal static bool IsRegisteredForAny() => SupportedExtensions.Any(IsRegistered);

    /// <summary>Whether the verb is registered for one extension.</summary>
    /// <param name="extension">The extension, with a leading dot.</param>
    /// <returns><see langword="true"/> if registered.</returns>
    internal static bool IsRegistered(string extension) => Verb.IsRegistered(extension);
}
