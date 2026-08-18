using Microsoft.Win32;
using System.Diagnostics;
using System.Text;

namespace EBookMeta;

/// <summary>
/// An archiver already installed on the machine, run to produce an archive this
/// build cannot compress itself. Shared by the two containers in that position —
/// RAR and 7z — which differ only in what the program is called, where it
/// registers itself and which switches it takes.
/// </summary>
internal sealed class ExternalArchiver
{
    private readonly IReadOnlyList<string> _registryKeys;
    private readonly Func<string, string, bool, string> _arguments;

    /// <summary>Describes one archiver.</summary>
    /// <param name="executableName">The console program to look for, with its extension.</param>
    /// <param name="registryKeys">
    /// Subkeys, tried in order under both hives and both bitness views, whose
    /// <c>Path</c>, <c>Path64</c> or default value names the install directory.
    /// </param>
    /// <param name="arguments">
    /// The whole command line, given the quoted target, the quoted list file and
    /// whether the source archive stored its entries. Not shared: WinRAR keeps
    /// reading <c>@list</c> after <c>--</c> and 7-Zip stops, so the two archivers
    /// cannot be given the same shape.
    /// </param>
    internal ExternalArchiver(
        string executableName,
        IReadOnlyList<string> registryKeys,
        Func<string, string, bool, string> arguments)
    {
        ExecutableName = executableName;
        _registryKeys = registryKeys;
        _arguments = arguments;
    }

    /// <summary>The console program this archiver is.</summary>
    internal string ExecutableName { get; }

    /// <summary>
    /// The names handed to the archiver arrive in a file, not on a command line:
    /// three hundred pages is more than one holds.
    /// </summary>
    internal const string ListFileName = "__entries.lst";

    private const string StagingSuffix = ".stage";

    /// <summary>So a wedged archiver cannot hang a save forever. Not a budget.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>The values under a registry key that may name the install directory.</summary>
    private static readonly string?[] DirectoryValues = ["Path", "Path64", null];

    /// <summary>The archiver on this machine, or <see langword="null"/> if it has none.</summary>
    internal string? Search() =>
        _registryKeys.Select(InstallDirectory).Select(In).FirstOrDefault(found => found is not null) ??
        Environment.GetEnvironmentVariable("PATH")?.Split(';')
            .Select(In).FirstOrDefault(found => found is not null);

    /// <summary>The install directory a registry key records, when it records one.</summary>
    private static string? InstallDirectory(string subKey)
    {
        // Both bitness views and both hives: a 32-bit install on 64-bit Windows
        // registers under Wow6432Node, and a per-user install under HKCU.
        foreach (RegistryView view in
            new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default })
        {
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? key = root.OpenSubKey(subKey);

                    if (key is null)
                    {
                        continue;
                    }

                    foreach (string? name in DirectoryValues)
                    {
                        if (key.GetValue(name) as string is not { Length: > 0 } value)
                        {
                            continue;
                        }

                        string trimmed = value.Trim().Trim('"');

                        // The value is either the directory itself or the windowed
                        // program's own path, which names the same directory.
                        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? System.IO.Path.GetDirectoryName(trimmed)
                            : trimmed;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or ArgumentException)
                {
                    Log.Debug($"Could not read {hive}\\{subKey}: {ex.Message}");
                }
            }
        }

        return null;
    }

    /// <summary>The archiver inside a directory, when it is there.</summary>
    internal string? In(string? directory)
    {
        string trimmed = (directory ?? string.Empty).Trim().Trim('"');

        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            string candidate = System.IO.Path.Combine(trimmed, ExecutableName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (ArgumentException)
        {
            // PATH collects entries with characters no path may contain.
            return null;
        }
    }

    /// <summary>The command line handed to the archiver.</summary>
    /// <param name="targetPath">The archive to produce.</param>
    /// <param name="stored">Whether the source archive stored its entries.</param>
    /// <returns>The whole argument string.</returns>
    internal string Arguments(string targetPath, bool stored) =>
        _arguments(Quote(targetPath), Quote(ListFileName), stored);

    /// <summary>
    /// Writes an archive containing the given entries by handing them to the
    /// archiver.
    /// </summary>
    /// <param name="entries">The entries to write, in order.</param>
    /// <param name="targetPath">The archive to produce.</param>
    /// <param name="tool">The archiver to run.</param>
    /// <param name="stored">Whether the source archive stored its entries.</param>
    /// <exception cref="BookFormatException">An unsafe or repeated entry name.</exception>
    /// <exception cref="BookIoException">The archiver did not produce the file.</exception>
    internal void Create(
        IEnumerable<PendingEntry> entries, string targetPath, string tool, bool stored)
    {
        string staging = targetPath + StagingSuffix;

        try
        {
            Delete(staging);
            Directory.CreateDirectory(staging);

            // The archiver adds to an existing archive rather than replacing it, so a
            // leftover from an earlier attempt would silently merge into this one.
            File.Delete(targetPath);

            List<string> names = Stage(entries, staging);
            Run(tool, staging, System.IO.Path.GetFullPath(targetPath), names, stored);
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            // One answer for every cause, on purpose — see IsWriteFailure. The
            // particulars go here because the message withholds them.
            Log.Debug($"Could not write '{targetPath}': {ex.GetType().Name}: {ex.Message}");

            throw new BookIoException($"Could not write '{targetPath}'.", ex);
        }
        finally
        {
            Delete(staging);
        }
    }

    /// <summary>
    /// Writes every entry into the staging directory and returns their relative names,
    /// in order. The one place in Core that puts archive entries on disk, and so the
    /// one place hard invariant 4 has to be enforced rather than reported.
    /// </summary>
    /// <param name="entries">The entries to write.</param>
    /// <param name="staging">The directory to write them under.</param>
    /// <returns>The relative names to hand the archiver, in order.</returns>
    /// <exception cref="BookFormatException">An unsafe or repeated entry name.</exception>
    internal static List<string> Stage(IEnumerable<PendingEntry> entries, string staging)
    {
        string root = System.IO.Path.GetFullPath(staging);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PendingEntry pending in entries)
        {
            if (ContainerEntry.EscapesArchive(pending.Name))
            {
                throw new BookFormatException(
                    $"Entry '{pending.Name}' is absolute or escapes the archive, so it "
                    + "cannot be written. Nothing was changed.");
            }

            string relative = pending.Name.Replace('/', System.IO.Path.DirectorySeparatorChar);
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));

            // The predicate reads the name; this checks where it actually landed.
            if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new BookFormatException(
                    $"Entry '{pending.Name}' does not stay inside the archive.");
            }

            // RAR records a folder marker with no trailing separator, so IsDirectory is
            // all that tells it from a page. Written as a file it would fail on the
            // directory its own pages created. Not listed: the archiver recreates the
            // folder from the paths inside it.
            if (pending.Source?.IsDirectory == true || pending.Name.EndsWith('/'))
            {
                Directory.CreateDirectory(full);
                continue;
            }

            // Archives may legally repeat a name. On disk the second would overwrite
            // the first and a page would vanish.
            if (!seen.Add(relative))
            {
                throw new BookFormatException(
                    $"Entry '{pending.Name}' appears more than once, which cannot be "
                    + "reproduced. Nothing was changed.");
            }

            string? directory = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (Stream content = pending.OpenContent())
            using (var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                content.CopyTo(file);
            }

            names.Add(relative);
        }

        return names;
    }

    /// <summary>Runs the archiver over the staged files and waits for it to finish.</summary>
    private void Run(
        string tool, string workingDirectory, string targetPath, List<string> names, bool stored)
    {
        File.WriteAllLines(
            System.IO.Path.Combine(workingDirectory, ListFileName),
            names,
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        var info = new ProcessStartInfo
        {
            FileName = tool,
            Arguments = Arguments(targetPath, stored),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };

        // Drained asynchronously: a child that fills a pipe nobody reads blocks
        // forever, and this one is being waited on.
        process.OutputDataReceived += (_, e) => Capture(output, e.Data);
        process.ErrorDataReceived += (_, e) => Capture(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            Stop(process);
            throw new TimeoutException($"'{tool}' did not finish within {Timeout.TotalMinutes} minutes.");
        }

        // The parameterless overload after the timed one, so the handlers above are
        // known to have run before their buffer is read.
        process.WaitForExit();

        int exitCode = process.ExitCode;
        string text = output.ToString().Trim();

        if (exitCode != 0)
        {
            Log.Debug($"'{tool}' exited with {exitCode}. {text}");

            throw new IOException($"'{tool}' exited with code {exitCode}.");
        }

        if (text.Length > 0)
        {
            Log.Debug($"'{tool}': {text}");
        }

        // It can report success and produce nothing, and AtomicFileWriter's own check
        // would then blame the wrong thing.
        if (!File.Exists(targetPath))
        {
            throw new IOException($"'{tool}' reported success but produced no archive.");
        }

        Log.Info($"Wrote {names.Count} entries to '{targetPath}' using '{tool}'.");
    }

    private static void Capture(StringBuilder output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (output)
        {
            // Bounded: a chatty archiver must not turn a failed save into a megabyte
            // of log.
            if (output.Length < 4096)
            {
                output.Append(line).Append(' ');
            }
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // It exited already, or cannot be killed. The save has failed either way.
        }
    }

    /// <summary>Quotes an argument, which on Windows means wrapping it.</summary>
    private static string Quote(string value) => $"\"{value}\"";

    private static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: throwing from cleanup would mask the real failure.
        }
    }

    /// <summary>
    /// Whether an exception means "the save did not happen", not a bug. Names
    /// <see cref="System.ComponentModel.Win32Exception"/> and deliberately not its
    /// base <see cref="SystemException"/>, so a null-reference bug here still
    /// crashes rather than being reported as a polite save failure.
    /// </summary>
    private static bool IsWriteFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException
            or TimeoutException or InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception;
}
