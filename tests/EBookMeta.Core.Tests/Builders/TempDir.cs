namespace EBookMeta.Tests.Builders;

/// <summary>A scratch directory for one test, deleted afterwards.</summary>
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
