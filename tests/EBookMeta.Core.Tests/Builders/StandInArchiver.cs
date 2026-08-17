using System.CodeDom.Compiler;
using Microsoft.CSharp;

namespace EBookMeta.Tests.Builders;

/// <summary>A stand-in for the external archiver a CBR save shells out to.</summary>
internal static class StandInArchiver
{
    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>The stand-in, compiled on first use and reused after.</summary>
    /// <param name="exitCode">
    /// What it should return. Non-zero also means it writes nothing, which is how a
    /// real archiver behaves when it gives up.
    /// </param>
    /// <returns>The path to the executable.</returns>
    internal static string Path(int exitCode = 0)
    {
        lock (Gate)
        {
            // Compiled once per test run and left in the output directory; the exit
            // code is passed at run time so one build serves both cases.
            _path ??= Compile();
            return _path;
        }
    }

    /// <summary>
    /// The environment variable the stand-in reads its exit code from, because
    /// <c>RarContainer</c> owns the command line and will not carry a test's
    /// argument through it.
    /// </summary>
    internal const string ExitCodeVariable = "EBOOKMETA_STANDIN_EXIT";

    private static string Compile()
    {
        string path = System.IO.Path.Combine(
            AppContext.BaseDirectory, "standin-archiver.exe");

        using var provider = new CSharpCodeProvider();

        var parameters = new CompilerParameters
        {
            GenerateExecutable = true,
            OutputAssembly = path,
            GenerateInMemory = false,
        };

        parameters.ReferencedAssemblies.Add("System.dll");

        CompilerResults results = provider.CompileAssemblyFromSource(parameters, Source);

        if (results.Errors.HasErrors)
        {
            throw new InvalidOperationException(
                "The stand-in archiver did not compile: " + results.Errors[0]);
        }

        return path;
    }

    /// <summary>
    /// Parses the arguments the way the real archiver would, and records what it was
    /// asked for rather than compressing it.
    /// </summary>
    private const string Source = """
        using System;
        using System.IO;
        using System.Text;

        internal static class StandIn
        {
            private static int Main(string[] args)
            {
                string target = null;
                string list = null;
                bool paths = false;

                foreach (string arg in args)
                {
                    if (arg == "--") { paths = true; continue; }
                    if (!paths) { continue; }

                    if (arg.StartsWith("@")) { list = arg.Substring(1); }
                    else if (target == null) { target = arg; }
                }

                if (target == null || list == null)
                {
                    Console.Error.WriteLine("no target or list file after --");
                    return 9;
                }

                int exit = 0;
                string requested = Environment.GetEnvironmentVariable("EBOOKMETA_STANDIN_EXIT");
                if (!string.IsNullOrEmpty(requested)) { int.TryParse(requested, out exit); }

                if (exit != 0)
                {
                    Console.Error.WriteLine("refusing, as asked");
                    return exit;
                }

                // Read as UTF-16, which is what -scul promises. Names are relative
                // to the working directory the archiver was started in.
                StringBuilder manifest = new StringBuilder();

                foreach (string name in File.ReadAllLines(list, Encoding.Unicode))
                {
                    if (name.Length == 0) { continue; }

                    FileInfo file = new FileInfo(name);
                    manifest.Append(name).Append('=')
                            .Append(file.Exists ? file.Length.ToString() : "MISSING")
                            .Append('\n');
                }

                File.WriteAllText(target, manifest.ToString());
                return 0;
            }
        }
        """;
}
