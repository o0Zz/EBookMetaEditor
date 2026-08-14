using System.Reflection;

namespace EBookMeta.App;

/// <summary>
/// Serves the application's managed dependencies out of its own resources, so
/// EBookMetaEditor.exe ships as a single file with nothing beside it.
/// </summary>
/// <remarks>
/// <para>
/// The <c>EmbedReferencedAssemblies</c> target in EBookMeta.App.csproj embeds
/// every copy-local reference and then drops it from the output folder, so
/// ordinary probing always fails and this handler is the only way those
/// assemblies are ever found. That is deliberate: a debug run exercises exactly
/// the same path as a release, rather than quietly loading DLLs from disk.
/// </para>
/// <para>
/// Nothing here may touch <c>EBookMeta.Core</c> — not even to log a failure —
/// because Core is itself one of the assemblies being resolved, and referencing
/// it from the resolver would recurse.
/// </para>
/// <para>
/// This also replaces the binding redirects that used to come from
/// App.config. An assembly handed back from <see cref="AppDomain.AssemblyResolve"/>
/// is accepted whatever its version, so the usual System.Memory /
/// System.Runtime.CompilerServices.Unsafe version skew resolves itself.
/// </para>
/// </remarks>
internal static class EmbeddedAssemblies
{
    /// <summary>Matches the <c>LogicalName</c> assigned by the csproj target.</summary>
    private const string Prefix = "EBookMetaEditor.Embedded.";

    /// <summary>
    /// Assemblies already materialised, keyed by simple name.
    /// </summary>
    /// <remarks>
    /// <see cref="Assembly.Load(byte[])"/> leaves its result in no load context,
    /// so the CLR does not find it again by name and asks once more on the next
    /// bind. Without this cache every request would load another copy, and two
    /// copies of the same assembly have incompatible type identities.
    /// </remarks>
    private static readonly Dictionary<string, Assembly?> Resolved =
        new Dictionary<string, Assembly?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hooks the resolver up. Must run before any method that mentions a type
    /// from an embedded assembly is JIT-compiled.
    /// </summary>
    internal static void Install() =>
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;

    private static Assembly? Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;

        // Satellite lookups are expected to miss in a single-language app, and
        // the CLR asks for them on the first localised resource access. Answering
        // null immediately keeps that off the resource-stream path.
        if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        lock (Resolved)
        {
            if (Resolved.TryGetValue(name, out Assembly? cached))
            {
                return cached;
            }

            Assembly? loaded = Load(name);

            // Cached even when null: a miss is worth remembering, since the CLR
            // will keep asking for a name that genuinely is not here.
            Resolved[name] = loaded;
            return loaded;
        }
    }

    private static Assembly? Load(string name)
    {
        Assembly self = typeof(EmbeddedAssemblies).Assembly;

        using Stream? stream = self.GetManifestResourceStream(Prefix + name + ".dll");
        if (stream is null)
        {
            return null;
        }

        byte[] image = new byte[stream.Length];
        int offset = 0;
        while (offset < image.Length)
        {
            int read = stream.Read(image, offset, image.Length - offset);
            if (read == 0)
            {
                // Truncated resource; a partial image would fail in a far more
                // confusing way inside Assembly.Load.
                return null;
            }

            offset += read;
        }

        return Assembly.Load(image);
    }
}
