using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewTerrainTreePlacementTests
{
    [Fact]
    public void CollectForChunk_skips_flat_pad()
    {
        var plan = MakeOakPlan();
        PreviewTerrainColumnSample ColumnAt(int x, int z) =>
            new(0, PreviewTerrainBiomeId.Plains, PreviewTerrainBlockKind.Grass,
                PreviewTerrainBlockKind.Dirt, PreviewTerrainBlockKind.Stone);

        var placements = PreviewTerrainTreePlacer.CollectForChunk(
            cx0: -8,
            cz0: -8,
            cx1: 8,
            cz1: 8,
            ColumnAt,
            PreviewTerrainWorldGenSettings.Default,
            plan,
            flatPadHalfExtent: 14);

        Assert.Empty(placements);
    }

    [Fact]
    public void CollectForChunk_desert_uses_cactus_when_available()
    {
        var cactus = new PreviewTerrainVegetationBakeEntry(
            PreviewTerrainTreeSpecies.Cactus,
            LogSlot: 7,
            LeavesOrTopSlot: 8,
            LogTopSlot: null);
        var plan = new PreviewTerrainVegetationBakePlan("veg|cactus", [cactus], 9);

        PreviewTerrainColumnSample ColumnAt(int x, int z) =>
            new(1, PreviewTerrainBiomeId.Desert, PreviewTerrainBlockKind.Sand,
                PreviewTerrainBlockKind.Sand, PreviewTerrainBlockKind.Stone);

        var found = false;
        for (var seed = 1; seed < 200 && !found; seed++)
        {
            var placements = PreviewTerrainTreePlacer.CollectForChunk(
                cx0: 40,
                cz0: 40,
                cx1: 56,
                cz1: 56,
                ColumnAt,
                PreviewTerrainWorldGenSettings.Default with { Seed = seed },
                plan,
                flatPadHalfExtent: 14);
            if (placements.Count > 0)
            {
                Assert.All(placements, p => Assert.Equal(PreviewTerrainTreeSpecies.Cactus, p.Species));
                found = true;
            }
        }

        Assert.True(found, "expected at least one cactus placement across seed trials");
    }

    [Fact]
    public void CollectForChunk_is_deterministic()
    {
        var plan = MakeOakAndCactusPlan();
        PreviewTerrainColumnSample ColumnAt(int x, int z)
        {
            // Force plains grass far from pad.
            return new(2, PreviewTerrainBiomeId.Plains, PreviewTerrainBlockKind.Grass,
                PreviewTerrainBlockKind.Dirt, PreviewTerrainBlockKind.Stone);
        }

        var gen = PreviewTerrainWorldGenSettings.Default with { Seed = 4242 };
        var a = PreviewTerrainTreePlacer.CollectForChunk(48, -64, 64, -48, ColumnAt, gen, plan);
        var b = PreviewTerrainTreePlacer.CollectForChunk(48, -64, 64, -48, ColumnAt, gen, plan);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i], b[i]);
        }
    }

    [Fact]
    public void CollectForChunk_empty_plan_yields_nothing()
    {
        PreviewTerrainColumnSample ColumnAt(int x, int z) =>
            new(2, PreviewTerrainBiomeId.Plains, PreviewTerrainBlockKind.Grass,
                PreviewTerrainBlockKind.Dirt, PreviewTerrainBlockKind.Stone);

        var placements = PreviewTerrainTreePlacer.CollectForChunk(
            48, -64, 64, -48, ColumnAt, PreviewTerrainWorldGenSettings.Default,
            PreviewTerrainVegetationBakePlan.Empty);
        Assert.Empty(placements);
    }

    [Fact]
    public void BakeFullChunk_with_vegetation_emits_leaf_or_log_batches()
    {
        var plan = MakeOakPlan();
        var grass = PreviewTerrainGrassBakeSettings.BuiltIn with
        {
            VegetationIdentity = plan.Identity,
        };

        // Force a known plains-friendly seed and chunk far from the pad.
        PreviewTerrainChunkMesh? meshWithTrees = null;
        for (var seed = 1; seed < 400 && meshWithTrees is null; seed++)
        {
            var gen = PreviewTerrainWorldGenSettings.Default with { Seed = seed };
            for (var cz = 2; cz <= 8 && meshWithTrees is null; cz++)
            {
                for (var cx = 2; cx <= 8 && meshWithTrees is null; cx++)
                {
                    var mesh = PreviewTerrainMeshBaker.BakeFullChunk(
                        new TerrainChunkKey(cx, cz),
                        grass,
                        gen,
                        plan);
                    if (mesh is null)
                    {
                        continue;
                    }

                    if (mesh.DrawBatches.Any(b => b.MaterialIndex >= PreviewTerrainGrassSlots.VegetationBase))
                    {
                        meshWithTrees = mesh;
                    }
                }
            }
        }

        Assert.NotNull(meshWithTrees);
        Assert.Contains(
            meshWithTrees.DrawBatches,
            b => b.MaterialIndex is 7 or 8);
    }

    [Fact]
    public void BakeFullChunk_without_vegetation_identity_skips_trees()
    {
        var plan = MakeOakPlan();
        var grass = PreviewTerrainGrassBakeSettings.BuiltIn; // no VegetationIdentity
        var gen = PreviewTerrainWorldGenSettings.Default with { Seed = 11 };
        var mesh = PreviewTerrainMeshBaker.BakeFullChunk(
            new TerrainChunkKey(3, -2),
            grass,
            gen,
            plan);
        Assert.NotNull(mesh);
        Assert.DoesNotContain(
            mesh.DrawBatches,
            b => b.MaterialIndex >= PreviewTerrainGrassSlots.VegetationBase);
    }

    [Fact]
    public void EmitBlock_uses_distinct_side_and_y_slots()
    {
        var sideBucket = new List<float>();
        var topBucket = new List<float>();
        var buckets = new List<float>[9];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = [];
        }

        buckets[7] = sideBucket;
        buckets[8] = topBucket;

        PreviewTerrainTreeMeshEmitter.EmitBlock(
            bx: 0,
            by: 5,
            bz: 0,
            sideSlot: 7,
            ySlot: 8,
            surfaceWorldY: 0f,
            metersPerTile: 1f,
            buckets);

        // 4 side faces + 2 Y faces; each face = 4 verts * FloatsPerVertex
        var floatsPerFace = 4 * PreviewMesh.FloatsPerVertex;
        Assert.Equal(4 * floatsPerFace, sideBucket.Count);
        Assert.Equal(2 * floatsPerFace, topBucket.Count);
    }

    [Fact]
    public void EmitCactusBlock_insets_side_faces_by_one_sixteenth()
    {
        var buckets = new List<float>[9];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = [];
        }

        PreviewTerrainTreeMeshEmitter.EmitCactusBlock(
            bx: 10,
            by: 3,
            bz: 20,
            sideSlot: 7,
            topSlot: 8,
            surfaceWorldY: 0f,
            metersPerTile: 1f,
            buckets);

        const float inset = 1f / 16f;
        var side = buckets[7];
        Assert.True(side.Count > 0);
        // Interleaved: position xyz at floats 0..2 of each vertex.
        var sawInsetX = false;
        var sawInsetZ = false;
        for (var i = 0; i < side.Count; i += PreviewMesh.FloatsPerVertex)
        {
            var x = side[i];
            var z = side[i + 2];
            if (Math.Abs(x - (10f + inset)) < 1e-5f || Math.Abs(x - (11f - inset)) < 1e-5f)
            {
                sawInsetX = true;
            }

            if (Math.Abs(z - (20f + inset)) < 1e-5f || Math.Abs(z - (21f - inset)) < 1e-5f)
            {
                sawInsetZ = true;
            }
        }

        Assert.True(sawInsetX, "expected west/east faces at ±1/16 inset");
        Assert.True(sawInsetZ, "expected north/south faces at ±1/16 inset");

        // No side vertex on the outer corner ring of a flush cube (x and z both on boundary).
        for (var i = 0; i < side.Count; i += PreviewMesh.FloatsPerVertex)
        {
            var x = side[i];
            var z = side[i + 2];
            var onXBoundary = Math.Abs(x - 10f) < 1e-5f || Math.Abs(x - 11f) < 1e-5f;
            var onZBoundary = Math.Abs(z - 20f) < 1e-5f || Math.Abs(z - 21f) < 1e-5f;
            Assert.False(onXBoundary && onZBoundary);
        }
    }

    private static PreviewTerrainVegetationBakePlan MakeOakPlan()
    {
        var oak = new PreviewTerrainVegetationBakeEntry(
            PreviewTerrainTreeSpecies.Oak,
            LogSlot: 7,
            LeavesOrTopSlot: 8,
            LogTopSlot: null);
        return new PreviewTerrainVegetationBakePlan("veg|oak", [oak], 9);
    }

    private static PreviewTerrainVegetationBakePlan MakeOakAndCactusPlan()
    {
        var oak = new PreviewTerrainVegetationBakeEntry(
            PreviewTerrainTreeSpecies.Oak,
            LogSlot: 7,
            LeavesOrTopSlot: 8,
            LogTopSlot: null);
        var cactus = new PreviewTerrainVegetationBakeEntry(
            PreviewTerrainTreeSpecies.Cactus,
            LogSlot: 9,
            LeavesOrTopSlot: 10,
            LogTopSlot: null);
        return new PreviewTerrainVegetationBakePlan("veg|oak|cactus", [oak, cactus], 11);
    }
}
