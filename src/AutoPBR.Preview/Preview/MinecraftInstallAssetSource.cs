using System.IO.Compression;

namespace AutoPBR.Preview;

internal static class MinecraftInstallAssetSource
{
    /// <summary>
    /// Opens an install-backed asset source for the configured Minecraft assets path.
    /// When the source owns a zip handle, <paramref name="lifetime"/> must be disposed by the caller.
    /// </summary>
    public static bool TryOpen(string? configuredPath, out IAssetSource? source, out IDisposable? lifetime)
    {
        source = null;
        lifetime = null;
        if (!MinecraftInstallAssetPaths.TryResolve(configuredPath, out var location))
        {
            return false;
        }

        switch (location.Kind)
        {
            case MinecraftInstallAssetsKind.DirectoryAssetsRoot:
                source = new MinecraftAssetsDirectorySource(location.Path);
                return true;

            case MinecraftInstallAssetsKind.ClientJar:
                try
                {
                    var zip = ZipFile.OpenRead(location.Path);
                    source = new ZipAssetSource(zip);
                    lifetime = zip;
                    return true;
                }
                catch
                {
                    source = null;
                    lifetime = null;
                    return false;
                }

            default:
                return false;
        }
    }
}
