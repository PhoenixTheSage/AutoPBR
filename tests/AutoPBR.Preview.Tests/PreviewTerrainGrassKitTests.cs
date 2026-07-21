using System.IO.Compression;
using System.Text;

using AutoPBR.Core.Models;
using AutoPBR.Preview;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.Preview.Tests;

public sealed class PreviewTerrainGrassKitTests
{
    [Fact]
    public void HasValidGrassSet_requires_top_side_dirt()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PreviewGroundMapsResolver.GrassBlockTopArchivePath,
            PreviewTerrainGrassKitResolver.GrassBlockSideArchivePath,
            PreviewTerrainGrassKitResolver.DirtArchivePath,
        };
        Assert.True(PreviewTerrainGrassKitResolver.HasValidGrassSet(present.Contains));

        present.Remove(PreviewTerrainGrassKitResolver.GrassBlockSideArchivePath);
        Assert.False(PreviewTerrainGrassKitResolver.HasValidGrassSet(present.Contains));
    }

    [Fact]
    public void BetterGrassProperties_parse_grass_false_and_texture_overrides()
    {
        var props = PreviewTerrainBetterGrassProperties.Parse("""
            grass=false
            grass.multilayer=true
            texture.grass=block/emerald_block
            texture.grass_side=block/redstone_block
            """);

        Assert.False(props.GrassEnabled);
        Assert.True(props.Multilayer);
        Assert.Equal("block/emerald_block", props.TextureGrass);
        Assert.Equal("block/redstone_block", props.TextureGrassSide);
    }

    [Fact]
    public void ModelTextureToBlockZipPath_maps_block_notation()
    {
        Assert.Equal(
            "assets/minecraft/textures/block/grass_block_top.png",
            PreviewTerrainBetterGrassProperties.ModelTextureToBlockZipPath("block/grass_block_top"));
        Assert.Equal(
            "assets/minecraft/textures/block/emerald_block.png",
            PreviewTerrainBetterGrassProperties.ModelTextureToBlockZipPath("minecraft:block/emerald_block"));
    }

    [Fact]
    public async Task TryResolveAsync_missing_side_falls_back_to_BuiltIn()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "autopbr_terrain_grass_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await CreatePackZipAsync(zipPath, includeSide: false, includeDirt: true, includeOverlay: false);

            var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json");
            var options = new AutoPBROptions
            {
                SpecularData = SpecularData.LoadFromFile(dataPath),
                FastSpecular = true,
                FoliageMode = "No Height",
            };

            var kit = await PreviewTerrainGrassKitResolver.TryResolveAsync(
                zipPath,
                preferScannedPack: true,
                minecraftAssetsDirectory: null,
                options);

            Assert.Equal(PreviewTerrainGrassMode.BuiltInSingleTop, kit.Mode);
            Assert.NotNull(kit.Top);
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }

    [Fact]
    public async Task TryResolveAsync_complete_pack_uses_BlockModelFaces()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "autopbr_terrain_grass_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await CreatePackZipAsync(zipPath, includeSide: true, includeDirt: true, includeOverlay: true, betterGrass: "grass=true\n");

            var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json");
            var options = new AutoPBROptions
            {
                SpecularData = SpecularData.LoadFromFile(dataPath),
                FastSpecular = true,
                FoliageMode = "No Height",
            };

            var kit = await PreviewTerrainGrassKitResolver.TryResolveAsync(
                zipPath,
                preferScannedPack: true,
                minecraftAssetsDirectory: null,
                options);

            Assert.Equal(PreviewTerrainGrassMode.BlockModelFaces, kit.Mode);
            Assert.NotNull(kit.Top);
            Assert.NotNull(kit.Side);
            Assert.NotNull(kit.Dirt);
            Assert.NotNull(kit.Overlay);
            Assert.True(kit.BetterGrassEnabled);
            Assert.True(kit.EmitOverlay);
            Assert.True(kit.StoneAliased);
            Assert.True(kit.SandAliased);
            Assert.True(kit.GravelAliased);
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }

    [Fact]
    public async Task TryResolveAsync_missing_stone_is_aliased_but_present_stone_resolves()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "autopbr_terrain_grass_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await CreatePackZipAsync(
                zipPath,
                includeSide: true,
                includeDirt: true,
                includeOverlay: false,
                includeStone: true,
                includeSand: false);

            var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json");
            var options = new AutoPBROptions
            {
                SpecularData = SpecularData.LoadFromFile(dataPath),
                FastSpecular = true,
                FoliageMode = "No Height",
            };

            var kit = await PreviewTerrainGrassKitResolver.TryResolveAsync(
                zipPath,
                preferScannedPack: true,
                minecraftAssetsDirectory: null,
                options);

            Assert.Equal(PreviewTerrainGrassMode.BlockModelFaces, kit.Mode);
            Assert.False(kit.StoneAliased);
            Assert.NotNull(kit.Stone);
            Assert.True(kit.SandAliased);
            Assert.Null(kit.Sand);
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }

    [Fact]
    public async Task TryResolveAsync_grass_false_disables_BetterGrass()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "autopbr_terrain_grass_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await CreatePackZipAsync(
                zipPath,
                includeSide: true,
                includeDirt: true,
                includeOverlay: false,
                betterGrass: "grass=false\n");

            var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json");
            var options = new AutoPBROptions
            {
                SpecularData = SpecularData.LoadFromFile(dataPath),
                FastSpecular = true,
                FoliageMode = "No Height",
            };

            var kit = await PreviewTerrainGrassKitResolver.TryResolveAsync(
                zipPath,
                preferScannedPack: true,
                minecraftAssetsDirectory: null,
                options);

            Assert.Equal(PreviewTerrainGrassMode.BlockModelFaces, kit.Mode);
            Assert.False(kit.BetterGrassEnabled);
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }

    private static async Task CreatePackZipAsync(
        string zipPath,
        bool includeSide,
        bool includeDirt,
        bool includeOverlay,
        string? betterGrass = null,
        bool includeStone = false,
        bool includeSand = false)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        await using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        await AddPngEntryAsync(zip, PreviewGroundMapsResolver.GrassBlockTopArchivePath, 40, 120, 40);
        if (includeSide)
        {
            await AddPngEntryAsync(zip, PreviewTerrainGrassKitResolver.GrassBlockSideArchivePath, 90, 70, 40);
        }

        if (includeDirt)
        {
            await AddPngEntryAsync(zip, PreviewTerrainGrassKitResolver.DirtArchivePath, 110, 80, 50);
        }

        if (includeOverlay)
        {
            await AddPngEntryAsync(zip, PreviewTerrainGrassKitResolver.GrassBlockSideOverlayArchivePath, 30, 160, 30, alpha: 180);
        }

        if (includeStone)
        {
            await AddPngEntryAsync(zip, PreviewTerrainGrassKitResolver.StoneArchivePath, 120, 120, 120);
        }

        if (includeSand)
        {
            await AddPngEntryAsync(zip, PreviewTerrainGrassKitResolver.SandArchivePath, 210, 190, 140);
        }

        if (betterGrass is not null)
        {
            var entry = zip.CreateEntry(PreviewTerrainBetterGrassProperties.ArchivePath);
            await using var s = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(betterGrass);
            await s.WriteAsync(bytes);
        }
    }

    private static async Task AddPngEntryAsync(
        ZipArchive zip,
        string path,
        byte r,
        byte g,
        byte b,
        byte alpha = 255)
    {
        var entry = zip.CreateEntry(path);
        await using var s = entry.Open();
        using var image = new Image<Rgba32>(16, 16);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32(r, g, b, alpha);
                }
            }
        });
        await image.SaveAsPngAsync(s);
    }
}
