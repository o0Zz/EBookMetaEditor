namespace EBookMeta;

/// <summary>The only sanctioned way EBookMetaEditor writes over a user's file.</summary>
public static class AtomicFileWriter
{
    /// <summary>
    /// Builds a replacement for <paramref name="targetPath"/> and swaps it in.
    /// </summary>
    /// <param name="targetPath">The file to replace.</param>
    /// <param name="writeContent">
    /// Writes the complete new file to the temporary path it is given. It must
    /// not touch <paramref name="targetPath"/>.
    /// </param>
    /// <param name="keepBackup">
    /// Whether to leave the previous version beside the file as <c>.bak</c>.
    /// </param>
    /// <returns>The backup's path, or <see langword="null"/> if none was kept.</returns>
    /// <exception cref="BookIoException">The write or the swap failed.</exception>
    public static string? Write(string targetPath, Action<string> writeContent, bool keepBackup = true)
    {
        Throw.IfNullOrEmpty(targetPath);
        Throw.IfNull(writeContent);

        string full = Path.GetFullPath(targetPath);

        // Siblings, not %TEMP%: File.Replace requires both paths on one volume,
        // and a temp file on another drive would also mean copying the whole
        // archive across volumes for no reason.
        string temp = full + ".tmp";
        string backup = full + ".bak";

        try
        {
            DeleteIfExists(temp);
            writeContent(temp);

            if (!File.Exists(temp))
            {
                throw new BookIoException(
                    "The writer did not produce a file. Nothing was changed.");
            }

            if (!File.Exists(full))
            {
                // Nothing to replace — a new file rather than an edit.
                File.Move(temp, full);
                Log.Info($"Created '{full}'.");
                return null;
            }

            DeleteIfExists(backup);

            // ignoreMetadataErrors: a failure to copy auditing metadata must not
            // abort a swap that has otherwise succeeded.
            File.Replace(temp, full, backup, ignoreMetadataErrors: true);

            if (keepBackup)
            {
                Log.Info($"Replaced '{full}' atomically; previous version kept as '{backup}'.");
                return backup;
            }

            DeleteIfExists(backup);
            Log.Info($"Replaced '{full}' atomically; no backup kept.");
            return null;
        }
        catch (BookIoException)
        {
            DeleteIfExists(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The half-written temp file is ours and worthless; the user's
            // original is untouched, which is the point.
            DeleteIfExists(temp);
            throw new BookIoException($"Could not write '{full}'. The original is unchanged.", ex);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort. A leftover .tmp is untidy but harmless, and throwing
            // from cleanup would mask whatever real failure led here.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
