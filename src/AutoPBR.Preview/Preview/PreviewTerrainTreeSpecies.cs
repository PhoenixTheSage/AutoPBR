namespace AutoPBR.Preview;

/// <summary>
/// Wood / cactus species inferred from Minecraft block texture names
/// (<c>{id}_log</c> + matching <c>{id}_leaves</c>, or cactus side/top).
/// </summary>
public enum PreviewTerrainTreeSpecies : byte
{
    Oak = 0,
    Spruce = 1,
    Birch = 2,
    Jungle = 3,
    Acacia = 4,
    DarkOak = 5,
    Mangrove = 6,
    Cherry = 7,
    PaleOak = 8,
    Cactus = 9,
}

/// <summary>Canonical texture-stem ids used under <c>assets/minecraft/textures/block/</c>.</summary>
public static class PreviewTerrainTreeSpeciesIds
{
    public const string Oak = "oak";
    public const string Spruce = "spruce";
    public const string Birch = "birch";
    public const string Jungle = "jungle";
    public const string Acacia = "acacia";
    public const string DarkOak = "dark_oak";
    public const string Mangrove = "mangrove";
    public const string Cherry = "cherry";
    public const string PaleOak = "pale_oak";
    public const string Cactus = "cactus";

    /// <summary>Wood species that use <c>{id}_log</c> / <c>{id}_leaves</c> pairs (excludes cactus).</summary>
    public static readonly string[] WoodTextureStems =
    [
        Oak,
        Spruce,
        Birch,
        Jungle,
        Acacia,
        DarkOak,
        Mangrove,
        Cherry,
        PaleOak,
    ];

    public static string ToTextureStem(PreviewTerrainTreeSpecies species) =>
        species switch
        {
            PreviewTerrainTreeSpecies.Oak => Oak,
            PreviewTerrainTreeSpecies.Spruce => Spruce,
            PreviewTerrainTreeSpecies.Birch => Birch,
            PreviewTerrainTreeSpecies.Jungle => Jungle,
            PreviewTerrainTreeSpecies.Acacia => Acacia,
            PreviewTerrainTreeSpecies.DarkOak => DarkOak,
            PreviewTerrainTreeSpecies.Mangrove => Mangrove,
            PreviewTerrainTreeSpecies.Cherry => Cherry,
            PreviewTerrainTreeSpecies.PaleOak => PaleOak,
            PreviewTerrainTreeSpecies.Cactus => Cactus,
            _ => Oak,
        };

    public static bool TryParseTextureStem(string stem, out PreviewTerrainTreeSpecies species)
    {
        species = stem.ToLowerInvariant() switch
        {
            Oak => PreviewTerrainTreeSpecies.Oak,
            Spruce => PreviewTerrainTreeSpecies.Spruce,
            Birch => PreviewTerrainTreeSpecies.Birch,
            Jungle => PreviewTerrainTreeSpecies.Jungle,
            Acacia => PreviewTerrainTreeSpecies.Acacia,
            DarkOak => PreviewTerrainTreeSpecies.DarkOak,
            Mangrove => PreviewTerrainTreeSpecies.Mangrove,
            Cherry => PreviewTerrainTreeSpecies.Cherry,
            PaleOak => PreviewTerrainTreeSpecies.PaleOak,
            Cactus => PreviewTerrainTreeSpecies.Cactus,
            _ => (PreviewTerrainTreeSpecies)255,
        };
        return (byte)species != 255;
    }
}
