namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Climate-driven biome + height + block-stack sampling for stage terrain.
/// Flat pad under the subject is always Plains at height 0.
/// </summary>
public static class PreviewTerrainBiomeSampler
{
    /// <summary>
    /// Full column sample. Thread-safe and deterministic for a fixed seed.
    /// </summary>
    public static PreviewTerrainColumnSample Sample(
        int x,
        int z,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks,
        int seed = PreviewStageConstants.TerrainHeightSeed)
    {
        flatPadHalfExtent = Math.Max(0, flatPadHalfExtent);
        transitionBlocks = Math.Max(0, transitionBlocks);

        var chebyshev = Math.Max(Math.Abs(x), Math.Abs(z));
        if (chebyshev <= flatPadHalfExtent)
        {
            return PlainsColumn(height: 0);
        }

        SampleClimate(x, z, seed, out var temperature, out var humidity, out var continental);
        var biome = ClassifyBiome(temperature, humidity, continental);
        var rawHeight = SampleBiomeHeight(x, z, seed, biome);
        var height = ApplyPadTransition(rawHeight, chebyshev, flatPadHalfExtent, transitionBlocks);
        return BuildColumn(height, biome, continental);
    }

    /// <summary>Height-only accessor (wraps <see cref="Sample"/>).</summary>
    public static int SampleHeight(
        int x,
        int z,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks,
        int seed = PreviewStageConstants.TerrainHeightSeed) =>
        Sample(x, z, flatPadHalfExtent, transitionBlocks, seed).Height;

    public static void SampleClimate(
        int x,
        int z,
        int seed,
        out float temperature,
        out float humidity,
        out float continental)
    {
        var climateSeed = seed ^ PreviewStageConstants.TerrainClimateSeedSalt;
        // Low-frequency climate fields in [0, 1].
        temperature = Noise01(x * 0.008f, z * 0.008f, climateSeed);
        humidity = Noise01(x * 0.0095f, z * 0.0095f, climateSeed ^ unchecked((int)0xA5A5A5A5));
        continental = Noise01(x * 0.0065f, z * 0.0065f, climateSeed ^ unchecked((int)0x3C6EF372));
    }

    public static PreviewTerrainBiomeId ClassifyBiome(float temperature, float humidity, float continental)
    {
        temperature = Math.Clamp(temperature, 0f, 1f);
        humidity = Math.Clamp(humidity, 0f, 1f);
        continental = Math.Clamp(continental, 0f, 1f);

        // Cold + high continental → mountains.
        if (continental > 0.55f && temperature < 0.55f)
        {
            return PreviewTerrainBiomeId.Mountains;
        }

        // Hot + dry → desert.
        if (temperature > 0.62f && humidity < 0.38f)
        {
            return PreviewTerrainBiomeId.Desert;
        }

        // Coastal fringe: low continental (and not desert).
        if (continental < 0.34f)
        {
            return PreviewTerrainBiomeId.Beach;
        }

        return PreviewTerrainBiomeId.Plains;
    }

    private static int SampleBiomeHeight(int x, int z, int seed, PreviewTerrainBiomeId biome)
    {
        return biome switch
        {
            PreviewTerrainBiomeId.Desert => SampleDesertHeight(x, z, seed),
            PreviewTerrainBiomeId.Beach => SampleBeachHeight(x, z, seed),
            PreviewTerrainBiomeId.Mountains => SampleMountainHeight(x, z, seed),
            _ => SamplePlainsHeight(x, z, seed),
        };
    }

    private static int SamplePlainsHeight(int x, int z, int seed)
    {
        var max = PreviewStageConstants.TerrainMaxReliefBlocks;
        var n = PreviewTerrainHeightfield.SampleFbm(x, z, seed);
        return ClampRelief((int)Math.Round(n * max), max);
    }

    private static int SampleDesertHeight(int x, int z, int seed)
    {
        var max = PreviewStageConstants.TerrainDesertMaxReliefBlocks;
        // Slightly stretchier dunes.
        var n =
            PreviewTerrainHeightfield.SampleValueNoise(x * 0.035f, z * 0.035f, seed) * 0.65f +
            PreviewTerrainHeightfield.SampleValueNoise(x * 0.09f, z * 0.09f, seed ^ 0x11111111) * 0.35f;
        n = Math.Clamp(n, -1f, 1f);
        return ClampRelief((int)Math.Round(n * max), max);
    }

    private static int SampleBeachHeight(int x, int z, int seed)
    {
        var max = PreviewStageConstants.TerrainBeachMaxReliefBlocks;
        var n = PreviewTerrainHeightfield.SampleFbm(x, z, seed ^ unchecked((int)0xBEAC0001));
        // Bias toward low/coastal.
        var relief = (int)Math.Round((n * 0.55f - 0.15f) * max);
        return Math.Clamp(relief, -max, max);
    }

    private static int SampleMountainHeight(int x, int z, int seed)
    {
        var max = PreviewStageConstants.TerrainMountainMaxReliefBlocks;
        var baseN = PreviewTerrainHeightfield.SampleFbm(x, z, seed);
        // Sharp ridges: cubic falloff so neighboring columns often jump multiple blocks.
        var ridge = PreviewTerrainHeightfield.SampleValueNoise(x * 0.05f, z * 0.05f, seed ^ unchecked((int)0x51D6E001));
        ridge = 1f - MathF.Abs(ridge);
        ridge = ridge * ridge * ridge;
        // Coarse terrace hash creates intentional multi-block cliff faces.
        var terrace = PreviewTerrainHeightfield.Hash01(x >> 1, z >> 1, seed ^ unchecked((int)0x7E55ACE0));
        terrace = terrace * 2f - 1f;
        var detail = PreviewTerrainHeightfield.SampleValueNoise(x * 0.22f, z * 0.22f, seed ^ 0x51A1E001) * 0.15f;
        var n = Math.Clamp(baseN * 0.20f + ridge * 0.85f + terrace * 0.40f + detail - 0.05f, -1f, 1f);
        return ClampRelief((int)Math.Round(n * max), max);
    }

    private static int ApplyPadTransition(
        int relief,
        int chebyshev,
        int pad,
        int transitionBlocks)
    {
        var blendEnd = pad + transitionBlocks;
        if (chebyshev < blendEnd && transitionBlocks > 0)
        {
            var t = (chebyshev - pad) / (float)transitionBlocks;
            t = Math.Clamp(t, 0f, 1f);
            var s = t * t * (3f - 2f * t);
            return (int)Math.Round(relief * s);
        }

        return relief;
    }

    private static PreviewTerrainColumnSample BuildColumn(
        int height,
        PreviewTerrainBiomeId biome,
        float continental)
    {
        return biome switch
        {
            PreviewTerrainBiomeId.Desert => new PreviewTerrainColumnSample(
                height,
                PreviewTerrainBiomeId.Desert,
                Surface: PreviewTerrainBlockKind.Sand,
                Subsurface: PreviewTerrainBlockKind.Sand,
                Deep: PreviewTerrainBlockKind.Stone),
            PreviewTerrainBiomeId.Beach => new PreviewTerrainColumnSample(
                height,
                PreviewTerrainBiomeId.Beach,
                Surface: PreviewTerrainBlockKind.Sand,
                Subsurface: PreviewTerrainBlockKind.Sand,
                Deep: PreviewTerrainBlockKind.Stone),
            PreviewTerrainBiomeId.Mountains => BuildMountainColumn(height, continental),
            _ => PlainsColumn(height),
        };
    }

    private static PreviewTerrainColumnSample BuildMountainColumn(int height, float continental)
    {
        // High/steep peaks: stone surface; gentler shoulders: grass.
        var rocky = continental > 0.72f || height >= PreviewStageConstants.TerrainMaxReliefBlocks + 4;
        return new PreviewTerrainColumnSample(
            height,
            PreviewTerrainBiomeId.Mountains,
            Surface: rocky ? PreviewTerrainBlockKind.Stone : PreviewTerrainBlockKind.Grass,
            Subsurface: rocky ? PreviewTerrainBlockKind.Gravel : PreviewTerrainBlockKind.Dirt,
            Deep: PreviewTerrainBlockKind.Stone);
    }

    private static PreviewTerrainColumnSample PlainsColumn(int height) =>
        new(
            height,
            PreviewTerrainBiomeId.Plains,
            Surface: PreviewTerrainBlockKind.Grass,
            Subsurface: PreviewTerrainBlockKind.Dirt,
            Deep: PreviewTerrainBlockKind.Stone);

    private static int ClampRelief(int relief, int max) => Math.Clamp(relief, -max, max);

    private static float Noise01(float x, float z, int seed)
    {
        var n = PreviewTerrainHeightfield.SampleValueNoise(x, z, seed);
        return Math.Clamp((n + 1f) * 0.5f, 0f, 1f);
    }
}
