namespace AutoPBR.Preview;

/// <summary>How the 3D stage terrain samples grass materials.</summary>
public enum PreviewTerrainGrassMode
{
    /// <summary>Legacy single LabPBR <c>grass_block_top</c> on all faces (bundled fallback).</summary>
    BuiltInSingleTop = 0,

    /// <summary>BlockModel-style face slots: top / side / dirt / optional overlay.</summary>
    BlockModelFaces = 1,
}
