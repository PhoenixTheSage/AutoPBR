namespace AutoPBR.Preview;

/// <summary>Generative canopy / trunk style (wiki-inspired, simplified for stage terrain).</summary>
public enum PreviewTerrainTreeShapeKind : byte
{
    /// <summary>Classic oak/birch/jungle round leaf blob around the trunk top.</summary>
    RoundCanopy = 0,

    /// <summary>Layered spruce/pine cone that narrows with height.</summary>
    Conical = 1,

    /// <summary>Acacia-style flat canopy, often offset on a short branch.</summary>
    FlatCanopy = 2,

    /// <summary>Desert cactus column (no leaves).</summary>
    Column = 3,
}
