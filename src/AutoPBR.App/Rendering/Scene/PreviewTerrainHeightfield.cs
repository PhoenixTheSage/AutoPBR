namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Seeded Minecraft-style column heights: flat pad under the subject, blended noise hills beyond.
/// Heights are relative block offsets from the pad surface (<see cref="PreviewStageConstants.GroundPlaneWorldY"/>).
/// Infinite world: any integer XZ is valid via <see cref="SampleColumn(int,int,in PreviewTerrainWorldGenSettings,int,int,int)"/>.
/// </summary>
public static class PreviewTerrainHeightfield
{
    /// <summary>
    /// Column height at world block XZ. Thread-safe and deterministic for a fixed seed.
    /// </summary>
    public static int SampleColumn(
        int x,
        int z,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks,
        int maxRelief = PreviewStageConstants.TerrainMaxReliefBlocks,
        int seed = PreviewStageConstants.TerrainHeightSeed) =>
        SampleColumn(
            x,
            z,
            PreviewTerrainWorldGenSettings.Default with { Seed = seed },
            flatPadHalfExtent,
            transitionBlocks,
            maxRelief);

    public static int SampleColumn(
        int x,
        int z,
        in PreviewTerrainWorldGenSettings worldGen,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks,
        int maxRelief = PreviewStageConstants.TerrainMaxReliefBlocks)
    {
        // Biome-aware height (pad/transition handled inside sampler). maxRelief is ignored —
        // biomes supply their own relief caps. Parameter retained for call-site compatibility.
        _ = maxRelief;
        return PreviewTerrainBiomeSampler.SampleHeight(x, z, worldGen, flatPadHalfExtent, transitionBlocks);
    }

    /// <summary>
    /// Builds a square height map of side <c>2 * halfExtent</c>. Indexing is
    /// <c>heights[(z + halfExtent) * side + (x + halfExtent)]</c> for column world XZ integers.
    /// Kept for tests / legacy multi-chunk bake.
    /// </summary>
    public static int[] BuildColumnHeights(
        int halfExtent = PreviewStageConstants.TerrainHalfExtent,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks,
        int maxRelief = PreviewStageConstants.TerrainMaxReliefBlocks,
        int seed = PreviewStageConstants.TerrainHeightSeed)
    {
        if (halfExtent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(halfExtent));
        }

        flatPadHalfExtent = Math.Clamp(flatPadHalfExtent, 0, halfExtent);
        var side = halfExtent * 2;
        var heights = new int[side * side];
        for (var z = -halfExtent; z < halfExtent; z++)
        {
            for (var x = -halfExtent; x < halfExtent; x++)
            {
                heights[ToIndex(x, z, halfExtent)] = SampleColumn(
                    x, z, flatPadHalfExtent, transitionBlocks, maxRelief, seed);
            }
        }

        return heights;
    }

    public static int GetHeight(ReadOnlySpan<int> heights, int x, int z, int halfExtent)
    {
        if (x < -halfExtent || x >= halfExtent || z < -halfExtent || z >= halfExtent)
        {
            return int.MinValue;
        }

        return heights[ToIndex(x, z, halfExtent)];
    }

    public static int ToIndex(int x, int z, int halfExtent)
    {
        var side = halfExtent * 2;
        return (z + halfExtent) * side + (x + halfExtent);
    }

    /// <summary>Fractal Brownian motion in [-1, 1] from value-noise octaves.</summary>
    public static float SampleFbm(int x, int z, int seed)
    {
        var n =
            SampleValueNoise(x * 0.045f, z * 0.045f, seed) * 0.55f +
            SampleValueNoise(x * 0.11f, z * 0.11f, seed ^ unchecked((int)0x9E3779B9)) * 0.30f +
            SampleValueNoise(x * 0.27f, z * 0.27f, seed ^ unchecked((int)0x85EBCA6B)) * 0.15f;
        return Math.Clamp(n, -1f, 1f);
    }

    /// <summary>Interpolated value noise in [-1, 1].</summary>
    internal static float SampleValueNoise(float x, float z, int seed)
    {
        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);
        var fx = x - x0;
        var fz = z - z0;
        var sx = fx * fx * (3f - 2f * fx);
        var sz = fz * fz * (3f - 2f * fz);

        var n00 = Hash01(x0, z0, seed);
        var n10 = Hash01(x0 + 1, z0, seed);
        var n01 = Hash01(x0, z0 + 1, seed);
        var n11 = Hash01(x0 + 1, z0 + 1, seed);

        var nx0 = n00 + (n10 - n00) * sx;
        var nx1 = n01 + (n11 - n01) * sx;
        return (nx0 + (nx1 - nx0) * sz) * 2f - 1f;
    }

    /// <summary>Deterministic hash in [0, 1].</summary>
    internal static float Hash01(int x, int z, int seed)
    {
        unchecked
        {
            var h = seed;
            h = (h ^ x) * 0x27D4EB2D;
            h = (h ^ z) * 0x165667B1;
            h ^= h >> 15;
            h *= unchecked((int)0x85EBCA6B);
            h ^= h >> 13;
            return (h & 0x00FFFFFF) / 16777215f;
        }
    }
}
