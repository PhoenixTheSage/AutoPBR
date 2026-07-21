namespace AutoPBR.Preview;

/// <summary>Resolved LabPBR maps and bake flags for stage terrain (grass + biome blocks).</summary>
public sealed class PreviewTerrainGrassKit
{
    public required PreviewTerrainGrassMode Mode { get; init; }

    /// <summary>Identity string for cache invalidation (paths + mode + BetterGrass + aliases).</summary>
    public required string Identity { get; init; }

    /// <summary>When <see cref="Mode"/> is <see cref="PreviewTerrainGrassMode.BuiltInSingleTop"/>, only Top may be set (or null for bundled).</summary>
    public PreviewTextureMaps? Top { get; init; }

    public PreviewTextureMaps? Side { get; init; }

    public PreviewTextureMaps? Dirt { get; init; }

    public PreviewTextureMaps? Overlay { get; init; }

    public PreviewTextureMaps? Stone { get; init; }

    public PreviewTextureMaps? Sand { get; init; }

    public PreviewTextureMaps? Gravel { get; init; }

    /// <summary>True when Stone slot should use Top/bundled grass as a stand-in.</summary>
    public bool StoneAliased { get; init; }

    public bool SandAliased { get; init; }

    public bool GravelAliased { get; init; }

    public string TopArchivePath { get; init; } = PreviewGroundMapsResolver.GrassBlockTopArchivePath;

    public string SideArchivePath { get; init; } = PreviewTerrainGrassKitResolver.GrassBlockSideArchivePath;

    public string DirtArchivePath { get; init; } = PreviewTerrainGrassKitResolver.DirtArchivePath;

    public string? OverlayArchivePath { get; init; }

    public string StoneArchivePath { get; init; } = PreviewTerrainGrassKitResolver.StoneArchivePath;

    public string SandArchivePath { get; init; } = PreviewTerrainGrassKitResolver.SandArchivePath;

    public string GravelArchivePath { get; init; } = PreviewTerrainGrassKitResolver.GravelArchivePath;

    public PreviewTerrainBetterGrassProperties BetterGrass { get; init; } =
        PreviewTerrainBetterGrassProperties.Default;

    /// <summary>Fancy BetterGrass side→top replacement on height steps.</summary>
    public bool BetterGrassEnabled =>
        Mode == PreviewTerrainGrassMode.BlockModelFaces && BetterGrass.GrassEnabled;

    public bool EmitOverlay =>
        Mode == PreviewTerrainGrassMode.BlockModelFaces &&
        Overlay is not null &&
        !string.IsNullOrWhiteSpace(OverlayArchivePath);

    public static PreviewTerrainGrassKit BuiltIn(PreviewTextureMaps? topMaps = null) =>
        new()
        {
            Mode = PreviewTerrainGrassMode.BuiltInSingleTop,
            Identity = "builtin-single-top|stoneA|sandA|gravelA",
            Top = topMaps,
            StoneAliased = true,
            SandAliased = true,
            GravelAliased = true,
            BetterGrass = PreviewTerrainBetterGrassProperties.Default,
        };
}
