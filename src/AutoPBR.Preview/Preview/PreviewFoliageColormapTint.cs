using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.Preview;

/// <summary>
/// Java edition biome leaf tint from <c>textures/colormap/foliage.png</c> applied to grayscale leaf albedos.
/// </summary>
public static class PreviewFoliageColormapTint
{
    public const string FoliageColormapArchivePath = "assets/minecraft/textures/colormap/foliage.png";

    /// <summary>
    /// Vanilla leaf textures that sample the foliage colormap (gray mask × biome tint).
    /// Pre-colored leaves (e.g. cherry) are skipped by the grayscale heuristic at tint time.
    /// </summary>
    public static bool IsFoliageColormapTintIndexPath(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return false;
        }

        var norm = archivePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        return norm.EndsWith("_leaves.png", StringComparison.Ordinal);
    }

    public static bool NeedsFoliageColormapTint(string? archivePath) =>
        IsFoliageColormapTintIndexPath(archivePath);

    /// <summary>Same triangular temperature/downfall lookup as grass, sampling <c>foliage.png</c>.</summary>
    public static Rgba32 SampleFoliageTint(
        PreviewColormapImage colormap,
        double temperature01,
        double downfall01) =>
        PreviewGrassColormapTint.SampleGrassTint(colormap, temperature01, downfall01);

    public static PreviewTextureMaps WithFoliageTint(
        PreviewTextureMaps maps,
        string? archivePath,
        PreviewColormapImage colormap,
        double temperature01,
        double downfall01)
    {
        if (!NeedsFoliageColormapTint(archivePath))
        {
            return maps;
        }

        var tint = SampleFoliageTint(colormap, temperature01, downfall01);
        return WithFoliageTint(maps, archivePath, tint);
    }

    public static PreviewTextureMaps WithFoliageTint(
        PreviewTextureMaps maps,
        string? archivePath,
        Rgba32 tint)
    {
        if (!NeedsFoliageColormapTint(archivePath))
        {
            return maps;
        }

        var tinted = PreviewGrassColormapTint.ApplyTintToDiffuse(
            maps.DiffuseRgba,
            maps.Width,
            maps.Height,
            tint);
        return new PreviewTextureMaps
        {
            Width = maps.Width,
            Height = maps.Height,
            BakeAtlasWidth = maps.BakeAtlasWidth,
            BakeAtlasHeight = maps.BakeAtlasHeight,
            DiffuseRgba = tinted,
            NormalRgba = maps.NormalRgba,
            SpecularRgba = maps.SpecularRgba,
            HeightRgba = maps.HeightRgba,
            IsPlantForNoHeight = maps.IsPlantForNoHeight,
            Sprite2DFoliageTarget = maps.Sprite2DFoliageTarget,
            IsItemTexturePath = maps.IsItemTexturePath,
        };
    }
}
