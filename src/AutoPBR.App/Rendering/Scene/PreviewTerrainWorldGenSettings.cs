namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Runtime world-generation knobs for streamed preview terrain.
/// Pad columns (chebyshev ≤ flat pad) stay height 0 regardless of these values.
/// </summary>
public readonly record struct PreviewTerrainWorldGenSettings(
    int Seed,
    float BiomeSize,
    float Amplification,
    float ErosionStrength,
    float Continentalness)
{
    public static PreviewTerrainWorldGenSettings Default { get; } = new(
        Seed: PreviewStageConstants.TerrainHeightSeed,
        BiomeSize: PreviewStageConstants.TerrainDefaultBiomeSize,
        Amplification: PreviewStageConstants.TerrainDefaultAmplification,
        ErosionStrength: PreviewStageConstants.TerrainDefaultErosionStrength,
        Continentalness: PreviewStageConstants.TerrainDefaultContinentalness);

    public PreviewTerrainWorldGenSettings Clamped() =>
        new(
            Seed,
            Math.Clamp(
                BiomeSize <= 0f ? PreviewStageConstants.TerrainDefaultBiomeSize : BiomeSize,
                PreviewStageConstants.TerrainMinBiomeSize,
                PreviewStageConstants.TerrainMaxBiomeSize),
            Math.Clamp(
                Amplification <= 0f ? PreviewStageConstants.TerrainDefaultAmplification : Amplification,
                PreviewStageConstants.TerrainMinAmplification,
                PreviewStageConstants.TerrainMaxAmplification),
            Math.Clamp(
                ErosionStrength < 0f ? PreviewStageConstants.TerrainDefaultErosionStrength : ErosionStrength,
                PreviewStageConstants.TerrainMinErosionStrength,
                PreviewStageConstants.TerrainMaxErosionStrength),
            Math.Clamp(
                Continentalness <= 0f ? PreviewStageConstants.TerrainDefaultContinentalness : Continentalness,
                PreviewStageConstants.TerrainMinContinentalness,
                PreviewStageConstants.TerrainMaxContinentalness));

    /// <summary>Replace zeroed / incomplete defaults with <see cref="Default"/> then clamp.</summary>
    public static PreviewTerrainWorldGenSettings Resolve(in PreviewTerrainWorldGenSettings value)
    {
        if (value is
            {
                BiomeSize: <= 0f,
                Amplification: <= 0f,
                ErosionStrength: <= 0f,
                Continentalness: <= 0f,
                Seed: 0
            })
        {
            return Default;
        }

        return value.Clamped();
    }
}
