namespace AutoPBR.Preview;

/// <summary>CPU bake flags for Full chunks and combined distant LOD sections.</summary>
public readonly record struct PreviewTerrainGrassBakeSettings(
    PreviewTerrainGrassMode Mode,
    bool BetterGrassEnabled,
    bool EmitOverlay,
    bool HasStone = false,
    bool HasSand = false,
    bool HasGravel = false,
    string VegetationIdentity = "")
{
    public static PreviewTerrainGrassBakeSettings BuiltIn { get; } =
        new(PreviewTerrainGrassMode.BuiltInSingleTop, BetterGrassEnabled: false, EmitOverlay: false);

    public static PreviewTerrainGrassBakeSettings FromKit(
        PreviewTerrainGrassKit kit,
        PreviewTerrainVegetationKit? vegetation = null) =>
        new(
            kit.Mode,
            kit.BetterGrassEnabled,
            kit.EmitOverlay,
            HasStone: kit.Stone is not null || kit.StoneAliased,
            HasSand: kit.Sand is not null || kit.SandAliased,
            HasGravel: kit.Gravel is not null || kit.GravelAliased,
            VegetationIdentity: vegetation is { HasAny: true } ? vegetation.Identity : "");

    /// <summary>Biome mesh rules always enabled; materials may be aliased to grass-top.</summary>
    public bool UseBiomeMaterials => true;

    /// <summary>True when a vegetation kit identity is present (trees/cacti may bake).</summary>
    public bool EmitVegetation => !string.IsNullOrEmpty(VegetationIdentity);
}
