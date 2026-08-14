namespace EBookMeta.Tests.Builders;

/// <summary>
/// A scratch directory for one test, deleted afterwards.
/// </summary>
/// <remarks>
/// Fixtures are generated here at test time under the names CLAUDE.md
/// documents, rather than committed as binaries. That keeps the repository free
/// of opaque blobs while preserving the naming convention that ties a fixture to
/// the rule it triggers. Only golden expected-byte files are committed, since
/// those must be stable by definition.
/// </remarks>
internal sealed class TempDir : IDisposable
{
    internal TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ebookmeta-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(Path);
    }

    /// <summary>The directory's full path.</summary>
    internal string Path { get; }

    /// <summary>Resolves a name inside this directory.</summary>
    /// <param name="name">A file name.</param>
    /// <returns>The full path.</returns>
    internal string File(string name) => System.IO.Path.Combine(Path, name);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle in a failing test must not mask the real failure
            // by throwing from cleanup. The temp directory is disposable.
        }
    }
}
