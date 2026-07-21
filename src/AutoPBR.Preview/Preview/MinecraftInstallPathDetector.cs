namespace AutoPBR.Preview;

public static class MinecraftInstallPathDetector
{
    /// <summary>
    /// Auto-detects a Minecraft version folder (latest non-snapshot with a usable client jar)
    /// or an extracted assets root under common <c>.minecraft</c> locations.
    /// </summary>
    public static string? TryDetectDefaultAssetsRoot()
    {
        foreach (var gameRoot in EnumerateCandidateGameRoots())
        {
            if (MinecraftInstallAssetPaths.TryResolvePreferredVersionFolder(gameRoot, out var versionFolder))
            {
                return versionFolder;
            }

            if (MinecraftInstallAssetPaths.TryResolveAssetsRoot(gameRoot, out var assetsRoot))
            {
                return assetsRoot;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidateGameRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(appData, ".minecraft");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".minecraft");
            if (OperatingSystem.IsMacOS())
            {
                yield return Path.Combine(home, "Library", "Application Support", "minecraft");
            }
        }
    }
}
