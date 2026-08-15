using System.Reflection;
using System.Runtime.CompilerServices;

namespace EBookMeta.App;

/// <summary>
/// Entry point. Receives what to edit as arguments, supplied by the Explorer
/// context menu verb.
/// </summary>
internal static class Program
{
    [STAThread]
    internal static void Main(string[] args)
    {
        EmbeddedAssemblies.Install();
        Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string[] args)
    {
        // On net48 these are explicit calls rather than the generated
        // ApplicationConfiguration.Initialize() of modern .NET; the DPI mode
        // itself comes from app.manifest.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppSettings settings = AppSettings.Load();

        // Before any window exists: every form reads its text in its constructor,
        // so a language chosen after the first one is built would arrive too late
        // for it.
        Strings.Use(settings.Language);

        StartLogging(settings, args);

        // An unhandled exception is the one case the in-memory log cannot survive,
        // so it is recorded — which also flushes the whole session to disk,
        // because it is logged as an error.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Error("Unhandled exception; the application is closing", ex);
            }
        };

        // Only the arguments that name something on disk.
        string[] paths =
            [.. args.Where(arg => !string.IsNullOrWhiteSpace(arg))
                    .Where(arg => File.Exists(arg) || Directory.Exists(arg))];

        // Selecting thirty files in Explorer starts up to thirty processes. All but
        // the first hand their paths over and exit, so the user gets one window with
        // thirty rows instead of thirty windows with one row each.
        if (!SingleInstance.TryClaim() && SingleInstance.Forward(paths))
        {
            Log.Info("Handed over to the running instance.");
            return;
        }

        Form window = CreateWindow(settings, paths);

        SingleInstance.Listen(window);
        Application.Run(window);

        Log.Info("Closing.");
    }

    /// <summary>
    /// Chooses the window this launch wants.
    /// </summary>
    /// <param name="settings">The loaded user settings.</param>
    /// <param name="paths">The paths from the command line.</param>
    /// <returns>The window to run.</returns>
    private static Form CreateWindow(AppSettings settings, string[] paths)
    {
        if (paths.Length == 1 && !Directory.Exists(paths[0]))
        {
            return new MainForm(settings, paths[0]);
        }

        return paths.Length == 0
            ? new MainForm(settings, null)
            : new BatchForm(settings, paths);
    }

    /// <summary>
    /// Points the log at a file beside the settings and records what we started with.
    /// </summary>
    private static void StartLogging(AppSettings settings, string[] args)
    {
        try
        {
            string? directory = Path.GetDirectoryName(settings.Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Log.FilePath = Path.Combine(directory, "EBookMetaEditor.log");
            }
        }
        catch (ArgumentException)
        {
            // An unusable settings path just means no log file. Not worth failing
            // a launch over, and the in-memory log still works.
        }

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        Log.Info($"EBookMetaEditor {version} starting on {Environment.OSVersion}.");
        Log.Debug($"Settings: {settings.Path}");
        Log.Debug(settings.Language.Length == 0
            ? $"Interface language: {Strings.Code} (following Windows)."
            : $"Interface language: {Strings.Code} (chosen).");
        Log.Debug(args.Length > 0
            ? $"Launched with: {string.Join(" ", args)}"
            : "Launched with no arguments.");
    }
}
