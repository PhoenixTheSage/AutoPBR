namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// How vegetation meshes are stamped for a bake. Placement roots stay Full-identical;
/// only mesh density may change with distance.
/// </summary>
public enum PreviewTerrainVegetationEmitMode
{
    /// <summary>Full 1 m voxel trunks/canopy (Full chunks + LOD1 underlay).</summary>
    FullVoxel = 0,

    /// <summary>
    /// Crossed-plane impostor at the same root (LOD≥2). Keeps distant tree positions without
    /// multi-chunk voxel canopies.
    /// </summary>
    Impostor = 1,
}
