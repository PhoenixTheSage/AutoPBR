using System.Numerics;

using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewTerrainBiomeTests
{
    [Fact]
    public void Sample_pad_is_plains_height_zero()
    {
        var sample = PreviewTerrainBiomeSampler.Sample(0, 0);
        Assert.Equal(0, sample.Height);
        Assert.Equal(PreviewTerrainBiomeId.Plains, sample.Biome);
        Assert.Equal(PreviewTerrainBlockKind.Grass, sample.Surface);
    }

    [Fact]
    public void Sample_is_deterministic()
    {
        var a = PreviewTerrainBiomeSampler.Sample(40, -30);
        var b = PreviewTerrainBiomeSampler.Sample(40, -30);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ClassifyBiome_thresholds()
    {
        Assert.Equal(
            PreviewTerrainBiomeId.Mountains,
            PreviewTerrainBiomeSampler.ClassifyBiome(temperature: 0.3f, humidity: 0.5f, continental: 0.8f));
        Assert.Equal(
            PreviewTerrainBiomeId.Desert,
            PreviewTerrainBiomeSampler.ClassifyBiome(temperature: 0.8f, humidity: 0.2f, continental: 0.5f));
        Assert.Equal(
            PreviewTerrainBiomeId.Beach,
            PreviewTerrainBiomeSampler.ClassifyBiome(temperature: 0.5f, humidity: 0.5f, continental: 0.2f));
        Assert.Equal(
            PreviewTerrainBiomeId.Plains,
            PreviewTerrainBiomeSampler.ClassifyBiome(temperature: 0.5f, humidity: 0.5f, continental: 0.5f));
    }

    [Fact]
    public void ComputeBiomeWeights_sum_to_one_and_blend_near_threshold()
    {
        PreviewTerrainBiomeSampler.ComputeBiomeWeights(
            temperature: 0.55f,
            humidity: 0.5f,
            continental: 0.55f,
            PreviewStageConstants.TerrainBiomeBlendHalfWidth,
            out var m,
            out var d,
            out var b,
            out var p);
        Assert.InRange(m + d + b + p, 0.999f, 1.001f);
        Assert.True(m is > 0.05f and < 0.95f, $"expected soft mountain weight near threshold, got {m}");
        Assert.True(p > 0.05f, $"expected plains to share the border, got {p}");
    }

    [Fact]
    public void Biome_border_geometry_includes_soft_height_steps()
    {
        // Soft climate weights let neighboring columns change biome while height stays continuous.
        var borderPairs = 0;
        var softPairs = 0;
        for (var z = -120; z <= 120; z++)
        {
            for (var x = -120; x <= 120; x++)
            {
                var a = PreviewTerrainBiomeSampler.Sample(x, z);
                var b = PreviewTerrainBiomeSampler.Sample(x + 1, z);
                if (a.Biome == b.Biome)
                {
                    continue;
                }

                borderPairs++;
                if (Math.Abs(a.Height - b.Height) <= 1)
                {
                    softPairs++;
                }
            }
        }

        Assert.True(borderPairs > 0, "expected at least one biome border within search radius");
        Assert.True(
            softPairs > 0,
            $"expected soft |Δh|≤1 across some biome borders (soft={softPairs}/{borderPairs})");
    }

    [Fact]
    public void WorldGen_seed_changes_height_outside_pad_but_pad_stays_zero()
    {
        var a = PreviewTerrainWorldGenSettings.Default with { Seed = 11 };
        var b = PreviewTerrainWorldGenSettings.Default with { Seed = 99 };
        Assert.Equal(0, PreviewTerrainBiomeSampler.Sample(0, 0, a).Height);
        Assert.Equal(0, PreviewTerrainBiomeSampler.Sample(0, 0, b).Height);

        var foundDiff = false;
        for (var z = 20; z <= 80 && !foundDiff; z++)
        {
            for (var x = 20; x <= 80; x++)
            {
                if (PreviewTerrainBiomeSampler.Sample(x, z, a).Height !=
                    PreviewTerrainBiomeSampler.Sample(x, z, b).Height)
                {
                    foundDiff = true;
                    break;
                }
            }
        }

        Assert.True(foundDiff, "expected seed change to alter terrain outside the flat pad");
    }

    [Fact]
    public void WorldGen_amplification_scales_absolute_height()
    {
        var baseGen = PreviewTerrainWorldGenSettings.Default;
        var tallGen = baseGen with { Amplification = 2f };
        var maxBase = 0;
        var maxTall = 0;
        for (var z = -100; z <= 100; z++)
        {
            for (var x = -100; x <= 100; x++)
            {
                maxBase = Math.Max(maxBase, Math.Abs(PreviewTerrainBiomeSampler.Sample(x, z, baseGen).Height));
                maxTall = Math.Max(maxTall, Math.Abs(PreviewTerrainBiomeSampler.Sample(x, z, tallGen).Height));
            }
        }

        Assert.True(maxTall > maxBase, $"expected amp=2 to raise peak relief (base={maxBase}, tall={maxTall})");
    }

    [Fact]
    public void AdvancedErosion_is_deterministic_and_finite()
    {
        var a = PreviewTerrainAdvancedErosion.SampleErodedMountainNormalized(12.5f, -7.25f, seed: 42);
        var b = PreviewTerrainAdvancedErosion.SampleErodedMountainNormalized(12.5f, -7.25f, seed: 42);
        Assert.Equal(a, b);
        Assert.True(float.IsFinite(a));
        Assert.InRange(a, -1f, 1f);

        var baseH = PreviewTerrainAdvancedErosion.Fbm(
            new Vector2(0.42f, 0.31f),
            frequency: 2.4f,
            octaves: 3,
            lacunarity: 2f,
            gain: 0.18f);
        var filtered = PreviewTerrainAdvancedErosion.ErosionFilter(
            new Vector2(0.42f, 0.31f),
            baseH,
            fadeTargetIn: 0f,
            PreviewTerrainAdvancedErosion.MountainParams);
        Assert.True(float.IsFinite(filtered.Delta.X));
        Assert.True(float.IsFinite(filtered.Magnitude));
        Assert.True(filtered.Magnitude > 0f);
    }

    [Fact]
    public void Mountains_can_produce_multi_block_neighbor_steps()
    {
        var foundMountain = false;
        var foundCliff = false;
        for (var z = -160; z <= 160 && !foundCliff; z++)
        {
            for (var x = -160; x <= 160; x++)
            {
                var a = PreviewTerrainBiomeSampler.Sample(x, z);
                if (a.Biome != PreviewTerrainBiomeId.Mountains)
                {
                    continue;
                }

                foundMountain = true;
                var b = PreviewTerrainBiomeSampler.Sample(x + 1, z);
                if (Math.Abs(a.Height - b.Height) >= PreviewStageConstants.TerrainCliffDeltaBlocks)
                {
                    foundCliff = true;
                    break;
                }
            }
        }

        Assert.True(foundMountain, "Expected mountain biome columns within search radius");
        Assert.True(foundCliff, "Expected at least one mountain cliff step with |Δh| >= TerrainCliffDeltaBlocks");
    }

    [Fact]
    public void ResolveHorizontalFaceMaterial_mountain_cliff_uses_stone()
    {
        PreviewTerrainColumnSample ColumnAt(int x, int z) =>
            x == 0
                ? new PreviewTerrainColumnSample(
                    10,
                    PreviewTerrainBiomeId.Mountains,
                    PreviewTerrainBlockKind.Grass,
                    PreviewTerrainBlockKind.Dirt,
                    PreviewTerrainBlockKind.Stone)
                : new PreviewTerrainColumnSample(
                    7,
                    PreviewTerrainBiomeId.Mountains,
                    PreviewTerrainBlockKind.Grass,
                    PreviewTerrainBlockKind.Dirt,
                    PreviewTerrainBlockKind.Stone);

        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: true,
            EmitOverlay: true,
            HasStone: true,
            HasSand: true,
            HasGravel: true);

        var mat = PreviewTerrainMeshBaker.ResolveHorizontalFaceMaterial(
            ColumnAt, bx: 0, by: 10, bz: 0, neighborX: 1, neighborZ: 0, settings);
        Assert.Equal(PreviewTerrainGrassSlots.Stone, mat);
    }

    [Fact]
    public void ResolveHorizontalFaceMaterial_desert_surface_uses_sand()
    {
        PreviewTerrainColumnSample ColumnAt(int x, int z) =>
            new(
                3,
                PreviewTerrainBiomeId.Desert,
                PreviewTerrainBlockKind.Sand,
                PreviewTerrainBlockKind.Sand,
                PreviewTerrainBlockKind.Stone);

        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: false,
            EmitOverlay: false);

        var mat = PreviewTerrainMeshBaker.ResolveHorizontalFaceMaterial(
            ColumnAt, bx: 0, by: 3, bz: 0, neighborX: 1, neighborZ: 0, settings);
        Assert.Equal(PreviewTerrainGrassSlots.Sand, mat);
    }

    [Fact]
    public void ResolveHorizontalFaceMaterial_plains_BetterGrass_still_uses_Top()
    {
        int HeightAt(int x, int z) => z == 0 ? 2 : 0;
        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: true,
            EmitOverlay: true);

        var mat = PreviewTerrainMeshBaker.ResolveHorizontalFaceMaterial(
            HeightAt, bx: 0, by: 2, bz: 0, neighborX: 0, neighborZ: 1, settings);
        Assert.Equal(PreviewTerrainGrassSlots.Top, mat);
    }

    [Fact]
    public void ResolveYFaceMaterial_desert_top_is_sand()
    {
        var col = new PreviewTerrainColumnSample(
            2,
            PreviewTerrainBiomeId.Desert,
            PreviewTerrainBlockKind.Sand,
            PreviewTerrainBlockKind.Sand,
            PreviewTerrainBlockKind.Stone);
        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: false,
            EmitOverlay: false);
        Assert.Equal(
            PreviewTerrainGrassSlots.Sand,
            PreviewTerrainMeshBaker.ResolveYFaceMaterial(positiveUp: true, col, settings));
    }

    [Fact]
    public void BakeFullChunk_emits_biome_material_slots()
    {
        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: true,
            EmitOverlay: true,
            HasStone: true,
            HasSand: true,
            HasGravel: true);
        var mesh = PreviewTerrainMeshBaker.BakeFullChunk(new TerrainChunkKey(3, -2), settings);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh.DrawBatches);
        Assert.True(mesh.DrawBatches.All(b => b.MaterialIndex is >= 0 and < PreviewTerrainGrassSlots.MaxCount));
    }

    [Fact]
    public void BakeLodChunk_matches_full_surface_material_on_desert_column()
    {
        // Find a desert column outside the flat pad and assert LOD top uses Sand like Full.
        PreviewTerrainColumnSample? desert = null;
        var desertX = 0;
        var desertZ = 0;
        for (var z = 24; z < 96 && desert is null; z++)
        {
            for (var x = 24; x < 96; x++)
            {
                var sample = PreviewTerrainBiomeSampler.Sample(x, z);
                if (sample.Biome == PreviewTerrainBiomeId.Desert &&
                    sample.Surface == PreviewTerrainBlockKind.Sand)
                {
                    desert = sample;
                    desertX = x;
                    desertZ = z;
                    break;
                }
            }
        }

        Assert.NotNull(desert);
        var chunkSize = PreviewStageConstants.TerrainChunkSize;
        var key = new TerrainChunkKey(
            (int)Math.Floor(desertX / (double)chunkSize),
            (int)Math.Floor(desertZ / (double)chunkSize));
        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: false,
            EmitOverlay: false,
            HasSand: true);
        var lod = PreviewTerrainLodMeshBaker.BakeLodChunk(key, grassSettings: settings);
        Assert.NotNull(lod);
        Assert.Contains(lod.DrawBatches, b => b.MaterialIndex == PreviewTerrainGrassSlots.Sand);
    }
}
