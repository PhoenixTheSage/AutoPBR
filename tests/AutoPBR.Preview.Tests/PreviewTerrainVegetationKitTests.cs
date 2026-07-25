using System.IO.Compression;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoPBR.Preview.Tests;

public sealed class PreviewTerrainVegetationKitTests
{
    [Fact]
    public void HasMatchingWoodPair_requires_log_and_leaves()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PreviewTerrainVegetationKitResolver.LogArchivePath("oak"),
            PreviewTerrainVegetationKitResolver.LeavesArchivePath("oak"),
        };
        Assert.True(PreviewTerrainVegetationKitResolver.HasMatchingWoodPair(present.Contains, "oak"));

        present.Remove(PreviewTerrainVegetationKitResolver.LeavesArchivePath("oak"));
        Assert.False(PreviewTerrainVegetationKitResolver.HasMatchingWoodPair(present.Contains, "oak"));
    }

    [Fact]
    public void HasCactusPair_requires_side_and_top()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PreviewTerrainVegetationKitResolver.CactusSideArchivePath,
            PreviewTerrainVegetationKitResolver.CactusTopArchivePath,
        };
        Assert.True(PreviewTerrainVegetationKitResolver.HasCactusPair(present.Contains));
        present.Remove(PreviewTerrainVegetationKitResolver.CactusTopArchivePath);
        Assert.False(PreviewTerrainVegetationKitResolver.HasCactusPair(present.Contains));
    }

    [Fact]
    public void SpeciesIds_round_trip_texture_stems()
    {
        foreach (var stem in PreviewTerrainTreeSpeciesIds.WoodTextureStems)
        {
            Assert.True(PreviewTerrainTreeSpeciesIds.TryParseTextureStem(stem, out var species));
            Assert.Equal(stem, PreviewTerrainTreeSpeciesIds.ToTextureStem(species));
        }

        Assert.True(PreviewTerrainTreeSpeciesIds.TryParseTextureStem("cactus", out var cactus));
        Assert.Equal(PreviewTerrainTreeSpecies.Cactus, cactus);
    }

    [Fact]
    public void SpeciesRules_desert_and_beach_skip_wood()
    {
        Assert.False(
            PreviewTerrainTreeSpeciesRules.TryPickWoodSpecies(
                biomeId: 1,
                temperature: 0.9f,
                humidity: 0.1f,
                out _));
        Assert.False(
            PreviewTerrainTreeSpeciesRules.TryPickWoodSpecies(
                biomeId: 2,
                temperature: 0.5f,
                humidity: 0.5f,
                out _));
    }

    [Fact]
    public void SpeciesRules_climate_picks_expected_woods()
    {
        Assert.True(
            PreviewTerrainTreeSpeciesRules.TryPickWoodSpecies(0, 0.8f, 0.7f, out var jungle));
        Assert.Equal(PreviewTerrainTreeSpecies.Jungle, jungle);

        Assert.True(
            PreviewTerrainTreeSpeciesRules.TryPickWoodSpecies(0, 0.7f, 0.2f, out var acacia));
        Assert.Equal(PreviewTerrainTreeSpecies.Acacia, acacia);

        Assert.True(
            PreviewTerrainTreeSpeciesRules.TryPickWoodSpecies(3, 0.3f, 0.5f, out var spruce));
        Assert.Equal(PreviewTerrainTreeSpecies.Spruce, spruce);

        Assert.True(
            PreviewTerrainTreeSpeciesRules.TryPickWoodSpecies(3, 0.5f, 0.5f, out var mountainSpruce));
        Assert.Equal(PreviewTerrainTreeSpecies.Spruce, mountainSpruce);

        Assert.True(
            PreviewTerrainTreeSpeciesRules.TryPickWoodSpecies(0, 0.45f, 0.60f, out var birch));
        Assert.Equal(PreviewTerrainTreeSpecies.Birch, birch);

        Assert.True(
            PreviewTerrainTreeSpeciesRules.TryPickWoodSpecies(0, 0.5f, 0.5f, out var oak));
        Assert.Equal(PreviewTerrainTreeSpecies.Oak, oak);
    }

    [Fact]
    public void FallbackChain_ends_with_oak()
    {
        var chain = PreviewTerrainTreeSpeciesRules.FallbackChain(PreviewTerrainTreeSpecies.Cherry);
        Assert.Equal(PreviewTerrainTreeSpecies.Oak, chain[^1]);
        Assert.Contains(PreviewTerrainTreeSpecies.Cherry, chain);
    }

    [Fact]
    public async Task TryResolveAsync_without_sources_returns_empty()
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json");
        var options = new AutoPBROptions
        {
            SpecularData = SpecularData.LoadFromFile(dataPath),
            FastSpecular = true,
            FoliageMode = "No Height",
        };

        var kit = await PreviewTerrainVegetationKitResolver.TryResolveAsync(
            scannedPackDiskPath: null,
            preferScannedPack: false,
            minecraftAssetsDirectory: null,
            options);

        Assert.False(kit.HasAny);
        Assert.Equal(PreviewTerrainGrassSlots.MaxCount, kit.TotalSlotCount);
    }

    [Fact]
    public async Task TryResolveAsync_pack_with_oak_pair_discovers_species()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "autopbr_veg_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var fs = File.Create(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                await AddSolidPngAsync(zip, PreviewTerrainVegetationKitResolver.LogArchivePath("oak"), 90, 60, 30);
                await AddSolidPngAsync(zip, PreviewTerrainVegetationKitResolver.LeavesArchivePath("oak"), 40, 140, 40, alpha: 200);
                await AddSolidPngAsync(zip, PreviewTerrainVegetationKitResolver.CactusSideArchivePath, 20, 120, 20);
                await AddSolidPngAsync(zip, PreviewTerrainVegetationKitResolver.CactusTopArchivePath, 30, 140, 30);
            }

            var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json");
            var options = new AutoPBROptions
            {
                SpecularData = SpecularData.LoadFromFile(dataPath),
                FastSpecular = true,
                FoliageMode = "No Height",
            };

            var kit = await PreviewTerrainVegetationKitResolver.TryResolveAsync(
                zipPath,
                preferScannedPack: true,
                minecraftAssetsDirectory: null,
                options);

            Assert.True(kit.HasAny);
            Assert.True(kit.TryGet(PreviewTerrainTreeSpecies.Oak, out var oak));
            Assert.True(kit.CutoutBySlot[oak.LeavesOrTopSlot]);
            Assert.False(kit.CutoutBySlot[oak.LogSlot]);
            Assert.True(kit.TryGet(PreviewTerrainTreeSpecies.Cactus, out var cactus));
            Assert.True(kit.CutoutBySlot[cactus.LogSlot], "cactus_side should be cutout");
            Assert.False(kit.CutoutBySlot[cactus.LeavesOrTopSlot], "cactus_top stays opaque");
            Assert.Contains("oak", kit.Identity, StringComparison.Ordinal);
            Assert.Contains("cactus", kit.Identity, StringComparison.Ordinal);
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
    public async Task TryResolveAsync_partial_pack_leaves_override_install_logs()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "autopbr_veg_partial_" + Guid.NewGuid().ToString("N") + ".zip");
        var installRoot = Path.Combine(Path.GetTempPath(), "autopbr_mc_assets_" + Guid.NewGuid().ToString("N"));
        try
        {
            // Pack overhauls leaves only (no logs).
            const byte packLeafR = 220;
            const byte packLeafG = 40;
            const byte packLeafB = 200;
            await using (var fs = File.Create(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                await AddSolidPngAsync(
                    zip,
                    PreviewTerrainVegetationKitResolver.LeavesArchivePath("oak"),
                    packLeafR,
                    packLeafG,
                    packLeafB,
                    alpha: 200);
            }

            // Install supplies a complete vanilla pair with different leaf color.
            var assets = Path.Combine(installRoot, "assets");
            Directory.CreateDirectory(Path.Combine(assets, "minecraft", "models", "block"));
            var texDir = Path.Combine(assets, "minecraft", "textures", "block");
            Directory.CreateDirectory(texDir);
            await File.WriteAllTextAsync(
                Path.Combine(assets, "minecraft", "models", "block", "stone.json"),
                "{}");
            await WriteSolidPngFileAsync(
                Path.Combine(texDir, "oak_log.png"),
                90,
                60,
                30);
            await WriteSolidPngFileAsync(
                Path.Combine(texDir, "oak_leaves.png"),
                40,
                140,
                40,
                alpha: 200);

            var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "textures_data.json");
            var options = new AutoPBROptions
            {
                SpecularData = SpecularData.LoadFromFile(dataPath),
                FastSpecular = true,
                FoliageMode = "No Height",
            };

            var kit = await PreviewTerrainVegetationKitResolver.TryResolveAsync(
                zipPath,
                preferScannedPack: true,
                minecraftAssetsDirectory: installRoot,
                options);

            Assert.True(kit.HasAny);
            Assert.True(kit.TryGet(PreviewTerrainTreeSpecies.Oak, out var oak));
            Assert.NotNull(oak.LeavesOrTopMaps.DiffuseRgba);
            Assert.True(oak.LeavesOrTopMaps.DiffuseRgba.Length >= 4);
            // Pack leaf color must win over install green leaves.
            Assert.Equal(packLeafR, oak.LeavesOrTopMaps.DiffuseRgba[0]);
            Assert.Equal(packLeafG, oak.LeavesOrTopMaps.DiffuseRgba[1]);
            Assert.Equal(packLeafB, oak.LeavesOrTopMaps.DiffuseRgba[2]);
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            if (Directory.Exists(installRoot))
            {
                Directory.Delete(installRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildResolveSource_pack_overrides_install_order()
    {
        var pack = new RecordingAssetSource("pack");
        var install = new RecordingAssetSource("install");
        var composite = PreviewTerrainVegetationKitResolver.BuildResolveSource(pack, install);
        Assert.NotNull(composite);
        Assert.True(composite.TryReadBytes("assets/minecraft/textures/block/oak_leaves.png", out var bytes));
        Assert.Equal("pack"u8.ToArray(), bytes);
        Assert.Same(pack, PreviewTerrainVegetationKitResolver.BuildResolveSource(pack, null));
        Assert.Same(install, PreviewTerrainVegetationKitResolver.BuildResolveSource(null, install));
        Assert.Null(PreviewTerrainVegetationKitResolver.BuildResolveSource(null, null));
    }

    private sealed class RecordingAssetSource(string tag) : IAssetSource
    {
        public bool Exists(string assetPath) => true;

        public bool TryReadBytes(string assetPath, out byte[] bytes)
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(tag);
            return true;
        }

        public bool TryReadText(string assetPath, out string text)
        {
            text = tag;
            return true;
        }
    }

    private static async Task AddSolidPngAsync(
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
        FillSolid(image, r, g, b, alpha);
        await image.SaveAsPngAsync(s);
    }

    private static async Task WriteSolidPngFileAsync(
        string path,
        byte r,
        byte g,
        byte b,
        byte alpha = 255)
    {
        using var image = new Image<Rgba32>(16, 16);
        FillSolid(image, r, g, b, alpha);
        await image.SaveAsPngAsync(path);
    }

    private static void FillSolid(Image<Rgba32> image, byte r, byte g, byte b, byte alpha)
    {
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
    }

    [Fact]
    public void BakePlan_TryResolveWood_falls_back()
    {
        var oak = new PreviewTerrainVegetationBakeEntry(
            PreviewTerrainTreeSpecies.Oak,
            LogSlot: 7,
            LeavesOrTopSlot: 8,
            LogTopSlot: null);
        var plan = new PreviewTerrainVegetationBakePlan("veg|oak", [oak], 9);

        Assert.True(plan.TryResolveWood(PreviewTerrainTreeSpecies.Cherry, out var resolved));
        Assert.Equal(PreviewTerrainTreeSpecies.Oak, resolved.Species);
        Assert.False(plan.TryGet(PreviewTerrainTreeSpecies.Cactus, out _));
    }
}
