using System.Text;

namespace EBookMeta.App;

/// <summary>User preferences, persisted beside the executable.</summary>
internal sealed class AppSettings
{
    // Named after the exe, like EBookMetaEditor.log beside it. Sections are not
    // supported and not needed: three keys, one flat file.
    private const string FileName = "EBookMetaEditor.ini";

    /// <summary>
    /// The interface language, as a two-letter code, or empty to follow Windows.
    /// </summary>
    internal string Language { get; set; } = string.Empty;

    /// <summary>Whether a <c>.bak</c> is left beside a file after saving.</summary>
    internal bool KeepBackupOnSave { get; set; } = true;

    /// <summary>The extensions the context-menu button registers.</summary>
    internal List<string> RegisteredExtensions { get; set; } =
        [.. ShellRegistration.SupportedExtensions];

    /// <summary>The file the settings were loaded from and will be saved to.</summary>
    internal string Path { get; private set; } = string.Empty;

    /// <summary>Loads settings, falling back to defaults for anything missing.</summary>
    /// <returns>The loaded settings.</returns>
    internal static AppSettings Load()
    {
        var settings = new AppSettings { Path = ResolvePath() };

        try
        {
            if (!File.Exists(settings.Path))
            {
                return settings;
            }

            using var reader = new StreamReader(
                settings.Path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);

            foreach (KeyValuePair<string, string> pair in KeyValueFile.Read(reader))
            {
                Apply(settings, pair.Key, pair.Value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable settings file is not worth blocking a launch over;
            // the defaults are all perfectly usable.
        }

        return settings;
    }

    /// <summary>Saves settings. Failure is reported, never thrown.</summary>
    /// <returns>An error message, or <see langword="null"/> on success.</returns>
    internal string? TrySave()
    {
        var text = new StringBuilder();
        Append(text, "language", Language);
        Append(text, "keepBackupOnSave", KeepBackupOnSave ? "true" : "false");
        Append(text, "registeredExtensions", string.Join(";", RegisteredExtensions));

        try
        {
            File.WriteAllText(Path, text.ToString(), new UTF8Encoding(false));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Strings.Format("settings.saveFailed", Path, ex.Message);
        }
    }

    private static void Apply(AppSettings settings, string key, string value)
    {
        switch (key)
        {
            case "language":
                settings.Language = value.Trim().ToLowerInvariant();
                break;
            case "keepBackupOnSave":
                settings.KeepBackupOnSave = value == "true";
                break;
            case "registeredExtensions":
                settings.RegisteredExtensions = value
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.Trim().ToLowerInvariant())
                    .ToList();
                break;
        }
    }

    private static string ResolvePath()
    {
        string beside = System.IO.Path.Combine(AppContext.BaseDirectory, FileName);

        if (IsWritable(AppContext.BaseDirectory))
        {
            return beside;
        }

        // Unzipped into Program Files, or onto read-only media. Falling back is
        // better than pretending the save worked.
        string roaming = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EBookMetaEditor");

        Directory.CreateDirectory(roaming);
        return System.IO.Path.Combine(roaming, FileName);
    }

    private static bool IsWritable(string directory)
    {
        string probe = System.IO.Path.Combine(directory, ".ebookmetaeditor-write-probe");

        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Append(StringBuilder text, string key, string value) =>
        text.Append(key).Append(" = ").Append(value).AppendLine();
}
