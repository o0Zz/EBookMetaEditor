using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace EBookMeta.App;

/// <summary>A window that can be handed more files to work on.</summary>
internal interface IPathReceiver
{
    /// <summary>Takes paths that arrived after launch, on the UI thread.</summary>
    /// <param name="paths">The files or folders to open.</param>
    void AcceptPaths(string[] paths);
}

/// <summary>Keeps one process per user and forwards later launches into it.</summary>
internal static class SingleInstance
{
    /// <summary>How long a later launch waits to hand its paths over.</summary>
    private const int ConnectTimeoutMs = 3000;

    private static Mutex? _mutex;
    private static bool _listening;

    /// <summary>Claims the role of the one running instance.</summary>
    /// <returns>
    /// <see langword="true"/> if this process is the first; <see langword="false"/>
    /// if another already holds the role.
    /// </returns>
    internal static bool TryClaim()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            return createdNew;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A mutex we cannot create means we cannot coordinate. Behaving like the
            // only instance is the safe reading: the user still gets their window.
            Log.Debug($"Single-instance mutex unavailable ({ex.GetType().Name}); running standalone.");
            return true;
        }
    }

    /// <summary>Hands paths to the instance that already exists.</summary>
    /// <param name="paths">The paths to forward.</param>
    /// <returns><see langword="true"/> if they were delivered.</returns>
    internal static bool Forward(string[] paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.None);

            client.Connect(ConnectTimeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };

            foreach (string path in paths)
            {
                writer.WriteLine(path);
            }

            Log.Debug($"Forwarded {paths.Length} path(s) to the running instance.");
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException
                                      or ObjectDisposedException or InvalidOperationException)
        {
            Log.Warning(
                $"Could not hand these files to the running EBookMetaEditor ({ex.Message}). "
                + "Opening a separate window instead.");
            return false;
        }
    }

    /// <summary>Starts listening for paths from later launches.</summary>
    /// <param name="receiver">The window to hand arriving paths to.</param>
    internal static void Listen(Form receiver)
    {
        if (_listening)
        {
            return;
        }

        _listening = true;

        var thread = new Thread(() => ListenLoop(receiver))
        {
            IsBackground = true,
            Name = "EBookMetaEditor path listener",
        };

        thread.Start();
    }

    private static void ListenLoop(Form receiver)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1);

                server.WaitForConnection();

                using var reader = new StreamReader(server, new UTF8Encoding(false));

                var paths = new List<string>();
                string? line;

                while ((line = reader.ReadLine()) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        paths.Add(line.Trim());
                    }
                }

                Deliver(receiver, [.. paths]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // One failed connection is not worth stopping over: the next launch
                // gets a fresh server instance on the next pass.
                Log.Debug($"Path listener recovered from {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void Deliver(Form receiver, string[] paths)
    {
        // The receiver is a window, so this must run on the thread owning its handle.
        // Mid-create or mid-destroy is the ordinary race; dropping the paths beats it.
        try
        {
            if (receiver.IsDisposed || !receiver.IsHandleCreated)
            {
                return;
            }

            receiver.BeginInvoke(new Action(() =>
            {
                if (receiver is IPathReceiver target)
                {
                    target.AcceptPaths(paths);
                }
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Log.Debug($"Forwarded paths arrived too late to deliver: {ex.Message}");
        }
    }

    /// <summary>
    /// The mutex name, in the session-local namespace so each signed-in user gets
    /// their own instance.
    /// </summary>
    private static string MutexName => $"Local\\EBookMetaEditor.instance.{Identity}";

    /// <summary>
    /// The pipe name. Pipe names are machine-wide whatever namespace they claim, so
    /// the user and session go in the name itself.
    /// </summary>
    private static string PipeName => $"EBookMetaEditor.paths.{Identity}";

    private static string Identity { get; } = BuildIdentity();

    private static string BuildIdentity()
    {
        int session;

        try
        {
            using Process current = Process.GetCurrentProcess();
            session = current.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            session = 0;
        }

        var name = new StringBuilder();

        // Only characters that are safe in both a kernel object name and a pipe
        // name: a user name can contain a backslash, which would otherwise create a
        // namespace nobody meant.
        foreach (char c in Environment.UserName)
        {
            name.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        return $"{name}.{session}";
    }
}
