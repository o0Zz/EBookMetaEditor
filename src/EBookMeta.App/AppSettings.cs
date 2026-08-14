using System.Text;

namespace EBookMeta.App;

/// <summary>
/// User preferences, persisted beside the executable.
/// </summary>
internal sealed class AppSettings
{
    private const string FileName = "settings.json";

    /// <summary>
    /// The interface language, as a two-letter code, or empty to follow Windows.
    /// </summary>
    internal string Language { get; set; } = string.Empty;

    /// <summary>Whether a <c>.bak</c> is left beside a file after saving.</summary>
    internal bool KeepBackupOnSave { get; set; } = true;

    /// <summary>
    /// The extensions the context-menu button registers.
    /// </summary>
    internal List<string> RegisteredExtensions { get; set; } =
        [".epub", ".cbz", ".cbt", ".fb2", ".mobi", ".prc", ".azw", ".azw3"];

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

            foreach (KeyValuePair<string, string> pair in ParseFlatJson(File.ReadAllText(settings.Path)))
            {
                Apply(settings, pair.Key, pair.Value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            // Unreadable or corrupt settings are not worth blocking a launch
            // over; defaults are all perfectly usable.
        }

        return settings;
    }

    /// <summary>Saves settings. Failure is reported, never thrown.</summary>
    /// <returns>An error message, or <see langword="null"/> on success.</returns>
    internal string? TrySave()
    {
        var json = new StringBuilder();
        json.Append("{\n");
        Append(json, "language", Language, quote: true);
        Append(json, "keepBackupOnSave", KeepBackupOnSave ? "true" : "false", quote: false);
        Append(json, "registeredExtensions", string.Join(";", RegisteredExtensions), quote: true);
        json.Length -= 2; // trailing ",\n"
        json.Append("\n}\n");

        try
        {
            File.WriteAllText(Path, json.ToString(), new UTF8Encoding(false));
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

    private static void Append(StringBuilder json, string key, string value, bool quote)
    {
        json.Append("  \"").Append(key).Append("\": ");
        json.Append(quote ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : value);
        json.Append(",\n");
    }

    /// <summary>
    /// Reads a flat JSON object of string and boolean values.
    /// </summary>
    private static Dictionary<string, string> ParseFlatJson(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 0;

        while (i < text.Length)
        {
            int keyStart = text.IndexOf('"', i);
            if (keyStart < 0)
            {
                break;
            }

            int keyEnd = text.IndexOf('"', keyStart + 1);
            if (keyEnd < 0)
            {
                break;
            }

            string key = text.Substring(keyStart + 1, keyEnd - keyStart - 1);

            int colon = text.IndexOf(':', keyEnd);
            if (colon < 0)
            {
                break;
            }

            i = colon + 1;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            if (text[i] == '"')
            {
                int valueEnd = text.IndexOf('"', i + 1);
                if (valueEnd < 0)
                {
                    break;
                }

                result[key] = text.Substring(i + 1, valueEnd - i - 1)
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\");

                i = valueEnd + 1;
            }
            else
            {
                int valueEnd = i;
                while (valueEnd < text.Length && text[valueEnd] is not (',' or '}' or '\n' or '\r'))
                {
                    valueEnd++;
                }

                result[key] = text.Substring(i, valueEnd - i).Trim();
                i = valueEnd;
            }
        }

        return result;
    }
}
