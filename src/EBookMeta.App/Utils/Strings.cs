using System.Globalization;
using System.Reflection;

namespace EBookMeta.App.Utils;

/// <summary>The interface language: every piece of text the windows show.</summary>
internal static class Strings
{
    /// <summary>
    /// Where the language files are, as embedded resources. Derived rather than
    /// written down: the csproj assigns their <c>LogicalName</c> from
    /// <c>$(AssemblyName)</c>, so the two cannot drift apart, and an application
    /// that takes this file gets its own name without editing it.
    /// </summary>
    private static readonly string Prefix =
        typeof(Strings).Assembly.GetName().Name + ".Languages.";

    private const string Suffix = ".lang";

    /// <summary>The language every other one falls back to, key by key.</summary>
    private const string FallbackCode = "en";

    private static Dictionary<string, string> _english = Load(FallbackCode) ?? [];
    private static Dictionary<string, string> _current = _english;

    /// <summary>The language in use, as a two-letter code.</summary>
    internal static string Code { get; private set; } = FallbackCode;

    /// <summary>The languages this build carries, each named in itself.</summary>
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

        // UI culture only. CurrentCulture governs how Core parses metadata, so a date
        // read back differently because the window is in German would reach the file.
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
    internal static string Plural(string key, int count, params object?[] args) =>
        Format(key + (count == 1 ? ".one" : ".many"), args);

    /// <summary>Reads one language file out of the exe.</summary>
    /// <returns>The table, or <see langword="null"/> if this build has no such language.</returns>
    private static Dictionary<string, string>? Load(string code)
    {
        using Stream? stream = typeof(Strings).Assembly.GetManifestResourceStream(Prefix + code + Suffix);

        if (stream is null)
        {
            return null;
        }

        // UTF-8 without a BOM, and detectEncodingFromByteOrderMarks left on so a
        // translator who saves with one in Notepad is not punished for it.
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);

        Dictionary<string, string> table = KeyValueFile.Read(reader);

        foreach (string key in table.Keys.ToList())
        {
            table[key] = Unescape(table[key]);
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
