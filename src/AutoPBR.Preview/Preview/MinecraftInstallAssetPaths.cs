using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoPBR.Preview;

internal enum MinecraftInstallAssetsKind
{
    /// <summary>Extracted <c>assets/</c> directory containing <c>minecraft/models/block</c>.</summary>
    DirectoryAssetsRoot = 0,

    /// <summary>Client jar whose entries include <c>assets/minecraft/...</c>.</summary>
    ClientJar = 1,
}

internal readonly record struct MinecraftInstallAssetsLocation(
    MinecraftInstallAssetsKind Kind,
    string Path);

internal static class MinecraftInstallAssetPaths
{
    private static readonly Regex WeeklySnapshotName =
        new(@"^\d{2}w\d{2}[a-z]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Resolves a user-provided directory to an <c>assets/</c> root suitable for
    /// <see cref="MinecraftAssetsDirectorySource"/>. Accepts the install root, a version folder,
    /// or a path that already ends in <c>assets</c>. Does not open client jars.
    /// </summary>
    public static bool TryResolveAssetsRoot(string? configuredPath, out string assetsRoot)
    {
        assetsRoot = string.Empty;
        if (!TryResolve(configuredPath, out var location) ||
            location.Kind != MinecraftInstallAssetsKind.DirectoryAssetsRoot)
        {
            return false;
        }

        assetsRoot = location.Path;
        return true;
    }

    /// <summary>
    /// Resolves a configured Minecraft assets path to either an extracted <c>assets/</c> root
    /// or a client jar. Accepts <c>.minecraft</c>, <c>versions/</c>, a version folder, an
    /// extracted assets tree, or a client jar path. When scanning a versions directory, prefers
    /// the latest non-snapshot release that has a usable client jar.
    /// </summary>
    public static bool TryResolve(string? configuredPath, out MinecraftInstallAssetsLocation location)
    {
        location = default;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        var path = Path.GetFullPath(configuredPath.Trim());

        if (File.Exists(path))
        {
            if (IsJarPath(path) && JarHasBlockAssets(path))
            {
                location = new MinecraftInstallAssetsLocation(MinecraftInstallAssetsKind.ClientJar, path);
                return true;
            }

            return false;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        if (Path.GetFileName(path).Equals("assets", StringComparison.OrdinalIgnoreCase) &&
            HasBlockModels(path))
        {
            location = new MinecraftInstallAssetsLocation(MinecraftInstallAssetsKind.DirectoryAssetsRoot, path);
            return true;
        }

        var directAssets = Path.Combine(path, "assets");
        if (Directory.Exists(directAssets) && HasBlockModels(directAssets))
        {
            location = new MinecraftInstallAssetsLocation(
                MinecraftInstallAssetsKind.DirectoryAssetsRoot,
                directAssets);
            return true;
        }

        if (TryResolveVersionFolderJar(path, out var versionJar))
        {
            location = new MinecraftInstallAssetsLocation(MinecraftInstallAssetsKind.ClientJar, versionJar);
            return true;
        }

        if (TryResolveFromVersionsDirectory(path, preferNonSnapshot: true, out var versionsJar))
        {
            location = new MinecraftInstallAssetsLocation(MinecraftInstallAssetsKind.ClientJar, versionsJar);
            return true;
        }

        var nestedVersions = Path.Combine(path, "versions");
        if (Directory.Exists(nestedVersions) &&
            TryResolveFromVersionsDirectory(nestedVersions, preferNonSnapshot: true, out versionsJar))
        {
            location = new MinecraftInstallAssetsLocation(MinecraftInstallAssetsKind.ClientJar, versionsJar);
            return true;
        }

        foreach (var versionAssets in Directory.EnumerateDirectories(path, "assets", SearchOption.AllDirectories))
        {
            if (HasBlockModels(versionAssets))
            {
                location = new MinecraftInstallAssetsLocation(
                    MinecraftInstallAssetsKind.DirectoryAssetsRoot,
                    versionAssets);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Picks the version folder for the latest usable non-snapshot install under a game root
    /// or versions directory (falls back to any jar-backed version when no release exists).
    /// </summary>
    public static bool TryResolvePreferredVersionFolder(string? rootOrVersionsPath, out string versionFolder)
    {
        versionFolder = string.Empty;
        if (string.IsNullOrWhiteSpace(rootOrVersionsPath))
        {
            return false;
        }

        var path = Path.GetFullPath(rootOrVersionsPath.Trim());
        if (!Directory.Exists(path))
        {
            return false;
        }

        var versionsDir = Path.GetFileName(path).Equals("versions", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.Combine(path, "versions");
        if (!Directory.Exists(versionsDir))
        {
            return false;
        }

        if (!TrySelectVersionCandidate(versionsDir, preferNonSnapshot: true, out var candidate) ||
            string.IsNullOrWhiteSpace(candidate.FolderPath))
        {
            return false;
        }

        versionFolder = candidate.FolderPath;
        return true;
    }

    private static bool TryResolveVersionFolderJar(string versionDir, out string jarPath)
    {
        jarPath = string.Empty;
        var id = Path.GetFileName(versionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(id) ||
            id.Equals("versions", StringComparison.OrdinalIgnoreCase) ||
            id.Equals(".minecraft", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Explicit version folders are honored even when they are snapshots.
        if (!LooksLikeVersionFolder(versionDir, id))
        {
            return false;
        }

        return TryFindClientJar(versionDir, id, out jarPath);
    }

    private static bool LooksLikeVersionFolder(string versionDir, string id)
    {
        if (File.Exists(Path.Combine(versionDir, id + ".json")) ||
            File.Exists(Path.Combine(versionDir, id + ".jar")) ||
            File.Exists(Path.Combine(versionDir, "client.jar")))
        {
            return true;
        }

        try
        {
            return Directory.EnumerateFiles(versionDir, "*.jar").Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveFromVersionsDirectory(
        string versionsDir,
        bool preferNonSnapshot,
        out string jarPath)
    {
        jarPath = string.Empty;
        if (!Path.GetFileName(versionsDir).Equals("versions", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TrySelectVersionCandidate(versionsDir, preferNonSnapshot, out var candidate) ||
            string.IsNullOrWhiteSpace(candidate.JarPath))
        {
            return false;
        }

        jarPath = candidate.JarPath;
        return true;
    }

    private static bool TrySelectVersionCandidate(
        string versionsDir,
        bool preferNonSnapshot,
        out VersionCandidate candidate)
    {
        candidate = default;
        List<VersionCandidate> candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(versionsDir)
                .Select(TryCreateCandidate)
                .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Value.JarPath))
                .Select(c => c!.Value)
                .ToList();
        }
        catch
        {
            return false;
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        IEnumerable<VersionCandidate> pool = candidates;
        if (preferNonSnapshot)
        {
            var releases = candidates.Where(c => c.IsRelease).ToList();
            if (releases.Count > 0)
            {
                pool = releases;
            }
        }

        candidate = pool
            .OrderByDescending(c => c.ParsedVersion is not null)
            .ThenByDescending(c => c.ParsedVersion)
            .ThenByDescending(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .First();
        return true;
    }

    private static VersionCandidate? TryCreateCandidate(string versionDir)
    {
        var id = Path.GetFileName(versionDir);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (!TryFindClientJar(versionDir, id, out var jarPath))
        {
            return null;
        }

        var type = TryReadVersionType(Path.Combine(versionDir, id + ".json"));
        var isSnapshot = IsSnapshot(id, type);
        var parsed = MinecraftNativeProfileResolver.TryParseVersionLike(StripPrereleaseSuffix(id));
        return new VersionCandidate(versionDir, id, parsed, IsRelease: !isSnapshot, jarPath);
    }

    private static bool TryFindClientJar(string versionDir, string id, out string jarPath)
    {
        jarPath = string.Empty;
        foreach (var candidate in EnumerateJarCandidates(versionDir, id))
        {
            if (JarHasBlockAssets(candidate))
            {
                jarPath = candidate;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateJarCandidates(string versionDir, string id)
    {
        var preferred = new[]
        {
            Path.Combine(versionDir, id + ".jar"),
            Path.Combine(versionDir, "client.jar"),
        };
        foreach (var path in preferred)
        {
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        IEnumerable<string> extras;
        try
        {
            extras = Directory.EnumerateFiles(versionDir, "*.jar")
                .Where(p => !preferred.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            yield break;
        }

        foreach (var path in extras)
        {
            yield return path;
        }
    }

    private static bool JarHasBlockAssets(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            return zip.GetEntry("assets/minecraft/textures/block/grass_block_top.png") is not null ||
                   zip.GetEntry("assets/minecraft/models/block/stone.json") is not null ||
                   zip.GetEntry("assets/minecraft/textures/colormap/grass.png") is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasBlockModels(string assetsRoot)
    {
        var probe = Path.Combine(assetsRoot, "minecraft", "models", "block");
        return Directory.Exists(probe);
    }

    private static bool IsJarPath(string path) =>
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    private static bool IsSnapshot(string id, string? type)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (type.Equals("release", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (type.Equals("snapshot", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("old_snapshot", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (id.Contains("snapshot", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("-pre", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("-rc", StringComparison.OrdinalIgnoreCase) ||
            WeeklySnapshotName.IsMatch(id))
        {
            return true;
        }

        return false;
    }

    private static string StripPrereleaseSuffix(string id)
    {
        var cut = id.IndexOf('-', StringComparison.Ordinal);
        return cut <= 0 ? id : id[..cut];
    }

    private static string? TryReadVersionType(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("type", out var typeProp) &&
                typeProp.ValueKind == JsonValueKind.String)
            {
                return typeProp.GetString();
            }
        }
        catch
        {
            // ignore malformed launcher metadata
        }

        return null;
    }

    private readonly record struct VersionCandidate(
        string FolderPath,
        string Id,
        Version? ParsedVersion,
        bool IsRelease,
        string JarPath);
}
