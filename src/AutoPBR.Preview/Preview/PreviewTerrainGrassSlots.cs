namespace AutoPBR.Preview;

/// <summary>Material slot indices for biome-aware stage terrain meshes.</summary>
public static class PreviewTerrainGrassSlots
{
    public const int Top = 0;
    public const int GrassTop = Top;

    public const int Side = 1;
    public const int GrassSide = Side;

    public const int Dirt = 2;

    public const int Overlay = 3;
    public const int GrassOverlay = Overlay;

    public const int Stone = 4;
    public const int Sand = 5;
    public const int Gravel = 6;

    /// <summary>Full slot count including stone/sand/gravel.</summary>
    public const int MaxCount = 7;

    /// <summary>Legacy grass-only count when overlay is present.</summary>
    public const int GrassWithOverlayCount = 4;

    /// <summary>Slot count when overlay is absent (top/side/dirt only).</summary>
    public const int WithoutOverlayCount = 3;
}
