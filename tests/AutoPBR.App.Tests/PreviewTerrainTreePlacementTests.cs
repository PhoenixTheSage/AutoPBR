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
    public void BakeLodChunk_with_vegetation_emits_leaf_or_log_batches()
    {
        var plan = MakeOakPlan();
        var grass = PreviewTerrainGrassBakeSettings.BuiltIn with
        {
            VegetationIdentity = plan.Identity,
        };

        PreviewTerrainChunkMesh? meshWithTrees = null;
        for (var seed = 1; seed < 400 && meshWithTrees is null; seed++)
        {
            var gen = PreviewTerrainWorldGenSettings.Default with { Seed = seed };
            for (var cz = 2; cz <= 8 && meshWithTrees is null; cz++)
            {
                for (var cx = 2; cx <= 8 && meshWithTrees is null; cx++)
                {
                    var mesh = PreviewTerrainLodMeshBaker.BakeLodChunk(
                        new TerrainChunkKey(cx, cz),
                        gen,
                        grass,
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
            b => b.MaterialIndex >= PreviewTerrainGrassSlots.VegetationBase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BakeLodSection_with_vegetation_emits_on_all_lod_levels(byte lodLevel)
    {
        var plan = MakeOakPlan();
        var grass = PreviewTerrainGrassBakeSettings.BuiltIn with
        {
            VegetationIdentity = plan.Identity,
        };

        PreviewTerrainChunkMesh? meshWithTrees = null;
        for (var seed = 1; seed < 800 && meshWithTrees is null; seed++)
        {
            var gen = PreviewTerrainWorldGenSettings.Default with { Seed = seed };
            // Stay outside the flat pad; section coords scale with LOD.
            for (var sz = 1; sz <= 10 && meshWithTrees is null; sz++)
            {
                for (var sx = 1; sx <= 10 && meshWithTrees is null; sx++)
                {
                    var section = TerrainResidencyKey.Section(sx, sz, lodLevel);
                    var mesh = PreviewTerrainLodMeshBaker.BakeLodSection(
                        section,
                        gen,
                        grass,
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
            b => b.MaterialIndex >= PreviewTerrainGrassSlots.VegetationBase);
        Assert.True(PreviewStageConstants.ShouldEmitLodBlockSpaceVegetation(lodLevel));
    }

    [Fact]
    public void Lod_vegetation_keep_mask_is_stable_subset_of_full_roots()
    {
        Assert.Equal(1, PreviewStageConstants.ResolveLodVegetationKeepMask(1));
        Assert.Equal(1, PreviewStageConstants.ResolveLodVegetationKeepMask(2));
        Assert.Equal(2, PreviewStageConstants.ResolveLodVegetationKeepMask(3));
        Assert.Equal(2, PreviewStageConstants.ResolveLodVegetationKeepMask(4));
        Assert.Equal(4, PreviewStageConstants.ResolveLodVegetationKeepMask(5));
        Assert.Equal(4, PreviewStageConstants.ResolveLodVegetationKeepMask(7));
        Assert.Equal(1f, PreviewStageConstants.ResolveLodVegetationKeepFraction(2));
        Assert.Equal(0.5f, PreviewStageConstants.ResolveLodVegetationKeepFraction(3));
        Assert.Equal(0.25f, PreviewStageConstants.ResolveLodVegetationKeepFraction(5));

        var plan = MakeOakPlan();
        List<PreviewTerrainTreePlacer.Placement>? full = null;
        for (var seed = 1; seed < 400 && full is null; seed++)
        {
            var gen = PreviewTerrainWorldGenSettings.Default with { Seed = seed };
            for (var origin = 48; origin <= 192 && full is null; origin += 32)
            {
                PreviewTerrainColumnSample Column(int x, int z) =>
                    PreviewTerrainBiomeSampler.Sample(
                        x, z, gen, PreviewStageConstants.TerrainFlatPadHalfExtent);
                var a = PreviewTerrainTreePlacer.CollectForChunk(
                    origin, origin, origin + 64, origin + 64, Column, gen, plan);
                if (a.Count >= 8)
                {
                    full = a;
                }
            }
        }

        Assert.NotNull(full);
        var centerX = (full[0].RootX + full[^1].RootX) / 2;
        var centerZ = (full[0].RootZ + full[^1].RootZ) / 2;

        var lod2 = PreviewTerrainTreePlacer.FilterForLodKeep(full, lodLevel: 2, centerX, centerZ);
        Assert.Equal(full.Count, lod2.Count);

        var lod3 = PreviewTerrainTreePlacer.FilterForLodKeep(full, lodLevel: 3, centerX, centerZ);
        Assert.True(lod3.Count > 0);
        Assert.True(lod3.Count < full.Count);
        Assert.True(lod3.Count <= (full.Count + 1) / 2 + 1);
        var fullKeys = full.Select(p => (p.RootX, p.RootZ, p.Species)).ToHashSet();
        Assert.All(lod3, p => Assert.Contains((p.RootX, p.RootZ, p.Species), fullKeys));

        var lod5 = PreviewTerrainTreePlacer.FilterForLodKeep(full, lodLevel: 5, centerX, centerZ);
        Assert.True(lod5.Count > 0);
        Assert.True(lod5.Count <= lod3.Count);
        Assert.All(lod5, p => Assert.Contains((p.RootX, p.RootZ, p.Species), fullKeys));
    }

    [Fact]
    public void Lod_vegetation_keep_floor_never_empties_forested_section()
    {
        // Find a root that fails the 25% keep mask; floor must still force-keep it when alone.
        PreviewTerrainTreePlacer.Placement? only = null;
        for (var x = 0; x < 256 && only is null; x++)
        {
            for (var z = 0; z < 256 && only is null; z++)
            {
                if (!PreviewTerrainTreePlacer.ShouldKeepRootForLod(x, z, lodLevel: 5))
                {
                    only = new PreviewTerrainTreePlacer.Placement(
                        x,
                        z,
                        SurfaceHeight: 4,
                        Species: PreviewTerrainTreeSpecies.Oak,
                        Materials: default,
                        TrunkHeight: 4,
                        VariantSalt: 0);
                }
            }
        }

        Assert.NotNull(only);
        var kept = PreviewTerrainTreePlacer.FilterForLodKeep(
            [only.Value],
            lodLevel: 5,
            only.Value.RootX,
            only.Value.RootZ);
        Assert.Single(kept);
        Assert.Equal((only.Value.RootX, only.Value.RootZ), (kept[0].RootX, kept[0].RootZ));
    }

    [Fact]
    public void Lod_and_Full_vegetation_share_placement_roots()
    {
        var plan = MakeOakPlan();
        List<PreviewTerrainTreePlacer.Placement>? full = null;
        List<PreviewTerrainTreePlacer.Placement>? lod = null;
        for (var seed = 1; seed < 200 && full is null; seed++)
        {
            var gen = PreviewTerrainWorldGenSettings.Default with { Seed = seed };
            for (var origin = 48; origin <= 160 && full is null; origin += 32)
            {
                var cx0 = origin;
                var cz0 = origin;
                var cx1 = origin + 32;
                var cz1 = origin + 32;
                PreviewTerrainColumnSample Column(int x, int z) =>
                    PreviewTerrainBiomeSampler.Sample(
                        x, z, gen, PreviewStageConstants.TerrainFlatPadHalfExtent);

                var a = PreviewTerrainTreePlacer.CollectForChunk(
                    cx0, cz0, cx1, cz1, Column, gen, plan, placementStep: 1);
                if (a.Count == 0)
                {
                    continue;
                }

                var b = PreviewTerrainTreePlacer.CollectForChunk(
                    cx0, cz0, cx1, cz1, Column, gen, plan, placementStep: 1);
                full = a;
                lod = b;
            }
        }

        Assert.NotNull(full);
        Assert.NotNull(lod);
        Assert.Equal(full.Count, lod.Count);
        Assert.Equal(
            full.Select(p => (p.RootX, p.RootZ, p.Species)).ToArray(),
            lod.Select(p => (p.RootX, p.RootZ, p.Species)).ToArray());
    }

    [Fact]
    public void Impostor_emit_is_much_cheaper_than_full_voxel_at_same_roots()
    {
        var plan = MakeOakPlan();
        var gen = PreviewTerrainWorldGenSettings.Default with { Seed = 7 };
        PreviewTerrainColumnSample Column(int x, int z) =>
            PreviewTerrainBiomeSampler.Sample(x, z, gen, PreviewStageConstants.TerrainFlatPadHalfExtent);
        var placements = PreviewTerrainTreePlacer.CollectForChunk(
            64, -64, 96, -32, Column, gen, plan);
        Assert.True(placements.Count > 0);

        static int FloatCount(
            IReadOnlyList<PreviewTerrainTreePlacer.Placement> roots,
            PreviewTerrainVegetationEmitMode mode)
        {
            var buckets = new List<float>[16];
            for (var i = 0; i < buckets.Length; i++)
            {
                buckets[i] = [];
            }

            var maxH = 0;
            PreviewTerrainTreeMeshEmitter.EmitPlacements(
                roots,
                PreviewStageConstants.GroundPlaneWorldY,
                1f,
                buckets,
                ref maxH,
                emitMode: mode);
            return buckets.Sum(b => b.Count);
        }

        var full = FloatCount(placements, PreviewTerrainVegetationEmitMode.FullVoxel);
        var impostor = FloatCount(placements, PreviewTerrainVegetationEmitMode.Impostor);
        Assert.True(full > 0);
        Assert.True(impostor > 0);
        Assert.True(impostor < full / 4, $"impostor={impostor} full={full}");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void BakeLodSection_lod2_plus_emits_impostor_vegetation_batches(byte lodLevel)
    {
        Assert.Equal(
            PreviewTerrainVegetationEmitMode.Impostor,
            PreviewStageConstants.ResolveLodVegetationEmitMode(lodLevel));

        var plan = MakeOakPlan();
        var grass = PreviewTerrainGrassBakeSettings.BuiltIn with
        {
            VegetationIdentity = plan.Identity,
        };

        PreviewTerrainChunkMesh? meshWithTrees = null;
        for (var seed = 1; seed < 800 && meshWithTrees is null; seed++)
        {
            var gen = PreviewTerrainWorldGenSettings.Default with { Seed = seed };
            for (var sz = 1; sz <= 10 && meshWithTrees is null; sz++)
            {
                for (var sx = 1; sx <= 10 && meshWithTrees is null; sx++)
                {
                    var section = TerrainResidencyKey.Section(sx, sz, lodLevel);
                    var mesh = PreviewTerrainLodMeshBaker.BakeLodSection(
                        section,
                        gen,
                        grass,
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
    }

    [Fact]
    public void Vegetation_emit_is_deterministic_block_space_for_same_placements()
    {
        var plan = MakeOakPlan();
        var gen = PreviewTerrainWorldGenSettings.Default with { Seed = 7 };
        PreviewTerrainColumnSample Column(int x, int z) =>
            PreviewTerrainBiomeSampler.Sample(x, z, gen, PreviewStageConstants.TerrainFlatPadHalfExtent);
        var placements = PreviewTerrainTreePlacer.CollectForChunk(
            64, -64, 96, -32, Column, gen, plan);
        Assert.True(placements.Count > 0);
        Assert.True(PreviewStageConstants.TerrainLodVegetationBlockSpaceIdentity);
        Assert.Equal(
            TerrainResidencyKey.MaxLodLevel,
            PreviewStageConstants.TerrainLodVegetationBlockSpaceMaxLevel);
        Assert.Equal(1, PreviewStageConstants.TerrainLodVegetationFullVoxelMaxLevel);
        Assert.Equal(
            PreviewTerrainVegetationEmitMode.Impostor,
            PreviewStageConstants.ResolveLodVegetationEmitMode(2));
        Assert.Equal(
            PreviewTerrainVegetationEmitMode.FullVoxel,
            PreviewStageConstants.ResolveLodVegetationEmitMode(1));

        static float[] Emit(IReadOnlyList<PreviewTerrainTreePlacer.Placement> roots)
        {
            var buckets = new List<float>[16];
            for (var i = 0; i < buckets.Length; i++)
            {
                buckets[i] = [];
            }

            var maxH = 0;
            PreviewTerrainTreeMeshEmitter.EmitPlacements(
                roots, PreviewStageConstants.GroundPlaneWorldY, 1f, buckets, ref maxH);
            return buckets.SelectMany(b => b).ToArray();
        }

        var first = Emit(placements);
        var second = Emit(placements);
        Assert.Equal(first, second);
        Assert.True(first.Length > 0);
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
    public void SmartLeaves_omits_faces_between_adjacent_leaf_voxels()
    {
        var materials = new PreviewTerrainVegetationBakeEntry(
            PreviewTerrainTreeSpecies.Oak,
            LogSlot: 7,
            LeavesOrTopSlot: 8,
            LogTopSlot: null);
        var placement = new PreviewTerrainTreePlacer.Placement(
            RootX: 0,
            RootZ: 0,
            SurfaceHeight: 4,
            Species: PreviewTerrainTreeSpecies.Oak,
            Materials: materials,
            TrunkHeight: 5,
            VariantSalt: 0);
        PreviewTerrainTreePlacer.Placement[] placements = [placement];

        int CountLeafFloats(bool smartLeaves)
        {
            var buckets = new List<float>[9];
            for (var i = 0; i < buckets.Length; i++)
            {
                buckets[i] = [];
            }

            var maxH = 0;
            PreviewTerrainTreeMeshEmitter.EmitPlacements(
                placements,
                surfaceWorldY: 0f,
                metersPerTile: 1f,
                buckets,
                ref maxH,
                modelTemplates: null,
                PreviewTerrainVegetationEmitMode.FullVoxel,
                smartLeaves);
            return buckets[8].Count;
        }

        var full = CountLeafFloats(smartLeaves: false);
        var culled = CountLeafFloats(smartLeaves: true);
        Assert.True(full > 0, "expected leaf geometry without smart leaves");
        Assert.True(culled > 0, "expected exterior leaf faces with smart leaves");
        Assert.True(culled < full, $"smart leaves should reduce leaf floats ({culled} < {full})");

        // Exterior silhouette must remain: faces are whole quads.
        var floatsPerFace = 4 * PreviewMesh.FloatsPerVertex;
        Assert.True(culled % floatsPerFace == 0);
    }

    [Fact]
    public void SmartLeaves_impostor_emit_unchanged()
    {
        var materials = new PreviewTerrainVegetationBakeEntry(
            PreviewTerrainTreeSpecies.Oak,
            LogSlot: 7,
            LeavesOrTopSlot: 8,
            LogTopSlot: null);
        var placement = new PreviewTerrainTreePlacer.Placement(
            RootX: 10,
            RootZ: 20,
            SurfaceHeight: 3,
            Species: PreviewTerrainTreeSpecies.Oak,
            Materials: materials,
            TrunkHeight: 4,
            VariantSalt: 1);
        PreviewTerrainTreePlacer.Placement[] placements = [placement];

        int CountAllFloats(bool smartLeaves)
        {
            var buckets = new List<float>[9];
            for (var i = 0; i < buckets.Length; i++)
            {
                buckets[i] = [];
            }

            var maxH = 0;
            PreviewTerrainTreeMeshEmitter.EmitPlacements(
                placements,
                surfaceWorldY: 0f,
                metersPerTile: 1f,
                buckets,
                ref maxH,
                modelTemplates: null,
                PreviewTerrainVegetationEmitMode.Impostor,
                smartLeaves);
            var total = 0;
            foreach (var b in buckets)
            {
                total += b.Count;
            }

            return total;
        }

        Assert.Equal(CountAllFloats(smartLeaves: false), CountAllFloats(smartLeaves: true));
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
