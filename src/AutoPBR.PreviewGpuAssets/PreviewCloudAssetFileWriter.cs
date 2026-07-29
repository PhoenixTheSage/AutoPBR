namespace AutoPBR.PreviewGpuAssets;

/// <summary>
/// Atomic file replacement shared by the deterministic cloud-asset tool and its failure tests.
/// </summary>
public static class PreviewCloudAssetFileWriter
{
    public static void WriteAtomically(string path, byte[] data) =>
        WriteAtomically(
            path,
            data,
            static (temporaryPath, destinationPath) =>
                File.Move(temporaryPath, destinationPath, overwrite: true));

    internal static void WriteAtomically(
        string path,
        byte[] data,
        Action<string, string> commit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(commit);

        var destinationPath = Path.GetFullPath(path);
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException(
                "Cloud asset destination must have a parent directory.",
                nameof(path));
        }

        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, data);
            if (new FileInfo(temporaryPath).Length != data.LongLength)
            {
                throw new IOException(
                    $"Temporary asset length mismatch for {Path.GetFileName(destinationPath)}.");
            }

            commit(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
