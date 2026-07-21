using AutoPBR.App.Rendering.Scene;
using AutoPBR.Preview;

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
        Assert.NotEmpty(mesh!.DrawBatches);
        Assert.True(mesh.DrawBatches.All(b => b.MaterialIndex is >= 0 and < PreviewTerrainGrassSlots.MaxCount));
    }
}
