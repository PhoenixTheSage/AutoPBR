namespace AutoPBR.Preview;

/// <summary>
/// Resolves IR shard version labels for a native profile.
/// Temporarily falls back to the catalogued modern set (<see cref="ModernGeometryLabel"/>)
/// when the profile reports any other recognized version (until dynamic mesh loading covers other versions).
/// </summary>
internal static class NativeIrVersionLabels
{
    public const string ModernGeometryLabel = "26.1.2";

    public static bool IsRecognizedProfileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !string.Equals(name, "root", StringComparison.Ordinal) &&
        !string.Equals(name, "unknown", StringComparison.OrdinalIgnoreCase) &&
        MinecraftNativeProfileResolver.TryParseVersionLike(name) is not null;

    /// <summary>
    /// IR asset lookup order (geometry, animation, preview-deltas, …). Tries the profile's own
    /// version folder first, then <see cref="ModernGeometryLabel"/> when that is the only
    /// catalogued set. Texture/UV usually still match; mismatches can be reported by users.
    /// </summary>
    public static IEnumerable<string> ForProfile(MinecraftNativeProfile? profile)
    {
        if (profile is { Name: var n } && IsRecognizedProfileName(n))
        {
            yield return n;
            // Temporary: only 26.1.2 IR is catalogued; fall back for any other recognized label
            // (including legacy 1.21.11, nearby modern patches, and newer games like 26.2).
            if (!string.Equals(n, ModernGeometryLabel, StringComparison.Ordinal))
            {
                yield return ModernGeometryLabel;
            }

            yield break;
        }

        yield return ModernGeometryLabel;
    }

    /// <summary>
    /// Geometry IR lookup order. Same temporary catalog fallback as <see cref="ForProfile"/>.
    /// </summary>
    public static IEnumerable<string> ForGeometryIrShardLookup(MinecraftNativeProfile? profile) =>
        ForProfile(profile);

    public static string? PrimaryForProfile(MinecraftNativeProfile? profile)
    {
        foreach (var label in ForProfile(profile))
        {
            return label;
        }

        return null;
    }
}
