namespace AutoPBR.Preview;

/// <summary>
/// Wiki-informed generative structure and biome affinity for stage-terrain trees.
/// Mapped onto AutoPBR's starter biomes (Plains / Desert / Beach / Mountains) plus climate.
/// </summary>
public static class PreviewTerrainTreeSpeciesRules
{
    public readonly record struct ShapeProfile(
        PreviewTerrainTreeShapeKind Kind,
        int MinTrunkHeight,
        int MaxTrunkHeight,
        int CanopyRadius,
        bool HasBranches);

    /// <summary>Structure defaults keyed by species (Minecraft wiki tree sizes, simplified).</summary>
    public static ShapeProfile GetShape(PreviewTerrainTreeSpecies species) =>
        species switch
        {
            PreviewTerrainTreeSpecies.Birch => new(PreviewTerrainTreeShapeKind.RoundCanopy, 5, 7, 2, false),
            PreviewTerrainTreeSpecies.Spruce => new(PreviewTerrainTreeShapeKind.Conical, 6, 9, 2, false),
            PreviewTerrainTreeSpecies.Jungle => new(PreviewTerrainTreeShapeKind.RoundCanopy, 6, 10, 3, false),
            PreviewTerrainTreeSpecies.Cherry => new(PreviewTerrainTreeShapeKind.RoundCanopy, 4, 6, 3, true),
            PreviewTerrainTreeSpecies.Acacia => new(PreviewTerrainTreeShapeKind.FlatCanopy, 3, 5, 2, true),
            PreviewTerrainTreeSpecies.DarkOak => new(PreviewTerrainTreeShapeKind.RoundCanopy, 5, 7, 3, true),
            PreviewTerrainTreeSpecies.Mangrove => new(PreviewTerrainTreeShapeKind.RoundCanopy, 5, 8, 2, true),
            PreviewTerrainTreeSpecies.PaleOak => new(PreviewTerrainTreeShapeKind.RoundCanopy, 5, 7, 3, true),
            PreviewTerrainTreeSpecies.Cactus => new(PreviewTerrainTreeShapeKind.Column, 1, 3, 0, false),
            _ => new(PreviewTerrainTreeShapeKind.RoundCanopy, 4, 6, 2, false), // oak
        };

    /// <summary>
    /// Preferred wood species for a biome + climate sample. Returns false when vegetation
    /// should not spawn (beach, unsupported surface climate, etc.).
    /// Cactus is selected separately for desert.
    /// </summary>
    public static bool TryPickWoodSpecies(
        byte biomeId,
        float temperature,
        float humidity,
        out PreviewTerrainTreeSpecies species)
    {
        // Biome ids mirror PreviewTerrainBiomeId in AutoPBR.App (Plains=0, Desert=1, Beach=2, Mountains=3).
        species = PreviewTerrainTreeSpecies.Oak;
        temperature = Math.Clamp(temperature, 0f, 1f);
        humidity = Math.Clamp(humidity, 0f, 1f);

        switch (biomeId)
        {
            case 1: // Desert — cactus handled elsewhere
            case 2: // Beach — no trees
                return false;
            case 3: // Mountains — spruce / cold woods (wiki: windswept hills, taiga edges)
                species = temperature < 0.55f
                    ? PreviewTerrainTreeSpecies.Spruce
                    : PreviewTerrainTreeSpecies.Oak;
                return true;
            default: // Plains + soft climate variety
                if (temperature > 0.70f && humidity > 0.55f)
                {
                    species = PreviewTerrainTreeSpecies.Jungle;
                }
                else if (temperature > 0.65f && humidity < 0.40f)
                {
                    species = PreviewTerrainTreeSpecies.Acacia;
                }
                else if (temperature < 0.40f && humidity > 0.35f)
                {
                    species = PreviewTerrainTreeSpecies.Spruce;
                }
                else if (temperature is >= 0.42f and <= 0.62f && humidity > 0.62f)
                {
                    species = PreviewTerrainTreeSpecies.Cherry;
                }
                else if (temperature is >= 0.38f and <= 0.52f && humidity is >= 0.55f and <= 0.70f)
                {
                    species = PreviewTerrainTreeSpecies.Birch;
                }
                else if (humidity > 0.72f && temperature is >= 0.45f and <= 0.65f)
                {
                    species = PreviewTerrainTreeSpecies.DarkOak;
                }
                else
                {
                    species = PreviewTerrainTreeSpecies.Oak;
                }

                return true;
        }
    }

    /// <summary>
    /// Fallback chain when the preferred species textures are missing.
    /// Always ends with oak when any wood is available.
    /// </summary>
    public static PreviewTerrainTreeSpecies[] FallbackChain(PreviewTerrainTreeSpecies preferred) =>
        preferred switch
        {
            PreviewTerrainTreeSpecies.Spruce =>
            [
                PreviewTerrainTreeSpecies.Spruce,
                PreviewTerrainTreeSpecies.Oak,
            ],
            PreviewTerrainTreeSpecies.Birch =>
            [
                PreviewTerrainTreeSpecies.Birch,
                PreviewTerrainTreeSpecies.Oak,
            ],
            PreviewTerrainTreeSpecies.Jungle =>
            [
                PreviewTerrainTreeSpecies.Jungle,
                PreviewTerrainTreeSpecies.Oak,
            ],
            PreviewTerrainTreeSpecies.Cherry =>
            [
                PreviewTerrainTreeSpecies.Cherry,
                PreviewTerrainTreeSpecies.Birch,
                PreviewTerrainTreeSpecies.Oak,
            ],
            PreviewTerrainTreeSpecies.Acacia =>
            [
                PreviewTerrainTreeSpecies.Acacia,
                PreviewTerrainTreeSpecies.Oak,
            ],
            PreviewTerrainTreeSpecies.DarkOak =>
            [
                PreviewTerrainTreeSpecies.DarkOak,
                PreviewTerrainTreeSpecies.Oak,
            ],
            PreviewTerrainTreeSpecies.Mangrove =>
            [
                PreviewTerrainTreeSpecies.Mangrove,
                PreviewTerrainTreeSpecies.Oak,
            ],
            PreviewTerrainTreeSpecies.PaleOak =>
            [
                PreviewTerrainTreeSpecies.PaleOak,
                PreviewTerrainTreeSpecies.DarkOak,
                PreviewTerrainTreeSpecies.Oak,
            ],
            _ => [PreviewTerrainTreeSpecies.Oak],
        };

    /// <summary>Approximate spawn chance per candidate cell (wiki density → stage preview).</summary>
    public static float SpawnChance(byte biomeId, PreviewTerrainTreeSpecies species)
    {
        if (species == PreviewTerrainTreeSpecies.Cactus)
        {
            return 0.045f;
        }

        return biomeId switch
        {
            3 => 0.028f, // Mountains — sparse
            _ => species switch
            {
                PreviewTerrainTreeSpecies.Jungle => 0.055f,
                PreviewTerrainTreeSpecies.DarkOak => 0.040f,
                PreviewTerrainTreeSpecies.Cherry => 0.035f,
                PreviewTerrainTreeSpecies.Birch => 0.032f,
                PreviewTerrainTreeSpecies.Acacia => 0.022f,
                PreviewTerrainTreeSpecies.Spruce => 0.030f,
                _ => 0.024f, // oak plains — very sparse per wiki
            },
        };
    }

    /// <summary>Minimum Chebyshev spacing between decoration roots in the same chunk.</summary>
    public static int MinSpacing(PreviewTerrainTreeSpecies species) =>
        species == PreviewTerrainTreeSpecies.Cactus ? 3 : 5;
}
