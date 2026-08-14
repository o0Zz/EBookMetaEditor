using System.Globalization;
using System.Text;

namespace EBookMeta.App;

/// <summary>
/// User preferences, persisted beside the executable.
/// </summary>
/// <remarks>
/// <para>
/// Stored in the application's own folder, per the promise that EBookMetaEditor keeps
/// no configuration elsewhere — unzip it, use it, delete the folder and nothing
/// is left behind. If that folder is read-only, which it will be for an unzip
/// into Program Files, it falls back to <c>%APPDATA%</c> rather than silently
/// discarding the user's choices.
/// </para>
/// <para>
/// Serialised by hand. The schema is four flags and a list of extensions;
/// pulling in a JSON library for that would cost more startup than the whole
/// settings load, and cold launch has a 400 ms budget.
/// </para>
/// </remarks>
internal sealed class AppSettings
{
    private const string FileName = "settings.json";

    /// <summary>
    /// The interface language, as a two-letter code, or empty to follow Windows.
    /// </summary>
    /// <remarks>
    /// Empty by default, which is the answer that is right without being asked:
    /// a French user's first launch is in French. An explicit choice is for the
    /// case the default gets wrong — an English interface on a German Windows,
    /// which is a preference no amount of detection can guess.
    /// </remarks>
    internal string Language { get; set; } = string.Empty;

    /// <summary>Whether a <c>.bak</c> is left beside a file after saving.</summary>
    internal bool KeepBackupOnSave { get; set; } = true;

    /// <summary>Whether the window's size and position are restored between launches.</summary>
    internal bool RememberWindowGeometry { get; set; } = true;

    /// <summary>The last window bounds, when <see cref="RememberWindowGeometry"/> is on.</summary>
    internal Rectangle WindowBounds { get; set; } = Rectangle.Empty;

    /// <summary>Whether the window was last maximised.</summary>
    internal bool WindowMaximised { get; set; }

    /// <summary>The last bounds of the batch window, which is sized separately.</summary>
    /// <remarks>
    /// Its own setting rather than sharing the editor's, because the two windows
    /// want different shapes: a form over one file is tall and narrow, a grid over
    /// four hundred is wide.
    /// </remarks>
    internal Rectangle BatchWindowBounds { get; set; } = Rectangle.Empty;

    /// <summary>Whether the batch window was last maximised.</summary>
    internal bool BatchWindowMaximised { get; set; }

    /// <summary>
    /// The extensions the context-menu button registers.
    /// </summary>
    /// <remarks>
    /// Both supported formats by default. Kept as a set so a user can tag comics
    /// without EBookMetaEditor appearing on their EPUBs.
    /// </remarks>
    internal List<string> RegisteredExtensions { get; set; } = [".epub", ".cbz"];

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
        Append(json, "rememberWindowGeometry", RememberWindowGeometry ? "true" : "false", quote: false);
        Append(json, "windowMaximised", WindowMaximised ? "true" : "false", quote: false);
        Append(json, "windowBounds", FormatBounds(WindowBounds), quote: true);
        Append(json, "batchWindowMaximised", BatchWindowMaximised ? "true" : "false", quote: false);
        Append(json, "batchWindowBounds", FormatBounds(BatchWindowBounds), quote: true);
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
            case "rememberWindowGeometry":
                settings.RememberWindowGeometry = value == "true";
                break;
            case "windowMaximised":
                settings.WindowMaximised = value == "true";
                break;
            case "windowBounds":
                settings.WindowBounds = ParseBounds(value);
                break;
            case "batchWindowMaximised":
                settings.BatchWindowMaximised = value == "true";
                break;
            case "batchWindowBounds":
                settings.BatchWindowBounds = ParseBounds(value);
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

    private static string FormatBounds(Rectangle r) => string.Join(",",
        r.X.ToString(CultureInfo.InvariantCulture),
        r.Y.ToString(CultureInfo.InvariantCulture),
        r.Width.ToString(CultureInfo.InvariantCulture),
        r.Height.ToString(CultureInfo.InvariantCulture));

    private static Rectangle ParseBounds(string value)
    {
        string[] parts = value.Split(',');

        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
        {
            return Rectangle.Empty;
        }

        return new Rectangle(x, y, w, h);
    }

    /// <summary>
    /// Reads a flat JSON object of string and boolean values.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal — it understands exactly the shape this class
    /// writes. Anything else it does not recognise is skipped rather than
    /// treated as an error, so a settings file from a future version does not
    /// prevent an older build from starting.
    /// </remarks>
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
