namespace AutoPBR.App.Rendering.Scene;

/// <summary>Per-column biome height and block stack for Full terrain bakes.</summary>
public readonly record struct PreviewTerrainColumnSample(
    int Height,
    PreviewTerrainBiomeId Biome,
    PreviewTerrainBlockKind Surface,
    PreviewTerrainBlockKind Subsurface,
    PreviewTerrainBlockKind Deep);
