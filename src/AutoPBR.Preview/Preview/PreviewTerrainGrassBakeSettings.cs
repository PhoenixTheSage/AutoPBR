namespace AutoPBR.Preview;

/// <summary>CPU bake flags for Full terrain chunks (LOD always uses Top-only).</summary>
public readonly record struct PreviewTerrainGrassBakeSettings(
    PreviewTerrainGrassMode Mode,
    bool BetterGrassEnabled,
    bool EmitOverlay,
    bool HasStone = false,
    bool HasSand = false,
    bool HasGravel = false)
{
    public static PreviewTerrainGrassBakeSettings BuiltIn { get; } =
        new(PreviewTerrainGrassMode.BuiltInSingleTop, BetterGrassEnabled: false, EmitOverlay: false);

    public static PreviewTerrainGrassBakeSettings FromKit(PreviewTerrainGrassKit kit) =>
        new(
            kit.Mode,
            kit.BetterGrassEnabled,
            kit.EmitOverlay,
            HasStone: kit.Stone is not null || kit.StoneAliased,
            HasSand: kit.Sand is not null || kit.SandAliased,
            HasGravel: kit.Gravel is not null || kit.GravelAliased);

    /// <summary>Biome mesh rules always enabled; materials may be aliased to grass-top.</summary>
    public bool UseBiomeMaterials => true;
}
