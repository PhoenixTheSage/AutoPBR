using AutoPBR.Core.Models;
using AutoPBR.Preview;

using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.Preview.Tests;

public sealed class PreviewFoliageColormapTintTests
{
    [Fact]
    public void IsFoliageColormapTintIndexPath_matches_leaves_only()
    {
        Assert.True(PreviewFoliageColormapTint.IsFoliageColormapTintIndexPath(
            "assets/minecraft/textures/block/oak_leaves.png"));
        Assert.True(PreviewFoliageColormapTint.IsFoliageColormapTintIndexPath(
            "assets/minecraft/textures/block/birch_leaves.png"));
        Assert.False(PreviewFoliageColormapTint.IsFoliageColormapTintIndexPath(
            "assets/minecraft/textures/block/oak_log.png"));
        Assert.False(PreviewFoliageColormapTint.IsFoliageColormapTintIndexPath(
            "assets/minecraft/textures/block/cactus_side.png"));
        Assert.False(PreviewFoliageColormapTint.IsFoliageColormapTintIndexPath(
            "assets/minecraft/textures/colormap/foliage.png"));
    }

    [Fact]
    public void WithFoliageTint_tints_grayscale_leaves()
    {
        var maps = new PreviewTextureMaps
        {
            Width = 1,
            Height = 1,
            DiffuseRgba = [128, 128, 128, 200],
        };
        var tinted = PreviewFoliageColormapTint.WithFoliageTint(
            maps,
            "assets/minecraft/textures/block/oak_leaves.png",
            new Rgba32(100, 200, 50, 255));
        Assert.Equal((byte)50, tinted.DiffuseRgba[0]);
        Assert.Equal((byte)100, tinted.DiffuseRgba[1]);
        Assert.Equal((byte)25, tinted.DiffuseRgba[2]);
        Assert.Equal((byte)200, tinted.DiffuseRgba[3]);
    }

    [Fact]
    public void WithFoliageTint_preserves_precolored_leaves()
    {
        var maps = new PreviewTextureMaps
        {
            Width = 1,
            Height = 1,
            DiffuseRgba = [220, 120, 160, 255],
        };
        var tinted = PreviewFoliageColormapTint.WithFoliageTint(
            maps,
            "assets/minecraft/textures/block/cherry_leaves.png",
            new Rgba32(100, 200, 50, 255));
        Assert.Equal((byte)220, tinted.DiffuseRgba[0]);
        Assert.Equal((byte)120, tinted.DiffuseRgba[1]);
        Assert.Equal((byte)160, tinted.DiffuseRgba[2]);
    }

    [Fact]
    public void FoliageColormapArchivePath_is_vanilla_foliage_png()
    {
        Assert.Equal(
            "assets/minecraft/textures/colormap/foliage.png",
            PreviewFoliageColormapTint.FoliageColormapArchivePath);
    }
}
