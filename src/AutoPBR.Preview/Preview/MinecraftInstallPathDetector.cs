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

    /// <summary>Exposes candidate roots for tests without touching the filesystem layout of installs.</summary>
    internal static IEnumerable<string> EnumerateCandidateGameRootsForTests() => EnumerateCandidateGameRoots();

    private static IEnumerable<string> EnumerateCandidateGameRoots()
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (var root in EnumerateLinuxGameRoots())
            {
                yield return root;
            }

            yield break;
        }

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

    private static IEnumerable<string> EnumerateLinuxGameRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdgData) && !string.IsNullOrWhiteSpace(home))
        {
            xdgData = Path.Combine(home, ".local", "share");
        }

        if (!string.IsNullOrWhiteSpace(xdgData))
        {
            yield return Path.Combine(xdgData, "minecraft");
            yield return Path.Combine(xdgData, ".minecraft");
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".minecraft");
            yield return Path.Combine(home, ".var", "app", "com.mojang.Minecraft", ".minecraft");
            yield return Path.Combine(home, ".var", "app", "com.mojang.Minecraft", "data", ".minecraft");
        }
    }
}
