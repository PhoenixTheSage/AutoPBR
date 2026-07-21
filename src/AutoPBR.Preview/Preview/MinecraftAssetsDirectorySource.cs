using AutoPBR.Core;

namespace AutoPBR.Preview;

/// <summary>
/// Directory under a Minecraft <c>assets/</c> root where zip paths <c>assets/minecraft/...</c>
/// map to <c>{root}/minecraft/...</c>.
/// </summary>
internal sealed class MinecraftAssetsDirectorySource(string assetsRoot) : IAssetSource
{
    public bool Exists(string assetPath) =>
        TryToDiskPath(assetPath, out var path) && File.Exists(path);

    public bool TryReadBytes(string assetPath, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!TryToDiskPath(assetPath, out var p) || !File.Exists(p))
        {
            return false;
        }

        bytes = File.ReadAllBytes(p);
        return true;
    }

    public bool TryReadText(string assetPath, out string text)
    {
        text = string.Empty;
        if (!TryToDiskPath(assetPath, out var p) || !File.Exists(p))
        {
            return false;
        }

        text = File.ReadAllText(p);
        return true;
    }

    private bool TryToDiskPath(string assetPath, out string path)
    {
        path = string.Empty;
        var norm = assetPath.Replace('\\', '/').TrimStart('/');
        if (norm.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm["assets/".Length..];
        }

        return ArchivePathSafety.TryResolveExtractionPath(assetsRoot, norm, out path);
    }
}
