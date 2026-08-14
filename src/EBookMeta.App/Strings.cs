using System.Globalization;
using System.Reflection;

namespace EBookMeta.App;

/// <summary>
/// The interface language: every piece of text the windows show.
/// </summary>
/// <remarks>
/// <para>
/// Backed by one small <c>key = value</c> file per language, embedded in the
/// exe. Deliberately not .resx and satellite assemblies: a satellite is a DLL in
/// a subfolder, and this application is one file with nothing beside it. It is
/// also not a format a translator can open — a plain text file is, which is the
/// whole reason a language ships as data rather than as code.
/// </para>
/// <para>
/// Adding a language is adding one file to <c>Languages/</c>. Nothing here
/// enumerates them by name: the picker in the settings dialog is built from
/// whatever is embedded.
/// </para>
/// <para>
/// <b>Only the interface is translated.</b> The session log and the findings it
/// carries stay in English, because they are diagnostics: a rule ID and its
/// message are what a user pastes into a bug report, and Core has no business
/// knowing what language a window is in.
/// </para>
/// </remarks>
internal static class Strings
{
    /// <summary>Matches the <c>LogicalName</c> the csproj target assigns.</summary>
    private const string Prefix = "EBookMetaEditor.Languages.";

    private const string Suffix = ".lang";

    /// <summary>
    /// The language every other one falls back to, key by key.
    /// </summary>
    /// <remarks>
    /// Per key rather than per file, so a translation that is merely incomplete
    /// still works — the untranslated lines come out in English instead of the
    /// window showing raw key names.
    /// </remarks>
    private const string FallbackCode = "en";

    private static Dictionary<string, string> _english = Load(FallbackCode) ?? [];
    private static Dictionary<string, string> _current = _english;

    /// <summary>The language in use, as a two-letter code.</summary>
    internal static string Code { get; private set; } = FallbackCode;

    /// <summary>
    /// The languages this build carries, each named in itself.
    /// </summary>
    /// <remarks>
    /// Read on demand: the settings dialog is the only thing that needs the whole
    /// list, and parsing every language at launch would be work done for a window
    /// most sessions never open.
    /// </remarks>
    internal static IReadOnlyList<Language> Available
    {
        get
        {
            var found = new List<Language>();

            foreach (string resource in typeof(Strings).Assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(Prefix, StringComparison.Ordinal) ||
                    !resource.EndsWith(Suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                string code = resource.Substring(Prefix.Length, resource.Length - Prefix.Length - Suffix.Length);
                Dictionary<string, string>? table = Load(code);

                if (table is not null)
                {
                    // Named in its own language, never translated: a French speaker
                    // looking for their language in an interface they cannot read
                    // is looking for the word "Français".
                    found.Add(new Language(code, table.TryGetValue("@name", out string? name) ? name : code));
                }
            }

            return [.. found.OrderBy(l => l.Name, StringComparer.CurrentCulture)];
        }
    }

    /// <summary>Switches language.</summary>
    /// <param name="code">
    /// A two-letter code, or <see langword="null"/> or empty to follow Windows.
    /// An unknown code falls back to English rather than failing.
    /// </param>
    internal static void Use(string? code)
    {
        string wanted = string.IsNullOrWhiteSpace(code)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : code!.Trim().ToLowerInvariant();

        Dictionary<string, string>? table = Load(wanted);

        if (table is null)
        {
            wanted = FallbackCode;
            table = _english;
        }

        Code = wanted;
        _current = table;

        // UI culture only. CurrentCulture decides how numbers and dates parse and
        // format, and this application parses metadata out of users' files — a
        // series index or a publication date read back differently because the
        // interface is in German would be a change to what gets written to disk.
        // The language of the window must not reach the file.
        Thread.CurrentThread.CurrentUICulture = ResolveCulture(wanted);
    }

    /// <summary>Looks up one piece of text.</summary>
    /// <param name="key">The key, as it appears in the language files.</param>
    /// <returns>
    /// The translation, the English text when this language has no line for the
    /// key, or the key itself when nothing does — visible, but never a crash.
    /// </returns>
    internal static string Get(string key)
    {
        if (_current.TryGetValue(key, out string? text) || _english.TryGetValue(key, out text))
        {
            return text;
        }

        Log.Debug($"No text for '{key}' in '{Code}'.");
        return key;
    }

    /// <summary>Looks up text with placeholders and fills them in.</summary>
    /// <param name="key">The key, as it appears in the language files.</param>
    /// <param name="args">The values for <c>{0}</c>, <c>{1}</c> and so on.</param>
    /// <returns>The formatted text.</returns>
    /// <remarks>
    /// A translation with a mistyped placeholder returns unformatted rather than
    /// throwing. Language files are edited by people who are not building the
    /// application, and a stray brace in one line of Italian is not a reason for
    /// the window to fall over.
    /// </remarks>
    internal static string Format(string key, params object?[] args)
    {
        string template = Get(key);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            Log.Warning($"The '{Code}' text for '{key}' has a malformed placeholder.");
            return template;
        }
    }

    /// <summary>Looks up text that reads differently for one thing and for several.</summary>
    /// <param name="key">The key without its <c>.one</c> or <c>.many</c> suffix.</param>
    /// <param name="count">How many, which chooses the form.</param>
    /// <param name="args">The values for <c>{0}</c>, <c>{1}</c> and so on.</param>
    /// <returns>The formatted text.</returns>
    /// <remarks>
    /// Two forms, which is enough for the languages here and honest about not
    /// being enough in general. "1 file" and "2 files" cannot be built by pasting
    /// an "s" onto the end outside English, so the two readings are separate
    /// lines a translator can write independently.
    /// </remarks>
    internal static string Plural(string key, int count, params object?[] args) =>
        Format(key + (count == 1 ? ".one" : ".many"), args);

    /// <summary>
    /// Reads one language file out of the exe.
    /// </summary>
    /// <returns>The table, or <see langword="null"/> if this build has no such language.</returns>
    private static Dictionary<string, string>? Load(string code)
    {
        using Stream? stream = typeof(Strings).Assembly.GetManifestResourceStream(Prefix + code + Suffix);

        if (stream is null)
        {
            return null;
        }

        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        // UTF-8 without a BOM, and detectEncodingFromByteOrderMarks left on so a
        // translator who saves with one in Notepad is not punished for it.
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            int equals = trimmed.IndexOf('=');

            if (equals <= 0)
            {
                continue;
            }

            table[trimmed.Substring(0, equals).Trim()] = Unescape(trimmed.Substring(equals + 1).Trim());
        }

        return table;
    }

    /// <summary>
    /// Turns the two escapes a one-line-per-string format needs back into characters.
    /// </summary>
    private static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            i++;
            builder.Append(value[i] switch
            {
                'n' => '\n',
                't' => '\t',
                _ => value[i],
            });
        }

        return builder.ToString();
    }

    private static CultureInfo ResolveCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            // A language file whose name is not a culture Windows knows. The text
            // still works; only the culture object is unavailable.
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>One language, as offered in the settings dialog.</summary>
    /// <remarks>
    /// A class rather than a record: <c>init</c> accessors need a support type
    /// that only Core polyfills, and the UI project has no business declaring
    /// compiler plumbing of its own.
    /// </remarks>
    internal sealed class Language(string code, string name)
    {
        /// <summary>The two-letter code, which is also the file name.</summary>
        internal string Code { get; } = code;

        /// <summary>The language's name for itself.</summary>
        internal string Name { get; } = name;

        /// <summary>The label the combo box shows.</summary>
        public override string ToString() => Name;
    }
}
