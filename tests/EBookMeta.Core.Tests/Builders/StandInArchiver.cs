using System.CodeDom.Compiler;
using Microsoft.CSharp;

namespace EBookMeta.Tests.Builders;

/// <summary>A stand-in for the external archiver a CBR save shells out to.</summary>
internal static class StandInArchiver
{
    private static string? _path;

    /// <summary>The stand-in, compiled on first use and reused after.</summary>
    /// <returns>The path to the executable.</returns>
    internal static string Path()
    {
        // Compiled once per test run and left in the output directory; the exit code
        // comes from ExitCodeVariable at run time, so one build serves every case.
        // The suite runs serially, so nothing guards this.
        return _path ??= Compile();
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

                // The verb first, then switches, then paths. WinRAR's -- says the
                // rest are paths; 7-Zip has no -- and its switches simply lead
                // with a dash. Both leave the list file as @name.
                for (int i = 1; i < args.Length; i++)
                {
                    string arg = args[i];

                    if (arg == "--") { paths = true; continue; }
                    if (arg.Length == 0) { continue; }
                    if (!paths && arg[0] == '-') { continue; }

                    if (arg[0] == '@') { list = arg.Substring(1); }
                    else if (target == null) { target = arg; }
                }

                if (target == null || list == null)
                {
                    Console.Error.WriteLine("no target or list file on the command line");
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
