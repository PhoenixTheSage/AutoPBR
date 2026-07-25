namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Climate-driven biome + height + block-stack sampling for stage terrain.
/// Flat pad under the subject is always Plains at height 0.
/// Biome borders soft-blend height; mountains use analytical advanced erosion.
/// </summary>
public static class PreviewTerrainBiomeSampler
{
    /// <summary>
    /// Full column sample. Thread-safe and deterministic for a fixed world-gen settings.
    /// </summary>
    public static PreviewTerrainColumnSample Sample(
        int x,
        int z,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks,
        int seed = PreviewStageConstants.TerrainHeightSeed) =>
        Sample(x, z, PreviewTerrainWorldGenSettings.Default with { Seed = seed }, flatPadHalfExtent, transitionBlocks);

    /// <summary>
    /// Full column sample with runtime world-gen modifiers.
    /// </summary>
    public static PreviewTerrainColumnSample Sample(
        int x,
        int z,
        in PreviewTerrainWorldGenSettings worldGen,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks)
    {
        flatPadHalfExtent = Math.Max(0, flatPadHalfExtent);
        transitionBlocks = Math.Max(0, transitionBlocks);
        var gen = PreviewTerrainWorldGenSettings.Resolve(worldGen);

        var chebyshev = Math.Max(Math.Abs(x), Math.Abs(z));
        if (chebyshev <= flatPadHalfExtent)
        {
            return PlainsColumn(height: 0);
        }

        SampleClimate(x, z, gen, out var temperature, out var humidity, out var continental);
        ComputeBiomeWeights(
            temperature,
            humidity,
            continental,
            PreviewStageConstants.TerrainBiomeBlendHalfWidth,
            out var wMountains,
            out var wDesert,
            out var wBeach,
            out var wPlains);

        var biome = DominantBiome(wMountains, wDesert, wBeach, wPlains);
        var blended = SampleBlendedHeight(x, z, gen, wMountains, wDesert, wBeach, wPlains);
        var height = ApplyPadTransition(
            (int)Math.Round(blended),
            chebyshev,
            flatPadHalfExtent,
            transitionBlocks);
        return BuildColumn(height, biome, continental);
    }

    /// <summary>Height-only accessor (wraps <see cref="Sample(int,int,in PreviewTerrainWorldGenSettings,int,int)"/>).</summary>
    public static int SampleHeight(
        int x,
        int z,
        in PreviewTerrainWorldGenSettings worldGen,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks) =>
        Sample(x, z, worldGen, flatPadHalfExtent, transitionBlocks).Height;

    public static void SampleClimate(
        int x,
        int z,
        in PreviewTerrainWorldGenSettings worldGen,
        out float temperature,
        out float humidity,
        out float continental)
    {
        var gen = PreviewTerrainWorldGenSettings.Resolve(worldGen);
        var climateSeed = gen.Seed ^ PreviewStageConstants.TerrainClimateSeedSalt;
        // Larger BiomeSize → lower frequency → bigger regions.
        var freq = 1f / gen.BiomeSize;
        temperature = Noise01(x * 0.008f * freq, z * 0.008f * freq, climateSeed);
        humidity = Noise01(
            x * 0.0095f * freq,
            z * 0.0095f * freq,
            climateSeed ^ unchecked((int)0xA5A5A5A5));
        continental = Noise01(
            x * 0.0065f * freq,
            z * 0.0065f * freq,
            climateSeed ^ 0x3C6EF372);
        // Stretch continental around 0.5 so islands ↔ continents can be biased.
        continental = Math.Clamp((continental - 0.5f) * gen.Continentalness + 0.5f, 0f, 1f);
    }

    /// <summary>
    /// Hard classification (threshold midpoints). Prefer soft weights for geometry;
    /// this remains for tests and material-dominant decisions at exact thresholds.
    /// </summary>
    public static PreviewTerrainBiomeId ClassifyBiome(float temperature, float humidity, float continental)
    {
        ComputeBiomeWeights(
            temperature,
            humidity,
            continental,
            blendHalfWidth: 0f,
            out var wMountains,
            out var wDesert,
            out var wBeach,
            out var wPlains);
        return DominantBiome(wMountains, wDesert, wBeach, wPlains);
    }

    /// <summary>
    /// Soft biome affinities that preserve hard-classify priority
    /// (Mountains → Desert → Beach → Plains) while blending near thresholds.
    /// Weights always sum to 1.
    /// </summary>
    public static void ComputeBiomeWeights(
        float temperature,
        float humidity,
        float continental,
        float blendHalfWidth,
        out float mountains,
        out float desert,
        out float beach,
        out float plains)
    {
        temperature = Math.Clamp(temperature, 0f, 1f);
        humidity = Math.Clamp(humidity, 0f, 1f);
        continental = Math.Clamp(continental, 0f, 1f);
        blendHalfWidth = Math.Max(0f, blendHalfWidth);

        var mCont = SoftGateHigh(continental, threshold: 0.55f, blendHalfWidth);
        var mTemp = SoftGateLow(temperature, threshold: 0.55f, blendHalfWidth);
        var mAff = mCont * mTemp;

        var dTemp = SoftGateHigh(temperature, threshold: 0.62f, blendHalfWidth);
        var dHum = SoftGateLow(humidity, threshold: 0.38f, blendHalfWidth);
        var dAff = dTemp * dHum * (1f - mAff);

        var bAff = SoftGateLow(continental, threshold: 0.34f, blendHalfWidth) * (1f - mAff) * (1f - dAff);

        var pAff = Math.Max(0f, 1f - mAff - dAff - bAff);
        var sum = mAff + dAff + bAff + pAff;
        if (sum <= 1e-8f)
        {
            mountains = 0f;
            desert = 0f;
            beach = 0f;
            plains = 1f;
            return;
        }

        mountains = mAff / sum;
        desert = dAff / sum;
        beach = bAff / sum;
        plains = pAff / sum;
    }

    private static float SampleBlendedHeight(
        int x,
        int z,
        in PreviewTerrainWorldGenSettings gen,
        float wMountains,
        float wDesert,
        float wBeach,
        float wPlains)
    {
        const float eps = 1e-4f;
        var h = 0f;
        if (wPlains > eps)
        {
            h += wPlains * SamplePlainsHeightContinuous(x, z, gen.Seed);
        }

        if (wDesert > eps)
        {
            h += wDesert * SampleDesertHeightContinuous(x, z, gen.Seed);
        }

        if (wBeach > eps)
        {
            h += wBeach * SampleBeachHeightContinuous(x, z, gen.Seed);
        }

        if (wMountains > eps)
        {
            h += wMountains * SampleMountainHeightContinuous(x, z, gen);
        }

        h *= gen.Amplification;
        var maxAbs = (int)Math.Ceiling(
            PreviewStageConstants.TerrainMountainMaxReliefBlocks * gen.Amplification);
        return Math.Clamp(h, -maxAbs, maxAbs);
    }

    private static float SamplePlainsHeightContinuous(int x, int z, int seed)
    {
        var max = PreviewStageConstants.TerrainMaxReliefBlocks;
        return PreviewTerrainHeightfield.SampleFbm(x, z, seed) * max;
    }

    private static float SampleDesertHeightContinuous(int x, int z, int seed)
    {
        var max = PreviewStageConstants.TerrainDesertMaxReliefBlocks;
        var n =
            PreviewTerrainHeightfield.SampleValueNoise(x * 0.035f, z * 0.035f, seed) * 0.65f +
            PreviewTerrainHeightfield.SampleValueNoise(x * 0.09f, z * 0.09f, seed ^ 0x11111111) * 0.35f;
        return Math.Clamp(n, -1f, 1f) * max;
    }

    private static float SampleBeachHeightContinuous(int x, int z, int seed)
    {
        var max = PreviewStageConstants.TerrainBeachMaxReliefBlocks;
        var n = PreviewTerrainHeightfield.SampleFbm(x, z, seed ^ unchecked((int)0xBEAC0001));
        return Math.Clamp((n * 0.55f - 0.15f) * max, -max, max);
    }

    private static float SampleMountainHeightContinuous(
        int x,
        int z,
        in PreviewTerrainWorldGenSettings gen)
    {
        var max = PreviewStageConstants.TerrainMountainMaxReliefBlocks;
        var n = PreviewTerrainAdvancedErosion.SampleErodedMountainNormalized(
            x, z, gen.Seed, gen.ErosionStrength);
        return n * max;
    }

    private static PreviewTerrainBiomeId DominantBiome(
        float mountains,
        float desert,
        float beach,
        float plains)
    {
        var best = PreviewTerrainBiomeId.Plains;
        var bestW = plains;
        if (beach >= bestW)
        {
            best = PreviewTerrainBiomeId.Beach;
            bestW = beach;
        }

        if (desert >= bestW)
        {
            best = PreviewTerrainBiomeId.Desert;
            bestW = desert;
        }

        if (mountains >= bestW)
        {
            best = PreviewTerrainBiomeId.Mountains;
        }

        return best;
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

    private static float SoftGateHigh(float value, float threshold, float blendHalfWidth)
    {
        if (blendHalfWidth <= 0f)
        {
            return value > threshold ? 1f : 0f;
        }

        return Smoothstep(threshold - blendHalfWidth, threshold + blendHalfWidth, value);
    }

    private static float SoftGateLow(float value, float threshold, float blendHalfWidth)
    {
        if (blendHalfWidth <= 0f)
        {
            return value < threshold ? 1f : 0f;
        }

        return 1f - Smoothstep(threshold - blendHalfWidth, threshold + blendHalfWidth, value);
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0)
        {
            return x < edge0 ? 0f : 1f;
        }

        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Noise01(float x, float z, int seed)
    {
        var n = PreviewTerrainHeightfield.SampleValueNoise(x, z, seed);
        return Math.Clamp((n + 1f) * 0.5f, 0f, 1f);
    }
}
