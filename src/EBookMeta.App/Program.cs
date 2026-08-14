using System.Reflection;
using System.Runtime.CompilerServices;

namespace EBookMeta.App;

/// <summary>
/// Entry point. Receives the file to edit as <c>argv[0]</c>, supplied by the
/// Explorer context menu verb.
/// </summary>
internal static class Program
{
    /// <remarks>
    /// This body does nothing but install the assembly resolver, and that is
    /// the whole point. Dependencies live inside the exe rather than beside it
    /// (see <see cref="EmbeddedAssemblies"/>), and the JIT loads every assembly
    /// a method mentions before running its first instruction — so anything
    /// touching EBookMeta.Core has to sit behind a separate, non-inlined call
    /// that is not compiled until the resolver is already listening.
    /// </remarks>
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

        Application.Run(new MainForm(settings, args.Length > 0 ? args[0] : null));

        Log.Info("Closing.");
    }

    /// <summary>
    /// Points the log at a file beside the settings and records what we started with.
    /// </summary>
    /// <remarks>
    /// Setting <see cref="Log.FilePath"/> does not create or open anything. The
    /// file is written only if a warning or worse occurs, so a clean launch does
    /// no disk I/O for logging at all — which matters against a 400 ms budget,
    /// where opening a file can cost an antivirus scan.
    /// </remarks>
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
        Log.Debug(args.Length > 0
            ? $"Launched with: {string.Join(" ", args)}"
            : "Launched with no arguments.");
    }
}
