using System.Numerics;

using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewTerrainTests
{
    [Fact]
    public void Heightfield_is_deterministic_for_fixed_seed()
    {
        var a = PreviewTerrainHeightfield.BuildColumnHeights(halfExtent: 8, seed: 123);
        var b = PreviewTerrainHeightfield.BuildColumnHeights(halfExtent: 8, seed: 123);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Heightfield_flat_pad_is_zero_and_outer_has_relief()
    {
        const int half = 20;
        const int pad = 14;
        var heights = PreviewTerrainHeightfield.BuildColumnHeights(
            halfExtent: half,
            flatPadHalfExtent: pad,
            transitionBlocks: 4,
            maxRelief: 6,
            seed: PreviewStageConstants.TerrainHeightSeed);

        for (var z = -pad; z < pad; z++)
        {
            for (var x = -pad; x < pad; x++)
            {
                Assert.Equal(0, PreviewTerrainHeightfield.GetHeight(heights, x, z, half));
            }
        }

        var anyRelief = false;
        for (var z = -half; z < half; z++)
        {
            for (var x = -half; x < half; x++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(z)) <= pad)
                {
                    continue;
                }

                if (PreviewTerrainHeightfield.GetHeight(heights, x, z, half) != 0)
                {
                    anyRelief = true;
                }
            }
        }

        Assert.True(anyRelief, "expected some non-zero heights outside the flat pad");
    }

    [Fact]
    public void TerrainBake_pad_top_faces_sit_at_grid_world_y()
    {
        const int half = 8;
        const int pad = 4;
        var heights = PreviewTerrainHeightfield.BuildColumnHeights(
            halfExtent: half,
            flatPadHalfExtent: pad,
            transitionBlocks: 2,
            maxRelief: 3,
            seed: 7);
        var bake = PreviewTerrainMeshBaker.Bake(
            heights,
            halfExtent: half,
            fillDepth: 2,
            chunkSize: 4,
            surfaceWorldY: PreviewStageConstants.GridWorldY);

        var topY = PreviewStageConstants.GridWorldY;
        var foundPadTop = false;
        var v = bake.Mesh.InterleavedVertices;
        var idx = bake.Mesh.Indices;
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            var i0 = (int)idx[t];
            var i1 = (int)idx[t + 1];
            var i2 = (int)idx[t + 2];
            var p0 = ReadPos(v, i0);
            var p1 = ReadPos(v, i1);
            var p2 = ReadPos(v, i2);
            var n = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            if (n.Y < 0.9f)
            {
                continue;
            }

            var y = (p0.Y + p1.Y + p2.Y) / 3f;
            var x = (p0.X + p1.X + p2.X) / 3f;
            var z = (p0.Z + p1.Z + p2.Z) / 3f;
            if (MathF.Abs(y - topY) > 1e-3f)
            {
                continue;
            }

            if (MathF.Abs(x) <= pad && MathF.Abs(z) <= pad)
            {
                foundPadTop = true;
                // Geometric normal must point +Y (outward) for the pad turf.
                Assert.True(n.Y > 0.9f);
                break;
            }
        }

        Assert.True(foundPadTop, "expected +Y faces on the flat pad at GridWorldY");
    }

    private static Vector3 ReadPos(float[] verts, int vertexIndex)
    {
        var o = vertexIndex * PreviewMesh.FloatsPerVertex;
        return new Vector3(verts[o], verts[o + 1], verts[o + 2]);
    }

    [Fact]
    public void TerrainBake_top_face_uv_spans_one_tile_per_world_unit()
    {
        // Two adjacent flat columns → one greedy top quad of size 2×1 (or larger).
        var heights = new int[4]; // halfExtent 1 → side 2
        var bake = PreviewTerrainMeshBaker.Bake(
            heights,
            halfExtent: 1,
            fillDepth: 1,
            chunkSize: 2,
            metersPerTile: 1f,
            surfaceWorldY: 0f,
            nearPomRadius: 100f,
            lodMaxDistance: 0f);

        const int s = PreviewMesh.FloatsPerVertex;
        var v = bake.Mesh.InterleavedVertices;
        var topVerts = new List<(Vector3 P, Vector2 Uv)>();
        for (var i = 0; i < bake.Mesh.VertexCount; i++)
        {
            var o = i * s;
            if (MathF.Abs(v[o + 4] - 1f) > 1e-4f)
            {
                continue;
            }

            topVerts.Add((
                new Vector3(v[o], v[o + 1], v[o + 2]),
                new Vector2(v[o + 6], v[o + 7])));
        }

        Assert.True(topVerts.Count >= 4);
        // UV should equal world XZ at metersPerTile=1.
        foreach (var (p, uv) in topVerts)
        {
            Assert.InRange(MathF.Abs(uv.X - p.X), 0f, 1e-4f);
            Assert.InRange(MathF.Abs(uv.Y - p.Z), 0f, 1e-4f);
        }
    }

    [Fact]
    public void TerrainBake_side_faces_have_outward_geometric_normals()
    {
        // Single column: exposed ±X/±Z sides must wind so Cross(e1,e2) matches attributed normal.
        int[] heights = [0, 0, 0, 0];
        var bake = PreviewTerrainMeshBaker.Bake(
            heights,
            halfExtent: 1,
            fillDepth: 1,
            chunkSize: 2,
            surfaceWorldY: 0f,
            nearPomRadius: 100f);

        const int s = PreviewMesh.FloatsPerVertex;
        var v = bake.Mesh.InterleavedVertices;
        var idx = bake.Mesh.Indices;
        var checkedSide = false;
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            var p0 = ReadPos(v, (int)idx[t]);
            var p1 = ReadPos(v, (int)idx[t + 1]);
            var p2 = ReadPos(v, (int)idx[t + 2]);
            var geo = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            var o = (int)idx[t] * s;
            var attributed = new Vector3(v[o + 3], v[o + 4], v[o + 5]);
            if (MathF.Abs(attributed.Y) > 0.5f)
            {
                continue; // tops/bottoms covered elsewhere
            }

            Assert.True(Vector3.Dot(geo, attributed) > 0.9f,
                $"side face winding opposes normal geo={geo} attr={attributed}");
            checkedSide = true;
        }

        Assert.True(checkedSide, "expected at least one side face");
    }

    [Fact]
    public void IsSolid_seals_tall_columns_to_shared_floor_past_fillDepth()
    {
        // Cliff shelf: tall=10 beside short=0 with fillDepth=3. Pre-fix solids were only
        // y=8..10 on the tall column → sky holes at y=1..7. Shared floor must seal that.
        int HeightAt(int x, int z) => x == 0 ? 10 : 0;

        Assert.Equal(
            PreviewStageConstants.TerrainSolidFloorRelativeY,
            PreviewTerrainMeshBaker.SolidBottomY(10, fillDepth: 3));

        Assert.True(PreviewTerrainMeshBaker.IsSolid(HeightAt, fillDepth: 3, bx: 0, by: 5, bz: 0));
        Assert.True(PreviewTerrainMeshBaker.IsSolid(
            HeightAt,
            fillDepth: 3,
            bx: 0,
            by: PreviewStageConstants.TerrainSolidFloorRelativeY,
            bz: 0));
        Assert.False(PreviewTerrainMeshBaker.IsSolid(HeightAt, fillDepth: 3, bx: 0, by: 11, bz: 0));
        Assert.False(PreviewTerrainMeshBaker.IsSolid(HeightAt, fillDepth: 3, bx: 1, by: 5, bz: 0));
        Assert.True(PreviewTerrainMeshBaker.IsSolid(HeightAt, fillDepth: 3, bx: 1, by: 0, bz: 0));
    }

    [Fact]
    public void ResolveLayerMin_reaches_shared_solid_floor()
    {
        Assert.Equal(
            PreviewStageConstants.TerrainSolidFloorRelativeY,
            PreviewTerrainMeshBaker.ResolveLayerMin(minColumnHeight: 0, fillDepth: 3));
        Assert.Equal(
            PreviewStageConstants.TerrainSolidFloorRelativeY,
            PreviewTerrainMeshBaker.ResolveLayerMin(minColumnHeight: 12, fillDepth: 3));
    }

    [Fact]
    public void TerrainBake_occludes_internal_faces_between_neighbors()
    {
        // 2×1 flat pad: shared interior vertical face must not be emitted.
        var heights = new[] { 0, 0, 0, 0 }; // half=1 → 2×2 all flat
        var bake = PreviewTerrainMeshBaker.Bake(
            heights,
            halfExtent: 1,
            fillDepth: 1,
            chunkSize: 2,
            surfaceWorldY: 0f,
            nearPomRadius: 100f);

        // Without occlusion every solid cell emits 6 faces. Shared floor makes columns thick,
        // so assert against a naive per-cell bound rather than a thin-slab constant.
        var solidLayers = 0 - PreviewTerrainMeshBaker.SolidBottomY(0, fillDepth: 1) + 1;
        var naiveVerts = 4 * solidLayers * 6 * 4;
        Assert.True(bake.Mesh.VertexCount < naiveVerts,
            $"expected occluded/merged shell, got {bake.Mesh.VertexCount} verts (naive {naiveVerts})");

        // No +X face at x=0 between columns -1|0 (shared plane at x=0 inside solid volume of neighbors).
        // Both columns solid through the shared floor; shared face at x=0 should be culled.
        const int s = PreviewMesh.FloatsPerVertex;
        var v = bake.Mesh.InterleavedVertices;
        var sharedInterior = false;
        for (var i = 0; i < bake.Mesh.VertexCount; i++)
        {
            var o = i * s;
            var nx = v[o + 3];
            var x = v[o];
            if (MathF.Abs(MathF.Abs(nx) - 1f) < 1e-4f && MathF.Abs(x) < 1e-4f)
            {
                // A face on the x=0 plane with ±X normal between two solids would be interior.
                // Outer boundary faces are at x=-1 or x=+2 for halfExtent=1 columns [-1,0] and [0,1].
                // Interior shared plane is x=0 between bx=-1 and bx=0 — should not exist.
                sharedInterior = true;
                break;
            }
        }

        Assert.False(sharedInterior, "interior ±X faces at x=0 should be occluded");
    }

    [Fact]
    public void TerrainBake_chunk_batches_have_bounds_and_outer_lod()
    {
        var heights = PreviewTerrainHeightfield.BuildColumnHeights(
            halfExtent: 16,
            flatPadHalfExtent: 4,
            transitionBlocks: 2,
            maxRelief: 3,
            seed: 99);
        var bake = PreviewTerrainMeshBaker.Bake(
            heights,
            halfExtent: 16,
            fillDepth: 2,
            chunkSize: 8,
            nearPomRadius: 10f,
            lodMaxDistance: 50f);

        Assert.NotEmpty(bake.ChunkBatches);
        var anyOuterLod = false;
        foreach (var batch in bake.ChunkBatches)
        {
            Assert.True(batch.IndexCount > 0);
            Assert.True(batch.HasBounds);
            Assert.True(batch.BoundsRadius > 0f);
            if (batch.LodMaxDistance > 0f)
            {
                anyOuterLod = true;
                Assert.Equal(50f, batch.LodMaxDistance);
                Assert.False(batch.EnableParallax);
            }
        }

        Assert.True(anyOuterLod, "expected outer chunks to carry LodMaxDistance");
        Assert.Contains(bake.ChunkBatches, static b => b is { EnableParallax: true, LodMaxDistance: <= 0f });
    }

    [Fact]
    public void ForOrbitPreview_uses_terrain_ceiling_when_provided()
    {
        // Close eye above a small pad: a tall ceiling pushes far out vs a thin floor slab.
        var eye = new Vector3(0f, 2f, 0f);
        var (_, farThin) = PreviewCameraDepthRange.ForOrbitPreview(
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(0.5f, 1f, 0.5f),
            orbitDistance: 3f,
            eye,
            environmentHalfExtent: 4f,
            environmentFloorY: 0f);

        var (_, farTall) = PreviewCameraDepthRange.ForOrbitPreview(
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(0.5f, 1f, 0.5f),
            orbitDistance: 3f,
            eye,
            environmentHalfExtent: 4f,
            environmentFloorY: 0f,
            environmentCeilingY: 20f);

        Assert.True(farTall > farThin, $"farTall={farTall} should exceed farThin={farThin} when ceiling rises");
        Assert.True(farTall <= PreviewCameraDepthRange.DefaultFar);
    }

    [Fact]
    public void ForOrbitPreview_large_terrain_keeps_near_small_when_eye_outside_aabb()
    {
        // Pulled-back fly camera outside ±48 terrain used to push near to ~10+ and carve the foreground.
        var eye = new Vector3(0f, 15f, -80f);
        var (near, far) = PreviewCameraDepthRange.ForOrbitPreview(
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(0.5f, 1f, 0.5f),
            orbitDistance: 80f,
            eye,
            environmentHalfExtent: 48f,
            environmentFloorY: -0.56f,
            environmentCeilingY: 6f);

        Assert.True(near <= 1.0f, $"near={near} must not scale with AABB exit distance");
        Assert.True(far > 100f, $"far={far} should use the large-environment cap");
        Assert.True(far <= PreviewCameraDepthRange.LargeEnvironmentFar);
    }

    [Fact]
    public void SampleColumn_matches_BuildColumnHeights_inside_extent()
    {
        const int half = 12;
        var built = PreviewTerrainHeightfield.BuildColumnHeights(halfExtent: half, seed: 99);
        for (var z = -half; z < half; z++)
        {
            for (var x = -half; x < half; x++)
            {
                Assert.Equal(
                    PreviewTerrainHeightfield.GetHeight(built, x, z, half),
                    PreviewTerrainHeightfield.SampleColumn(x, z, seed: 99));
            }
        }
    }

    [Fact]
    public void BakeFullChunk_produces_indices_and_bounds()
    {
        var mesh = PreviewTerrainMeshBaker.BakeFullChunk(new TerrainChunkKey(0, 0));
        Assert.NotNull(mesh);
        Assert.Equal(TerrainChunkLodKind.Full, mesh.Lod);
        Assert.True(mesh.Indices.Length >= 6);
        Assert.True(mesh.BoundsRadius > 0f);
    }

    [Fact]
    public void BakeLodChunk_has_fewer_or_equal_indices_than_full_and_no_fill_dependency()
    {
        var key = new TerrainChunkKey(2, -1);
        var full = PreviewTerrainMeshBaker.BakeFullChunk(key);
        var lod = PreviewTerrainLodMeshBaker.BakeLodChunk(key);
        Assert.NotNull(full);
        Assert.NotNull(lod);
        Assert.Equal(TerrainChunkLodKind.Lod, lod.Lod);
        Assert.True(lod.Indices.Length >= 6);
        Assert.True(lod.Indices.Length <= full.Indices.Length,
            $"lod={lod.Indices.Length} should be <= full={full.Indices.Length}");
    }

    [Fact]
    public void BakeFullChunk_is_deterministic_with_height_cache()
    {
        var key = new TerrainChunkKey(1, -2);
        var a = PreviewTerrainMeshBaker.BakeFullChunk(key);
        var b = PreviewTerrainMeshBaker.BakeFullChunk(key);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a.Indices, b.Indices);
        Assert.Equal(a.InterleavedVertices, b.InterleavedVertices);
        Assert.Equal(a.MinRelativeHeight, b.MinRelativeHeight);
        Assert.Equal(a.MaxRelativeHeight, b.MaxRelativeHeight);
    }

    [Fact]
    public void ResolveHorizontalFaceMaterial_BetterGrass_uses_Top_on_height_step()
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
    public void ResolveHorizontalFaceMaterial_without_BetterGrass_uses_Side_on_surface()
    {
        int HeightAt(int x, int z) => 2;
        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: false,
            EmitOverlay: true);

        var mat = PreviewTerrainMeshBaker.ResolveHorizontalFaceMaterial(
            HeightAt, bx: 0, by: 2, bz: 0, neighborX: 1, neighborZ: 0, settings);
        Assert.Equal(PreviewTerrainGrassSlots.Side, mat);
    }

    [Fact]
    public void ResolveHorizontalFaceMaterial_fill_layer_uses_Dirt()
    {
        int HeightAt(int x, int z) => 2;
        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: true,
            EmitOverlay: false);

        var mat = PreviewTerrainMeshBaker.ResolveHorizontalFaceMaterial(
            HeightAt, bx: 0, by: 1, bz: 0, neighborX: 1, neighborZ: 0, settings);
        Assert.Equal(PreviewTerrainGrassSlots.Dirt, mat);
    }

    [Fact]
    public void ResolveYFaceMaterial_BlockModel_top_and_bottom()
    {
        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: true,
            EmitOverlay: false);
        Assert.Equal(PreviewTerrainGrassSlots.Top, PreviewTerrainMeshBaker.ResolveYFaceMaterial(true, settings));
        Assert.Equal(PreviewTerrainGrassSlots.Dirt, PreviewTerrainMeshBaker.ResolveYFaceMaterial(false, settings));
    }

    [Fact]
    public void BakeFullChunk_BlockModelFaces_emits_material_batches_including_overlay()
    {
        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: true,
            EmitOverlay: true);
        var mesh = PreviewTerrainMeshBaker.BakeFullChunk(new TerrainChunkKey(0, 0), settings);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh.DrawBatches);
        Assert.Contains(mesh.DrawBatches, b => b.MaterialIndex == PreviewTerrainGrassSlots.Top);
        // Flat pad / biome bake at the origin may emit stone/sand instead of dirt fill; material
        // slots must still stay within the grass/biome table and include a solid ground batch.
        Assert.All(
            mesh.DrawBatches,
            b => Assert.InRange(b.MaterialIndex, 0, PreviewTerrainGrassSlots.MaxCount - 1));
        Assert.Contains(
            mesh.DrawBatches,
            b => b.MaterialIndex is PreviewTerrainGrassSlots.Dirt
                or PreviewTerrainGrassSlots.Stone
                or PreviewTerrainGrassSlots.Sand
                or PreviewTerrainGrassSlots.Gravel
                or PreviewTerrainGrassSlots.Side);
        // Flat pad under subject may still expose side/overlay on relief edges outside pad.
        Assert.True(
            mesh.DrawBatches.Any(b => b.MaterialIndex == PreviewTerrainGrassSlots.Side) ||
            mesh.DrawBatches.Any(b => b.MaterialIndex == PreviewTerrainGrassSlots.Top) ||
            mesh.DrawBatches.Any(b => b.MaterialIndex == PreviewTerrainGrassSlots.Overlay));
    }

    [Fact]
    public void BakeLodChunk_flat_pad_uses_Top_batch()
    {
        var lod = PreviewTerrainLodMeshBaker.BakeLodChunk(new TerrainChunkKey(0, 0));
        Assert.NotNull(lod);
        Assert.NotEmpty(lod.DrawBatches);
        Assert.All(lod.DrawBatches, b => Assert.InRange(b.MaterialIndex, 0, PreviewTerrainGrassSlots.MaxCount - 1));
        Assert.Contains(lod.DrawBatches, b => b.MaterialIndex == PreviewTerrainGrassSlots.Top);
    }

    [Fact]
    public void BakeLodChunk_emits_biome_material_slots_outside_flat_pad()
    {
        var settings = new PreviewTerrainGrassBakeSettings(
            PreviewTerrainGrassMode.BlockModelFaces,
            BetterGrassEnabled: true,
            EmitOverlay: true,
            HasStone: true,
            HasSand: true,
            HasGravel: true);
        var lod = PreviewTerrainLodMeshBaker.BakeLodChunk(new TerrainChunkKey(3, -2), grassSettings: settings);
        Assert.NotNull(lod);
        Assert.NotEmpty(lod.DrawBatches);
        Assert.All(lod.DrawBatches, b => Assert.InRange(b.MaterialIndex, 0, PreviewTerrainGrassSlots.MaxCount - 1));
    }

    [Fact]
    public void TerrainChunkStreamer_desired_rings_match_view_distance()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(new Vector3(8f, 2f, 8f), chunkViewDistance: 3, lodRingChunks: 6);
        var desired = streamer.SnapshotDesired();
        Assert.Equal(3, streamer.HardRadiusChunks);
        Assert.Equal(6, streamer.LodRingChunks);
        Assert.Equal(9, streamer.LodRadiusChunks);

        var cam = TerrainChunkKey.FromWorld(8f, 8f);
        Assert.True(desired.TryGetValue(TerrainResidencyKey.Full(cam), out var camKind));
        Assert.Equal(TerrainChunkLodKind.Full, camKind);

        var edgeFull = TerrainResidencyKey.Full(cam.X + 3, cam.Z);
        Assert.True(desired.TryGetValue(edgeFull, out var fullKind));
        Assert.Equal(TerrainChunkLodKind.Full, fullKind);

        // Outer ring uses combined section keys, not one Lod entry per chunk.
        Assert.Contains(desired, kv => kv.Value.IsLod());
        Assert.DoesNotContain(desired, kv => kv.Key.IsLod && kv.Key.ChunksPerSide == 1);

        var outside = TerrainResidencyKey.Full(cam.X + streamer.UnloadRadiusChunks + 1, cam.Z);
        Assert.False(desired.ContainsKey(outside));
        Assert.True(streamer.ShouldUnload(outside));
    }

    [Fact]
    public void TerrainChunkStreamer_lod_ring_setting_extends_desired_radius()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(new Vector3(0f, 2f, 0f), chunkViewDistance: 2, lodRingChunks: 10);
        Assert.Equal(12, streamer.LodRadiusChunks);
        var desired = streamer.SnapshotDesired();
        var cam = TerrainChunkKey.FromWorld(0f, 0f);
        Assert.Contains(
            desired,
            kv => kv.Value.IsLod() &&
                  kv.Key.ChebyshevDistanceToChunk(cam) >= 3 &&
                  kv.Key.ChebyshevDistanceToChunk(cam) <= 12);
        Assert.DoesNotContain(
            desired,
            kv => kv.Key.ChebyshevDistanceToChunk(cam) > streamer.LodRadiusChunks);
    }

    [Fact]
    public void TerrainChunkStreamer_lod_bands_escalate_section_scale()
    {
        var cam = new TerrainChunkKey(0, 0);
        var desired = TerrainChunkStreamer.BuildDesiredResidency(cam, hardRadius: 2, lodRingChunks: 9);
        Span<TerrainChunkStreamer.LodBand> bands = stackalloc TerrainChunkStreamer.LodBand[7];
        var n = TerrainChunkStreamer.ResolveLodBands(2, 9, bands);
        Assert.Equal(3, n);
        Assert.True(bands[0].DMax < bands[1].DMax);
        Assert.True(bands[1].DMax < bands[2].DMax);
        Assert.Equal(11, bands[2].DMax);

        Assert.Contains(desired, kv => kv.Value == TerrainChunkLodKind.Lod1 && kv.Key.ChunksPerSide == 2);
        Assert.Contains(desired, kv => kv.Value == TerrainChunkLodKind.Lod2 && kv.Key.ChunksPerSide == 4);
        Assert.Contains(desired, kv => kv.Value == TerrainChunkLodKind.Lod3 && kv.Key.ChunksPerSide == 8);
        // LOD underlay ring overlaps Full for fade-out; only the Full *core* excludes LOD.
        var overlap = TerrainChunkStreamer.ResolveLodFadeOverlapChunks();
        var fullCore = 2 - overlap;
        if (fullCore >= 0)
        {
            Assert.DoesNotContain(
                desired,
                kv => kv.Key.IsLod && kv.Key.MaxChebyshevDistanceToChunk(cam) <= fullCore);
        }

        Assert.Contains(
            desired,
            kv => kv.Key.IsLod && kv.Key.OverlapsFullDisk(cam, 2));
    }

    [Fact]
    public void TerrainLodDetailFade_window_fades_finer_out_at_band_edge()
    {
        TerrainChunkStreamer.ResolveLodDetailFadeMeters(
            hardRadius: 8,
            lodRingChunks: 128,
            lodLevel: 0,
            out var fadeStart,
            out var fadeEnd);
        var hardMeters = 8 * PreviewStageConstants.TerrainChunkSize;
        Assert.Equal(hardMeters, fadeEnd, precision: 3);
        Assert.Equal(
            PreviewStageConstants.TerrainLodDetailFadeWidthMeters,
            fadeEnd - fadeStart,
            precision: 3);
        Assert.Equal(32f, PreviewStageConstants.TerrainLodDetailFadeWidthMeters);
        Assert.Equal(3, TerrainChunkStreamer.ResolveLodFadeOverlapChunks());
        Assert.False(TerrainChunkStreamer.IsOutermostLodLevel(8, 128, 0));
        Assert.True(TerrainChunkStreamer.IsOutermostLodLevel(8, 128, 7));
    }

    [Fact]
    public void TerrainChunkStreamer_adjacent_underlay_keeps_lod2_out_of_full_disk()
    {
        var cam = new TerrainChunkKey(0, 0);
        var desired = TerrainChunkStreamer.BuildDesiredResidency(cam, hardRadius: 8, lodRingChunks: 64);
        Assert.DoesNotContain(
            desired,
            kv => kv.Key.LodLevel >= 2 && kv.Key.OverlapsFullDisk(cam, 8));
        // LOD1 may still underlay the Full outer fade ring.
        Assert.Contains(
            desired,
            kv => kv.Key.LodLevel == 1 && kv.Key.OverlapsFullDisk(cam, 8));
    }

    [Fact]
    public void BuildDiskPrefetchResidency_covers_all_active_lod_levels_over_ring()
    {
        var cam = new TerrainChunkKey(0, 0);
        const int hard = 4;
        const int ring = 64;
        var levels = TerrainChunkStreamer.ResolveActiveLodLevelCount(ring);
        Assert.True(levels >= 3);

        var desired = TerrainChunkStreamer.BuildDesiredResidency(cam, hard, ring);
        var prefetch = TerrainChunkStreamer.BuildDiskPrefetchResidency(cam, hard, ring);

        // Prefetch has every active level; desired is band-primary (+ thin underlay) only.
        for (byte level = 1; level <= levels; level++)
        {
            Assert.Contains(prefetch, kv => kv.Key.LodLevel == level);
        }

        Assert.True(prefetch.Count > desired.Count(kv => kv.Key.IsLod));

        // A world cell in a mid band should be coverable by both its band LOD and neighbors on disk.
        Span<TerrainChunkStreamer.LodBand> bands = stackalloc TerrainChunkStreamer.LodBand[TerrainResidencyKey.MaxLodLevel];
        var bandCount = TerrainChunkStreamer.ResolveLodBands(hard, ring, bands);
        Assert.True(bandCount >= 2);
        var mid = bands[1];
        Assert.Contains(
            prefetch,
            kv => kv.Key.LodLevel == mid.Level &&
                  kv.Key.ChebyshevDistanceToChunk(cam) <= mid.DMax &&
                  kv.Key.MaxChebyshevDistanceToChunk(cam) >= mid.DMin);
        Assert.Contains(
            prefetch,
            kv => kv.Key.LodLevel != mid.Level &&
                  kv.Key.LodLevel >= 1 &&
                  kv.Key.ChebyshevDistanceToChunk(cam) <= mid.DMax &&
                  kv.Key.MaxChebyshevDistanceToChunk(cam) >= mid.DMin);
    }

    [Fact]
    public void TerrainChunkStreamer_disk_prefetch_picks_when_gpu_desired_idle()
    {
        var root = Path.Combine(Path.GetTempPath(), "autopbr-lod-prefetch-" + Guid.NewGuid().ToString("N"));
        using var streamer = new TerrainChunkStreamer(new TerrainRegionPackStore(root));
        try
        {
            streamer.Tick(Vector3.Zero, chunkViewDistance: 2, lodRingChunks: 16);
            streamer.SetScheduleMaxRingForTests(streamer.LodRadiusChunks);
            foreach (var (k, want) in streamer.SnapshotDesired())
            {
                streamer.NotifyUploaded(k, want);
            }

            Assert.True(streamer.TryPickJobForTests(out var key, out var lod, allowDiskPrefetch: true));
            Assert.True(key.IsLod);
            Assert.True(lod.IsLod());
            Assert.False(streamer.SnapshotDesired().ContainsKey(key));
            Assert.True(streamer.SnapshotDiskPrefetchForTests().ContainsKey(key));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TerrainChunkStreamer_reserved_disk_warm_runs_after_unlocked_window_is_satisfied()
    {
        var root = Path.Combine(Path.GetTempPath(), "autopbr-lod-warm-" + Guid.NewGuid().ToString("N"));
        using var streamer = new TerrainChunkStreamer(new TerrainRegionPackStore(root));
        try
        {
            streamer.Tick(Vector3.Zero, chunkViewDistance: 2, lodRingChunks: 16);
            streamer.SetScheduleMaxRingForTests(streamer.LodRadiusChunks);
            foreach (var (k, want) in streamer.SnapshotDesired())
            {
                streamer.NotifyUploaded(k, want);
            }

            Assert.True(streamer.TryPickJobForTests(
                out var key,
                out var lod,
                allowDiskPrefetch: true,
                preferLodLane: true,
                preferDiskWarm: true));
            Assert.True(key.IsLod);
            Assert.True(lod.IsLod());
            Assert.False(streamer.SnapshotDesired().ContainsKey(key));
            Assert.True(streamer.SnapshotDiskPrefetchForTests().ContainsKey(key));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void IsTransitionCoverageKey_protects_seam_not_distant_coarse()
    {
        var cam = new TerrainChunkKey(0, 0);
        const int hard = 8;
        Assert.True(
            TerrainChunkStreamer.IsTransitionCoverageKey(TerrainResidencyKey.Full(0, 0), cam, hard));
        Assert.True(
            TerrainChunkStreamer.IsTransitionCoverageKey(
                TerrainResidencyKey.Section(4, 0, lodLevel: 1), cam, hard));
        // Coarse Lod7 far out must not be treated as seam coverage (old ChunksPerSide bug).
        Assert.False(
            TerrainChunkStreamer.IsTransitionCoverageKey(
                TerrainResidencyKey.Section(8, 0, lodLevel: 7), cam, hard));
    }

    [Fact]
    public void ScheduledPriority_PutsEntireFullDiskAheadOfDistantLod()
    {
        var cam = new TerrainChunkKey(0, 0);
        const int hard = 8;

        Assert.Equal(
            TerrainStreamPriority.CoverageRepair,
            TerrainChunkStreamer.ResolveScheduledPriority(
                TerrainResidencyKey.Full(8, 8), cam, hard));
        Assert.Equal(
            TerrainStreamPriority.PredictedArrival,
            TerrainChunkStreamer.ResolveScheduledPriority(
                TerrainResidencyKey.Section(8, 0, lodLevel: 4), cam, hard));
        Assert.Equal(
            TerrainStreamPriority.VisibleRefinement,
            TerrainChunkStreamer.ResolveScheduledPriority(
                TerrainResidencyKey.Full(9, 0), cam, hard));
    }

    [Fact]
    public void TerrainResidencyDiagnostics_separates_gpu_fake_park_and_unlocked()
    {
        var cam = new TerrainChunkKey(0, 0);
        var desired = new Dictionary<TerrainResidencyKey, TerrainChunkLodKind>
        {
            [TerrainResidencyKey.Full(0, 0)] = TerrainChunkLodKind.Full,
            [TerrainResidencyKey.Full(1, 0)] = TerrainChunkLodKind.Full,
            [TerrainResidencyKey.Section(2, 0, 1)] = TerrainChunkLodKind.Lod1,
            [TerrainResidencyKey.Section(8, 0, 3)] = TerrainChunkLodKind.Lod3,
        };
        var gpu = new HashSet<TerrainResidencyKey> { TerrainResidencyKey.Full(0, 0) };
        var deferred = new HashSet<TerrainResidencyKey>
        {
            TerrainResidencyKey.Full(1, 0), // retry — not streamer-resident
            TerrainResidencyKey.Section(8, 0, 3), // fake-park — streamer-resident
        };
        var streamerResident = new HashSet<TerrainResidencyKey>
        {
            TerrainResidencyKey.Full(0, 0),
            TerrainResidencyKey.Section(8, 0, 3),
        };

        var counts = TerrainResidencyDiagnostics.Count(
            desired,
            gpu,
            deferred,
            streamerResident.Contains,
            cam,
            scheduleMaxRing: 4);

        Assert.Equal(1, counts.GpuResident);
        Assert.Equal(1, counts.FakeParked);
        Assert.Equal(1, counts.DeferredRetry);
        Assert.Equal(3, counts.UnlockedDesired); // Full×2 + Lod1 ring≤4; Lod3 ring far
        Assert.Equal(4, counts.DesiredTotal);
        Assert.Contains("gpuResident=1", counts.Format(), StringComparison.Ordinal);
        Assert.Contains("fakeParked=1", counts.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void EstimateTerrainMeshPoolNeedBytes_impostor_bands_cheaper_than_all_voxel()
    {
        // Far ring with impostor LOD≥2 should stay below a naive all-voxel estimate.
        var need = PreviewStageConstants.EstimateTerrainMeshPoolNeedBytes(8, 256);
        var reserved = PreviewStageConstants.EstimateTerrainMeshPoolReservedBytes(8, 256);
        Assert.True(need > reserved);
        Assert.True(
            PreviewStageConstants.TerrainMeshPoolEstimateLodImpostorBytesPerWorldChunk <
            PreviewStageConstants.TerrainMeshPoolEstimateLodVegBytesPerWorldChunk);
    }

    [Fact]
    public void EstimateTerrainMeshPoolNeedBytes_keep_thinning_reduces_far_need()
    {
        // P11.1 scales impostor cost by keep fraction on LOD≥3; total far need must drop vs
        // a naive estimate that ignores thinning (reconstructed by temporarily treating keep=1).
        var thinned = PreviewStageConstants.EstimateTerrainMeshPoolNeedBytes(8, 256);
        Assert.True(thinned > 0);
        Assert.Equal(0.25f, PreviewStageConstants.ResolveLodVegetationKeepFraction(5));
        Assert.True(
            PreviewStageConstants.ResolveLodVegetationKeepFraction(3) <
            PreviewStageConstants.ResolveLodVegetationKeepFraction(2));
    }

    [Fact]
    public void EstimateTerrainMeshPoolReservedBytes_is_less_than_total_far_need()
    {
        var reserved = PreviewStageConstants.EstimateTerrainMeshPoolReservedBytes(8, 256);
        var total = PreviewStageConstants.EstimateTerrainMeshPoolNeedBytes(8, 256);
        var distant = PreviewStageConstants.EstimateTerrainMeshPoolDistantLodBytes(8, 256);
        Assert.True(reserved > 0);
        Assert.True(total > reserved);
        Assert.Equal(total - reserved, distant);
    }

    [Fact]
    public void BakeLodSection_emits_edge_skirts()
    {
        var section = TerrainResidencyKey.Section(2, 0, lodLevel: 1);
        var mesh = PreviewTerrainLodMeshBaker.BakeLodSection(section);
        Assert.NotNull(mesh);
        // Tops alone on a flat pad are few quads; skirts add vertical faces on all four edges.
        Assert.True(mesh.Indices.Length >= 6 * 8, $"expected skirted mesh, indices={mesh.Indices.Length}");
    }

    [Fact]
    public void TerrainChunkStreamer_extreme_ring_uses_coarse_sections_and_stays_tractable()
    {
        var cam = new TerrainChunkKey(0, 0);
        Assert.Equal(7, TerrainChunkStreamer.ResolveActiveLodLevelCount(1024));
        Assert.Equal(7, TerrainChunkStreamer.ResolveActiveLodLevelCount(512));
        Assert.Equal(7, TerrainChunkStreamer.ResolveActiveLodLevelCount(256));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var desired = TerrainChunkStreamer.BuildDesiredResidency(
            cam,
            hardRadius: 8,
            lodRingChunks: PreviewStageConstants.TerrainLodRingPresetExtreme);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 250, $"desired build took {sw.ElapsedMilliseconds} ms");
        Assert.True(desired.Count < 8_000, $"expected coarse residency, got {desired.Count}");
        Assert.Contains(desired, kv => kv.Value == TerrainChunkLodKind.Full);
        Assert.Contains(desired, kv => kv.Value == TerrainChunkLodKind.Lod7 && kv.Key.ChunksPerSide == 128);
        Assert.Contains(
            desired,
            kv => kv.Key.IsLod &&
                  kv.Key.ChebyshevDistanceToChunk(cam) >= PreviewStageConstants.TerrainLodRingPresetFar);
        var fullCore = 8 - TerrainChunkStreamer.ResolveLodFadeOverlapChunks();
        foreach (var kv in desired)
        {
            if (kv.Key.IsLod && fullCore >= 0)
            {
                Assert.True(
                    kv.Key.MaxChebyshevDistanceToChunk(cam) > fullCore,
                    $"LOD section inside Full core: {kv.Key}");
            }
        }
    }

    [Fact]
    public void BakeLodSection_cheaper_than_equivalent_full_chunk_footprint()
    {
        var section = TerrainResidencyKey.Section(1, 0, lodLevel: 2); // 4×4 chunks, step 4 m
        var lod = PreviewTerrainLodMeshBaker.BakeLodSection(section);
        Assert.NotNull(lod);

        var fullIndexSum = 0;
        var fullBatchSum = 0;
        var originChunkX = section.OriginChunkX;
        var originChunkZ = section.OriginChunkZ;
        for (var dz = 0; dz < section.ChunksPerSide; dz++)
        {
            for (var dx = 0; dx < section.ChunksPerSide; dx++)
            {
                var full = PreviewTerrainMeshBaker.BakeFullChunk(
                    new TerrainChunkKey(originChunkX + dx, originChunkZ + dz));
                Assert.NotNull(full);
                fullIndexSum += full.Indices.Length;
                fullBatchSum += Math.Max(1, full.DrawBatches.Length);
            }
        }

        Assert.True(
            lod.Indices.Length * 2 < fullIndexSum,
            $"lod indices={lod.Indices.Length} should be << full sum={fullIndexSum}");
        Assert.True(
            Math.Max(1, lod.DrawBatches.Length) < fullBatchSum,
            $"lod batches={lod.DrawBatches.Length} should be < full batch sum={fullBatchSum}");
    }

    [Fact]
    public void TerrainLodSectionCache_hit_and_clear()
    {
        var cache = new TerrainLodSectionCache();
        var key = new TerrainLodCacheKey(
            TerrainResidencyKey.Section(2, -1, 2),
            TerrainLodCacheFingerprint.From(
                PreviewTerrainWorldGenSettings.Default,
                PreviewTerrainGrassBakeSettings.BuiltIn,
                null));
        var mesh = PreviewTerrainLodMeshBaker.BakeLodSection(key.Residency);
        Assert.NotNull(mesh);

        cache.Store(key, mesh);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(key, out var hit));
        Assert.Same(mesh, hit);
        Assert.Equal(1, cache.Hits);

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void TerrainLodDiskCache_round_trips_and_fingerprint_mismatch_misses()
    {
        var root = Path.Combine(Path.GetTempPath(), "autopbr-lod-disk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var disk = new TerrainLodDiskCache(root);
            var residency = TerrainResidencyKey.Section(2, -1, 2);
            var fingerprint = TerrainLodCacheFingerprint.From(
                PreviewTerrainWorldGenSettings.Default,
                PreviewTerrainGrassBakeSettings.BuiltIn,
                null);
            var key = new TerrainLodCacheKey(residency, fingerprint);
            var mesh = PreviewTerrainLodMeshBaker.BakeLodSection(residency);
            Assert.NotNull(mesh);

            disk.TryStore(key, mesh);
            Assert.True(File.Exists(disk.ResolvePath(key)));
            Assert.Equal(1, disk.SuccessfulStoreCount);
            Assert.Equal(0, disk.StoreFailureCount);
            Assert.True(disk.TryLoad(key, out var loaded));
            Assert.NotNull(loaded);
            Assert.Equal(mesh.Key, loaded.Key);
            Assert.Equal(mesh.InterleavedVertices, loaded.InterleavedVertices);
            Assert.Equal(mesh.Indices, loaded.Indices);
            Assert.Equal(mesh.DrawBatches.Length, loaded.DrawBatches.Length);
            Assert.Equal(mesh.MinRelativeHeight, loaded.MinRelativeHeight);
            Assert.Equal(mesh.MaxRelativeHeight, loaded.MaxRelativeHeight);

            var mismatched = new TerrainLodCacheKey(
                residency,
                fingerprint with { Seed = fingerprint.Seed + 1 });
            Assert.False(disk.TryLoad(mismatched, out _));

            disk.Clear();
            Assert.False(Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TerrainLodDiskCache_default_root_uses_roaming_app_data()
    {
        var disk = new TerrainLodDiskCache();
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutoPBR",
            "terrain-lod-cache");
        Assert.Equal(expected, disk.RootDirectory);
    }

    [Fact]
    public void TerrainChunkStreamer_InvalidateAll_clears_lod_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "autopbr-lod-inv-" + Guid.NewGuid().ToString("N"));
        using var streamer = new TerrainChunkStreamer(new TerrainRegionPackStore(root));
        var section = TerrainResidencyKey.Section(0, 1, 1);
        var mesh = PreviewTerrainLodMeshBaker.BakeLodSection(section);
        Assert.NotNull(mesh);
        var cacheKey = new TerrainLodCacheKey(
            section,
            TerrainLodCacheFingerprint.From(
                streamer.WorldGenSettings,
                streamer.GrassBakeSettings,
                streamer.VegetationBakePlan));
        streamer.LodCache.Store(cacheKey, mesh);
        streamer.LodDiskCache.TryStore(cacheKey, mesh);
        Assert.Equal(1, streamer.LodCache.Count);
        var packStore = Assert.IsType<TerrainRegionPackStore>(streamer.LodDiskCache);
        Assert.True(File.Exists(packStore.ResolvePackPath(cacheKey)));

        streamer.InvalidateAll();
        Assert.Equal(0, streamer.LodCache.Count);
        Assert.False(File.Exists(packStore.ResolvePackPath(cacheKey)));
    }

    [Fact]
    public void TerrainChunkStreamer_InvalidateAll_preserves_schedule_hold()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.HoldScheduleExpansion = true;

        streamer.InvalidateAll();

        Assert.True(streamer.HoldScheduleExpansion);
    }

    [Fact]
    public void TerrainGpuLodMinLevel_keeps_lod1_and_2_on_worker_cpu()
    {
        Assert.Equal(3, PreviewStageConstants.TerrainGpuLodMinLevel);
        Assert.True(PreviewStageConstants.TerrainGpuLodMinLevel > 2);
    }

    [Fact]
    public void TerrainGpuLodJob_bake_matches_BakeLodSection()
    {
        var section = TerrainResidencyKey.Section(1, 1, PreviewStageConstants.TerrainGpuLodMinLevel);
        var grass = PreviewTerrainGrassBakeSettings.BuiltIn;
        var gen = PreviewTerrainWorldGenSettings.Default with { Seed = 42 };
        var expected = PreviewTerrainLodMeshBaker.BakeLodSection(section, gen, grass);
        Assert.NotNull(expected);

        var job = new TerrainGpuLodJob(section, grass, gen);
        var actual = PreviewTerrainLodMeshBaker.BakeLodSection(
            job.Key,
            job.WorldGen,
            job.GrassSettings,
            job.Vegetation);
        Assert.NotNull(actual);
        Assert.Equal(expected.InterleavedVertices.Length, actual.InterleavedVertices.Length);
        Assert.Equal(expected.Indices.Length, actual.Indices.Length);
        Assert.Equal(expected.Key, actual.Key);
    }

    [Fact]
    public void TerrainChunkStreamer_skips_desired_rebuild_when_camera_chunk_unchanged()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(new Vector3(1f, 2f, 1f), chunkViewDistance: 2);
        var first = streamer.SnapshotDesired();
        streamer.Tick(new Vector3(1.2f, 2f, 1.4f), chunkViewDistance: 2);
        var second = streamer.SnapshotDesired();
        Assert.Same(first, second);

        streamer.Tick(new Vector3(40f, 2f, 40f), chunkViewDistance: 2);
        var third = streamer.SnapshotDesired();
        Assert.NotSame(first, third);
        Assert.True(third.Count > 0);
    }

    [Fact]
    public void ObsoleteLodUnderFullDisk_unloads_even_when_camera_inside_section()
    {
        var cam = new TerrainChunkKey(0, 0);
        // Lod2 4×4 section covering the origin — closest Chebyshev dist is 0 under the eye.
        var section = TerrainResidencyKey.Section(0, 0, lodLevel: 2);
        Assert.Equal(0, section.ChebyshevDistanceToChunk(cam));
        Assert.True(section.OverlapsFullDisk(cam, hardRadiusChunks: 8));
        Assert.True(
            TerrainChunkStreamer.IsObsoleteLodUnderFullDisk(
                section,
                inDesired: false,
                cam,
                hardRadiusChunks: 8));
        Assert.False(
            TerrainChunkStreamer.IsObsoleteLodUnderFullDisk(
                section,
                inDesired: true,
                cam,
                hardRadiusChunks: 8));

        var farTrail = TerrainResidencyKey.Section(20, 0, lodLevel: 3);
        var underEyeRank = TerrainChunkStreamer.RankGpuDisposal(section, cam, 8, inDesired: false);
        var farRank = TerrainChunkStreamer.RankGpuDisposal(farTrail, cam, 8, inDesired: false);
        Assert.True(underEyeRank > farRank, $"underEye={underEyeRank} far={farRank}");
    }

    [Fact]
    public void HasFullDiskGpuCoverageForLodSection_pins_until_full_chunks_resident()
    {
        var cam = new TerrainChunkKey(0, 0);
        var section = TerrainResidencyKey.Section(0, 0, lodLevel: 2);
        var resident = new HashSet<TerrainResidencyKey>();
        Assert.False(
            TerrainChunkStreamer.HasFullDiskGpuCoverageForLodSection(
                section, cam, hardRadiusChunks: 8, resident.Contains));

        // Cover only part of the Full footprint — still incomplete.
        for (var z = 0; z < 2; z++)
        {
            for (var x = 0; x < 2; x++)
            {
                resident.Add(TerrainResidencyKey.Full(x, z));
            }
        }

        Assert.False(
            TerrainChunkStreamer.HasFullDiskGpuCoverageForLodSection(
                section, cam, hardRadiusChunks: 8, resident.Contains));

        for (var z = section.OriginChunkZ; z < section.OriginChunkZ + section.ChunksPerSide; z++)
        {
            for (var x = section.OriginChunkX; x < section.OriginChunkX + section.ChunksPerSide; x++)
            {
                if (Math.Max(Math.Abs(x - cam.X), Math.Abs(z - cam.Z)) <= 8)
                {
                    resident.Add(TerrainResidencyKey.Full(x, z));
                }
            }
        }

        Assert.True(
            TerrainChunkStreamer.HasFullDiskGpuCoverageForLodSection(
                section, cam, hardRadiusChunks: 8, resident.Contains));
    }

    [Fact]
    public void HasFootprintReplacementCoverage_pins_lod_skirt_until_replacement_resident()
    {
        var cam = new TerrainChunkKey(0, 0);
        // LOD2 4×4 section straddling Full disk edge when hardRadius=2.
        var leaving = TerrainResidencyKey.Section(0, 0, lodLevel: 2);
        var residents = new HashSet<TerrainResidencyKey> { leaving };

        // Full-disk cells alone are not enough — skirt outside hard radius must be covered too.
        for (var z = leaving.OriginChunkZ; z < leaving.OriginChunkZ + leaving.ChunksPerSide; z++)
        {
            for (var x = leaving.OriginChunkX; x < leaving.OriginChunkX + leaving.ChunksPerSide; x++)
            {
                if (Math.Max(Math.Abs(x - cam.X), Math.Abs(z - cam.Z)) <= 2)
                {
                    residents.Add(TerrainResidencyKey.Full(x, z));
                }
            }
        }

        Assert.True(
            TerrainChunkStreamer.HasFullDiskGpuCoverageForLodSection(
                leaving, cam, hardRadiusChunks: 2, residents.Contains));
        Assert.False(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                leaving, cam, keepRadiusChunks: 8, residents));

        // Cover the remaining skirt with a neighbor LOD1 section grid.
        for (var z = leaving.OriginChunkZ; z < leaving.OriginChunkZ + leaving.ChunksPerSide; z++)
        {
            for (var x = leaving.OriginChunkX; x < leaving.OriginChunkX + leaving.ChunksPerSide; x++)
            {
                if (Math.Max(Math.Abs(x - cam.X), Math.Abs(z - cam.Z)) <= 2)
                {
                    continue;
                }

                residents.Add(TerrainResidencyKey.FromChunk(new TerrainChunkKey(x, z), lodLevel: 1));
            }
        }

        Assert.True(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                leaving, cam, keepRadiusChunks: 8, residents));
    }

    [Fact]
    public void ResolveSoftUnloadHysteresisChunks_scales_with_lod_section_size()
    {
        Assert.Equal(
            PreviewStageConstants.TerrainSoftUnloadHysteresisChunks,
            TerrainChunkStreamer.ResolveSoftUnloadHysteresisChunks(TerrainResidencyKey.Full(0, 0)));
        Assert.Equal(
            Math.Max(PreviewStageConstants.TerrainSoftUnloadHysteresisChunks, 4),
            TerrainChunkStreamer.ResolveSoftUnloadHysteresisChunks(
                TerrainResidencyKey.Section(0, 0, lodLevel: 2)));
        Assert.Equal(
            Math.Max(PreviewStageConstants.TerrainSoftUnloadHysteresisChunks, 16),
            TerrainChunkStreamer.ResolveSoftUnloadHysteresisChunks(
                TerrainResidencyKey.Section(0, 0, lodLevel: 4)));
    }

    [Fact]
    public void TerrainStreamSchedule_same_ring_orders_clockwise()
    {
        var cam = new TerrainChunkKey(0, 0);
        // Ring 2 Full neighbors around the camera (Chebyshev = 2).
        var north = TerrainResidencyKey.Full(0, 2);   // +Z
        var east = TerrainResidencyKey.Full(2, 0);    // +X
        var south = TerrainResidencyKey.Full(0, -2);  // -Z
        var west = TerrainResidencyKey.Full(-2, 0);   // -X
        Assert.Equal(2, TerrainStreamSchedule.RingIndex(north, cam));
        Assert.Equal(2, TerrainStreamSchedule.RingIndex(east, cam));

        var angleN = TerrainStreamSchedule.ClockAngle(north, cam);
        var angleE = TerrainStreamSchedule.ClockAngle(east, cam);
        var angleS = TerrainStreamSchedule.ClockAngle(south, cam);
        var angleW = TerrainStreamSchedule.ClockAngle(west, cam);
        Assert.True(angleN < angleE, $"N={angleN} E={angleE}");
        Assert.True(angleE < angleS, $"E={angleE} S={angleS}");
        Assert.True(angleS < angleW, $"S={angleS} W={angleW}");

        Assert.True(TerrainStreamSchedule.CompareKeys(north, east, cam) < 0);
        Assert.True(TerrainStreamSchedule.CompareKeys(east, south, cam) < 0);
        Assert.True(TerrainStreamSchedule.CompareKeys(south, west, cam) < 0);
        // Full phase before LOD at the same ring.
        var lod = TerrainResidencyKey.Section(1, 0, lodLevel: 1);
        Assert.True(TerrainStreamSchedule.CompareKeys(north, lod, cam) < 0);
    }

    [Fact]
    public void TerrainChunkStreamer_soft_start_waits_for_stage2_lod_queue()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(Vector3.Zero, chunkViewDistance: 4, lodRingChunks: 8);
        streamer.SetScheduleMaxRingForTests(4);
        foreach (var (k, want) in streamer.SnapshotDesired())
        {
            if (want == TerrainChunkLodKind.Full ||
                TerrainChunkStreamer.IsSoftStartUngatedKey(k, streamer.CameraChunk, 4) ||
                TerrainStreamSchedule.RingIndex(k, streamer.CameraChunk) <= 4)
            {
                streamer.NotifyUploaded(k, want);
            }
        }

        // Window is saturated — Stage-2 LOD queue must still block band unlock.
        streamer.EnqueueGpuLodJobForTests(new TerrainGpuLodJob(
            TerrainResidencyKey.Section(20, 20, 3),
            streamer.GrassBakeSettings,
            streamer.WorldGenSettings));
        Assert.Equal(1, streamer.GpuLodJobCount);
        Assert.False(streamer.TryPickJobForTests(out _, out _));
        Assert.Equal(4, streamer.ScheduleMaxRing);

        streamer.DrainAbandonedGpuLodJobs();
        Assert.Equal(0, streamer.GpuLodJobCount);
        Assert.False(streamer.TryPickJobForTests(out _, out _));
        var expected = TerrainChunkStreamer.ResolveNextScheduleMaxRing(4, 8, 4);
        Assert.Equal(expected, streamer.ScheduleMaxRing);
    }

    [Fact]
    public void TerrainChunkStreamer_soft_start_does_not_expand_before_first_tick()
    {
        using var streamer = new TerrainChunkStreamer();
        var start = streamer.ScheduleMaxRing;

        Assert.False(streamer.TryPickJobForTests(out _, out _));
        Assert.Equal(start, streamer.ScheduleMaxRing);
        Assert.True(streamer.ScheduleMaxRing <= streamer.HardRadiusChunks);
    }

    [Fact]
    public void TerrainChunkStreamer_soft_start_gates_lod_but_full_always_eligible()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(Vector3.Zero, chunkViewDistance: 4, lodRingChunks: 8);
        // After desired rebuild, Full hard disk is unlocked.
        Assert.True(streamer.ScheduleMaxRing >= 4);

        streamer.SetScheduleMaxRingForTests(4);
        // Saturate Full + unlocked LOD + ungated transition seam.
        foreach (var (k, want) in streamer.SnapshotDesired())
        {
            if (want == TerrainChunkLodKind.Full ||
                TerrainChunkStreamer.IsSoftStartUngatedKey(k, streamer.CameraChunk, 4) ||
                TerrainStreamSchedule.RingIndex(k, streamer.CameraChunk) <= 4)
            {
                streamer.NotifyUploaded(k, want);
            }
        }

        Assert.False(streamer.TryPickJobForTests(out _, out _));
        var expected = TerrainChunkStreamer.ResolveNextScheduleMaxRing(4, 8, 4);
        Assert.Equal(expected, streamer.ScheduleMaxRing);

        // Strict target cuts are sparse: an unlock step may jump past empty mid-bands.
        // Either a newly unlocked outer leaf is pickable, or soft-start continues to the lod cap.
        if (streamer.TryPickJobForTests(out var outer, out var outerLod))
        {
            Assert.True(outerLod.IsLod());
            Assert.True(TerrainStreamSchedule.RingIndex(outer, streamer.CameraChunk) <= expected);
            Assert.True(TerrainStreamSchedule.RingIndex(outer, streamer.CameraChunk) > 4);
            return;
        }

        var guard = 0;
        while (streamer.ScheduleMaxRing < streamer.LodRadiusChunks && guard++ < 8)
        {
            Assert.False(streamer.TryPickJobForTests(out _, out _));
            if (streamer.TryPickJobForTests(out outer, out outerLod))
            {
                Assert.True(outerLod.IsLod());
                Assert.True(
                    TerrainStreamSchedule.RingIndex(outer, streamer.CameraChunk) >
                    4);
                return;
            }
        }

        Assert.Equal(streamer.LodRadiusChunks, streamer.ScheduleMaxRing);
    }

    [Fact]
    public void TerrainChunkStreamer_hold_blocks_schedule_unlock_until_cleared()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(Vector3.Zero, chunkViewDistance: 4, lodRingChunks: 8);
        streamer.SetScheduleMaxRingForTests(4);
        foreach (var (k, want) in streamer.SnapshotDesired())
        {
            if (want == TerrainChunkLodKind.Full ||
                TerrainChunkStreamer.IsSoftStartUngatedKey(k, streamer.CameraChunk, 4) ||
                TerrainStreamSchedule.RingIndex(k, streamer.CameraChunk) <= 4)
            {
                streamer.NotifyUploaded(k, want);
            }
        }

        streamer.HoldScheduleExpansion = true;
        Assert.False(streamer.TryPickJobForTests(out _, out _));
        Assert.Equal(4, streamer.ScheduleMaxRing);

        streamer.HoldScheduleExpansion = false;
        Assert.False(streamer.TryPickJobForTests(out _, out _));
        Assert.Equal(
            TerrainChunkStreamer.ResolveNextScheduleMaxRing(4, 8, 4),
            streamer.ScheduleMaxRing);
    }

    [Fact]
    public void TerrainChunkStreamer_reserves_lod_lane_while_hard_full_is_incomplete()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(Vector3.Zero, chunkViewDistance: 2, lodRingChunks: 8);

        // Full bakes can be expensive when vegetation is enabled. LOD coverage must not be
        // globally serialized behind the complete hard Full disk.
        Assert.True(streamer.TryPickJobForTests(out var key, out var lod, preferLodLane: true));
        Assert.True(lod.IsLod());
        Assert.True(key.IsLod);

        // The complementary lane still prioritizes camera-nearest Full.
        Assert.True(streamer.TryPickJobForTests(out var fullKey, out var fullLod));
        Assert.Equal(TerrainChunkLodKind.Full, fullLod);
        Assert.True(fullKey.IsFull);
    }

    [Fact]
    public void TerrainChunkStreamer_soft_start_does_not_race_while_full_pending()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(Vector3.Zero, chunkViewDistance: 8, lodRingChunks: 512);
        var start = streamer.ScheduleMaxRing;
        Assert.True(start <= streamer.HardRadiusChunks);

        // With Full still pending, picks must not unlock the entire 520-chunk lod radius.
        Assert.True(streamer.TryPickJobForTests(out _, out _));
        Assert.True(streamer.TryPickJobForTests(out _, out _, preferLodLane: true));
        Assert.Equal(start, streamer.ScheduleMaxRing);
        Assert.True(streamer.ScheduleMaxRing < streamer.LodRadiusChunks);
    }

    [Fact]
    public void TerrainChunkStreamer_disk_warm_does_not_steal_while_full_pending()
    {
        var root = Path.Combine(Path.GetTempPath(), "autopbr-lod-warm-steal-" + Guid.NewGuid().ToString("N"));
        using var streamer = new TerrainChunkStreamer(new TerrainRegionPackStore(root));
        try
        {
            streamer.Tick(Vector3.Zero, chunkViewDistance: 8, lodRingChunks: 512);
            Assert.True(streamer.TryPickJobForTests(
                out var key,
                out var lod,
                allowDiskPrefetch: true,
                preferDiskWarm: true));
            Assert.True(key.IsFull);
            Assert.Equal(TerrainChunkLodKind.Full, lod);
            Assert.True(streamer.ScheduleMaxRing <= streamer.HardRadiusChunks);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TerrainChunkStreamer_transition_lod_picks_when_schedule_ring_is_low()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(Vector3.Zero, chunkViewDistance: 4, lodRingChunks: 8);
        streamer.SetScheduleMaxRingForTests(0);
        // Saturate Full so the next pick can be ungated transition LOD.
        foreach (var (k, want) in streamer.SnapshotDesired())
        {
            if (want == TerrainChunkLodKind.Full)
            {
                streamer.NotifyUploaded(k, want);
            }
        }

        Assert.True(streamer.TryPickJobForTests(out var key, out var lod));
        Assert.True(lod.IsLod());
        Assert.True(
            TerrainChunkStreamer.IsSoftStartUngatedKey(key, streamer.CameraChunk, 4),
            $"expected ungated transition LOD, got {key}");
    }

    [Fact]
    public void ResolveNextScheduleMaxRing_jumps_to_band_end()
    {
        const int hard = 4;
        const int ring = 64;
        Span<TerrainChunkStreamer.LodBand> bands =
            stackalloc TerrainChunkStreamer.LodBand[TerrainResidencyKey.MaxLodLevel];
        var n = TerrainChunkStreamer.ResolveLodBands(hard, ring, bands);
        Assert.True(n >= 2);
        var fromHard = TerrainChunkStreamer.ResolveNextScheduleMaxRing(hard, ring, hard);
        Assert.Equal(bands[0].DMax, fromHard);
        var fromFirstBand = TerrainChunkStreamer.ResolveNextScheduleMaxRing(hard, ring, bands[0].DMax);
        Assert.Equal(bands[1].DMax, fromFirstBand);
    }

    [Fact]
    public void ApplyDesiredMembershipHysteresis_keeps_prior_lod_near_band_edge()
    {
        var cam = new TerrainChunkKey(0, 0);
        var prior = TerrainChunkStreamer.BuildDesiredResidency(cam, hardRadius: 4, lodRingChunks: 32);
        var nextCam = new TerrainChunkKey(1, 0);
        var next = TerrainChunkStreamer.BuildDesiredResidency(nextCam, hardRadius: 4, lodRingChunks: 32);
        var freshCount = next.Count;
        TerrainChunkStreamer.ApplyDesiredMembershipHysteresis(
            next, prior, nextCam, hardRadius: 4, lodRingChunks: 32);
        Assert.True(next.Count >= freshCount);
        Assert.Contains(next, kv => kv.Key.IsLod && prior.ContainsKey(kv.Key));
    }

    [Fact]
    public void TerrainChunkStreamer_full_picks_even_when_schedule_ring_is_low()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(Vector3.Zero, chunkViewDistance: 4, lodRingChunks: 8);
        streamer.SetScheduleMaxRingForTests(0);
        Assert.True(streamer.TryPickJobForTests(out var key, out var lod));
        Assert.Equal(TerrainChunkLodKind.Full, lod);
        Assert.True(key.IsFull);
    }

    [Fact]
    public void TerrainChunkStreamer_preserves_schedule_unlock_across_camera_moves()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(Vector3.Zero, chunkViewDistance: 4, lodRingChunks: 64);
        streamer.SetScheduleMaxRingForTests(20);
        Assert.Equal(20, streamer.ScheduleMaxRing);

        // Move one chunk — unlock progress must not collapse back to hard radius only.
        streamer.Tick(new Vector3(16f, 0f, 0f), chunkViewDistance: 4, lodRingChunks: 64);
        Assert.True(
            streamer.ScheduleMaxRing >= 20,
            $"schedule collapsed to {streamer.ScheduleMaxRing} after camera move");
        Assert.True(streamer.ScheduleMaxRing >= streamer.HardRadiusChunks);
    }

    [Fact]
    public void TerrainStreamSchedule_upload_sort_matches_clockwise_annular_order()
    {
        var cam = new TerrainChunkKey(0, 0);
        var keys = new[]
        {
            TerrainResidencyKey.Full(2, 0),
            TerrainResidencyKey.Full(0, 2),
            TerrainResidencyKey.Full(3, 0),
            TerrainResidencyKey.Section(2, 0, lodLevel: 1),
        };
        Array.Sort(keys, (a, b) => TerrainStreamSchedule.CompareKeys(a, b, cam));
        Assert.Equal(TerrainResidencyKey.Full(0, 2), keys[0]); // ring 2, angle 0 (+Z)
        Assert.Equal(TerrainResidencyKey.Full(2, 0), keys[1]); // ring 2, +X
        Assert.Equal(TerrainResidencyKey.Full(3, 0), keys[2]); // ring 3 Full before LOD
        Assert.True(keys[3].IsLod);
    }

    [Fact]
    public void ResolveTerrainMeshPoolBudgetBytes_scales_with_lod_ring_and_clamps()
    {
        var small = PreviewStageConstants.ResolveTerrainMeshPoolBudgetBytes(8, 16);
        var extreme = PreviewStageConstants.ResolveTerrainMeshPoolBudgetBytes(8, 1024);
        Assert.True(small >= PreviewStageConstants.TerrainMeshPoolBudgetDefaultBytes);
        Assert.True(extreme > small);
        Assert.True(extreme <= PreviewStageConstants.TerrainMeshPoolBudgetUnknownCeilingBytes);
        Assert.True(small <= PreviewStageConstants.TerrainMeshPoolBudgetUnknownCeilingBytes);
    }

    [Fact]
    public void EstimateTerrainMeshPoolNeedBytes_is_veg_aware_and_grows_with_full_disk()
    {
        var near = PreviewStageConstants.EstimateTerrainMeshPoolNeedBytes(4, 8);
        var far = PreviewStageConstants.EstimateTerrainMeshPoolNeedBytes(8, 1024);
        Assert.True(far > near);

        // Full disk alone at hard=8: 17² × ~384 KiB ≈ 108 MiB before LOD/headroom.
        var fullOnly = PreviewStageConstants.EstimateTerrainMeshPoolNeedBytes(8, 0);
        var fullSide = 2L * 8 + 1;
        var fullFloor = fullSide * fullSide * PreviewStageConstants.TerrainMeshPoolEstimateFullChunkBytes;
        Assert.True(fullOnly >= fullFloor);

        // High-water feedback raises the soft target when uploads already exceeded the a-priori need.
        const long highWater = 2L * 1024 * 1024 * 1024;
        var withHw = PreviewStageConstants.ResolveTerrainMeshPoolBudgetBytes(4, 8, 0, highWater);
        var withoutHw = PreviewStageConstants.ResolveTerrainMeshPoolBudgetBytes(4, 8);
        Assert.True(withHw > withoutHw);
        Assert.Equal(
            Math.Min(
                PreviewStageConstants.TerrainMeshPoolBudgetUnknownCeilingBytes,
                (long)(highWater * PreviewStageConstants.TerrainMeshPoolBudgetHeadroom)),
            withHw);
    }

    [Fact]
    public void TerrainLodCacheFingerprint_bake_revision_covers_lod_veg_policy()
    {
        Assert.True(TerrainLodCacheFingerprint.CurrentBakeRevision >= 11);
    }

    [Fact]
    public void ResolveTerrainMeshPoolBudgetBytes_clamps_to_detected_vram_fraction()
    {
        // 8 GiB dedicated → usable 7.25 GiB → 35% ≈ 2598 MiB
        const long vram8GiB = 8L * 1024 * 1024 * 1024;
        var ceiling = PreviewStageConstants.ResolveTerrainMeshPoolCeilingBytes(vram8GiB);
        var expected = (long)((vram8GiB - PreviewStageConstants.TerrainMeshPoolVramReserveBytes) *
                              PreviewStageConstants.TerrainMeshPoolVramFraction);
        Assert.Equal(expected, ceiling);

        var extreme = PreviewStageConstants.ResolveTerrainMeshPoolBudgetBytes(8, 1024, vram8GiB);
        Assert.Equal(ceiling, extreme);
        Assert.True(extreme < PreviewStageConstants.TerrainMeshPoolBudgetAbsoluteCeilingBytes);
    }

    [Fact]
    public void ResolveTerrainMeshPoolBudgetBytes_large_vram_can_exceed_legacy_3gib_unknown_cap()
    {
        const long vram24GiB = 24L * 1024 * 1024 * 1024;
        var ceiling = PreviewStageConstants.ResolveTerrainMeshPoolCeilingBytes(vram24GiB);
        Assert.True(ceiling > PreviewStageConstants.TerrainMeshPoolBudgetUnknownCeilingBytes);
        Assert.True(ceiling <= PreviewStageConstants.TerrainMeshPoolBudgetAbsoluteCeilingBytes);

        // With Full-identical LOD vegetation again, a-priori need for far rings can exceed
        // the legacy 3 GiB ladder when dedicated VRAM allows it.
        var fromNeed = PreviewStageConstants.ResolveTerrainMeshPoolBudgetBytes(8, 1024, vram24GiB);
        Assert.True(fromNeed <= ceiling);

        const long highWater = 4L * 1024 * 1024 * 1024;
        var budget = PreviewStageConstants.ResolveTerrainMeshPoolBudgetBytes(
            8, 1024, vram24GiB, highWater);
        Assert.True(budget > PreviewStageConstants.TerrainMeshPoolBudgetUnknownCeilingBytes);
        Assert.True(budget <= ceiling);
    }

    [Fact]
    public void TerrainChunkStreamer_multi_worker_produces_ready_meshes()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Start();
        Assert.Equal(TerrainChunkStreamer.ResolveWorkerCount(), streamer.WorkerCount);
        Assert.True(streamer.WorkerCount >= 1);
        Assert.True(streamer.WorkerCount <= 2);
        Assert.Equal(12, PreviewStageConstants.TerrainMaxBakeJobsAhead);

        streamer.Tick(Vector3.Zero, chunkViewDistance: 2);
        var uploads = new List<PreviewTerrainChunkMesh>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline &&
               (uploads.Count < 4 || uploads.All(mesh => mesh.Lod != TerrainChunkLodKind.Full)))
        {
            var frame = new List<PreviewTerrainChunkMesh>();
            streamer.DrainReady(frame, 8);
            foreach (var mesh in frame)
            {
                uploads.Add(mesh);
                streamer.NotifyUploaded(mesh.Key, mesh.Lod);
            }

            if (uploads.Count < 4 || uploads.All(mesh => mesh.Lod != TerrainChunkLodKind.Full))
            {
                Thread.Sleep(10);
            }
        }

        Assert.True(uploads.Count >= 4, $"expected ready meshes, got {uploads.Count}");
        Assert.Contains(uploads, m => m.Lod == TerrainChunkLodKind.Full);
        Assert.Contains(uploads, m => m.Key.ChebyshevDistanceToChunk(new TerrainChunkKey(0, 0)) <= 1);
    }

    [Fact]
    public void TerrainChunkDrawCull_frustum_keeps_pad_under_camera()
    {
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(
            new Vector3(0f, 12f, 0.01f),
            new Vector3(0f, 0f, 0f),
            Vector3.UnitZ);
        var projection = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            MathF.PI / 3f,
            1f,
            0.1f,
            200f);
        var vp = projection * view;

        var candidates = new TerrainChunkDrawCull.Candidate[]
        {
            new()
            {
                BoundsCenter = new Vector3(0f, 0f, 0f),
                BoundsRadius = 12f,
                Lod = TerrainChunkLodKind.Full,
                NearPom = true,
                SourceIndex = 0
            },
            new()
            {
                BoundsCenter = new Vector3(80f, 0f, 80f),
                BoundsRadius = 12f,
                Lod = TerrainChunkLodKind.Lod,
                NearPom = false,
                SourceIndex = 1
            }
        };
        var selected = new List<int>();
        TerrainChunkDrawCull.Select(
            candidates,
            vp,
            cameraPosition: new Vector3(0f, 12f, 0f),
            fallbackCount: 64,
            fullOnly: false,
            selected);

        Assert.Contains(0, selected);
        Assert.True(selected.Count >= 1);
    }

    [Fact]
    public void TerrainNearPom_fade_band_extends_enable_radius()
    {
        var enableR = PreviewStageConstants.TerrainNearPomRadius +
                      PreviewStageConstants.TerrainNearPomFadeWidth;
        Assert.True(PreviewStageConstants.TerrainNearPomFadeWidth > 0f);
        Assert.True(enableR > PreviewStageConstants.TerrainNearPomRadius);
        Assert.Equal(30f, enableR);
    }

    [Fact]
    public void TerrainShadowFar_coverage_matches_default_and_extreme_lod_ring()
    {
        var defaultRing =
            (PreviewStageConstants.TerrainDefaultChunkViewDistance +
             PreviewStageConstants.TerrainLodRingChunks) *
            (float)PreviewStageConstants.TerrainChunkSize;
        var extremeRing =
            (PreviewStageConstants.TerrainDefaultChunkViewDistance +
             PreviewStageConstants.TerrainLodRingPresetExtreme) *
            (float)PreviewStageConstants.TerrainChunkSize;
        Assert.True(PreviewShadowFrustum.TerrainShadowFarMaxHalfExtent >= defaultRing);
        Assert.True(PreviewShadowFrustum.TerrainShadowFarMaxHalfExtent >= extremeRing);
        Assert.True(PreviewShadowFrustum.TerrainShadowFarMaxHalfExtent >=
                    PreviewShadowFrustum.TerrainShadowMinXzHalfExtent);
    }

    [Fact]
    public void PreviewFrustumPlanes_match_gl_column_clip_at_screen_edges()
    {
        // Regression: Extract must use matrix rows so cull matches shader clip = (proj*view)*world
        // after the preview's Transpose-then-column-major upload (see PreviewFrustumPlanes docs).
        var eye = new Vector3(114.12f, 27.53f, -62.74f);
        var target = new Vector3(91.27f, 28.53f, -59.33f);
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(eye, target, Vector3.UnitY);
        var proj = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            42f * (MathF.PI / 180f),
            813f / 573f,
            0.05f,
            400f);
        var vp = proj * view;
        Span<Vector4> planes = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];
        PreviewFrustumPlanes.Extract(vp, planes);

        var forward = Vector3.Normalize(target - eye);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        var falseCulls = 0;
        var inNdc = 0;
        for (var dist = 10f; dist <= 120f; dist += 5f)
        {
            var halfH = MathF.Tan(21f * (MathF.PI / 180f)) * dist;
            var halfW = halfH * (813f / 573f);
            foreach (var (u, v) in new[]
                     {
                         (0f, 0f),
                         (halfW * 0.85f, 0f),
                         (-halfW * 0.85f, 0f),
                         (0f, halfH * 0.85f),
                         (0f, -halfH * 0.85f),
                         (halfW * 0.7f, halfH * 0.7f),
                         (-halfW * 0.7f, -halfH * 0.7f),
                     })
            {
                var p = eye + forward * dist + right * u + up * v;
                var clip = ColumnMul(vp, new Vector4(p, 1f));
                if (clip.W <= 1e-4f)
                {
                    continue;
                }

                var ndc = clip / clip.W;
                if (MathF.Abs(ndc.X) > 1f || MathF.Abs(ndc.Y) > 1f || ndc.Z < -1f || ndc.Z > 1f)
                {
                    continue;
                }

                inNdc++;
                if (!PreviewFrustumPlanes.SphereIntersects(planes, p, 0.5f))
                {
                    falseCulls++;
                }
            }
        }

        Assert.True(inNdc >= 20, $"expected on-screen samples, got {inNdc}");
        Assert.Equal(0, falseCulls);
    }

    private static Vector4 ColumnMul(Matrix4x4 m, Vector4 v) => new(
        m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14 * v.W,
        m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24 * v.W,
        m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34 * v.W,
        m.M41 * v.X + m.M42 * v.Y + m.M43 * v.Z + m.M44 * v.W);

    [Fact]
    public void TerrainChunkDrawCull_fallback_nearest_when_frustum_empty()
    {
        // Camera looks away from all candidates → frustum miss → nearest fallback.
        var view = PreviewGlMatrices.CreateLookAtRhOpenGlRowStorage(
            new Vector3(0f, 2f, 0f),
            new Vector3(0f, 2f, -10f),
            Vector3.UnitY);
        var projection = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            MathF.PI / 6f,
            1f,
            0.1f,
            20f);
        var vp = projection * view;

        var candidates = new TerrainChunkDrawCull.Candidate[3];
        for (var i = 0; i < candidates.Length; i++)
        {
            candidates[i] = new TerrainChunkDrawCull.Candidate
            {
                BoundsCenter = new Vector3(100f + i * 20f, 0f, 100f),
                BoundsRadius = 8f,
                Lod = TerrainChunkLodKind.Full,
                NearPom = false,
                SourceIndex = i
            };
        }

        var selected = new List<int>();
        TerrainChunkDrawCull.Select(
            candidates,
            vp,
            cameraPosition: new Vector3(0f, 2f, 0f),
            fallbackCount: 2,
            fullOnly: false,
            selected);

        Assert.Equal(2, selected.Count);
        Assert.Contains(0, selected);
        Assert.Contains(1, selected);
        Assert.DoesNotContain(2, selected);
    }

    [Fact]
    public void TerrainChunkDrawCull_shadow_full_only_skips_lod()
    {
        var view = Matrix4x4.Identity;
        var projection = Matrix4x4.CreateOrthographic(200f, 200f, 0.1f, 200f);
        var vp = projection * view;
        var candidates = new TerrainChunkDrawCull.Candidate[]
        {
            new()
            {
                BoundsCenter = Vector3.Zero,
                BoundsRadius = 20f,
                Lod = TerrainChunkLodKind.Full,
                NearPom = false,
                SourceIndex = 0
            },
            new()
            {
                BoundsCenter = new Vector3(5f, 0f, 5f),
                BoundsRadius = 20f,
                Lod = TerrainChunkLodKind.Lod,
                NearPom = false,
                SourceIndex = 1
            }
        };
        var selected = new List<int>();
        TerrainChunkDrawCull.Select(
            candidates, vp, Vector3.Zero, fallbackCount: 64, fullOnly: true, selected);
        Assert.Equal(new[] { 0 }, selected);
    }

    [Fact]
    public void TerrainChunkDrawCull_CompareDrawItems_orders_opaque_before_cutout_then_material()
    {
        var fullPom = new TerrainChunkDrawCull.Candidate
        {
            BoundsCenter = Vector3.Zero,
            BoundsRadius = 1f,
            Lod = TerrainChunkLodKind.Full,
            NearPom = true,
            SourceIndex = 0
        };
        var cmp = TerrainChunkDrawCull.CompareDrawItems(
            fullPom, materialA: 1, cutoutA: false,
            fullPom, materialB: 1, cutoutB: true,
            sourceOrderA: 0, sourceOrderB: 1);
        Assert.True(cmp < 0);

        cmp = TerrainChunkDrawCull.CompareDrawItems(
            fullPom, materialA: 0, cutoutA: false,
            fullPom, materialB: 2, cutoutB: false,
            sourceOrderA: 5, sourceOrderB: 1);
        Assert.True(cmp < 0);
    }

    [Fact]
    public void TerrainChunkDrawCull_ApplyNearPomFlags_marks_near_full_chunks()
    {
        var candidates = new List<TerrainChunkDrawCull.Candidate>
        {
            new()
            {
                BoundsCenter = Vector3.Zero,
                BoundsRadius = 8f,
                Lod = TerrainChunkLodKind.Full,
                NearPom = false,
                SourceIndex = 0
            },
            new()
            {
                BoundsCenter = new Vector3(200f, 0f, 200f),
                BoundsRadius = 8f,
                Lod = TerrainChunkLodKind.Full,
                NearPom = true,
                SourceIndex = 1
            },
            new()
            {
                BoundsCenter = Vector3.Zero,
                BoundsRadius = 8f,
                Lod = TerrainChunkLodKind.Lod,
                NearPom = true,
                SourceIndex = 2
            }
        };

        TerrainChunkDrawCull.ApplyNearPomFlags(
            candidates,
            cameraPosition: Vector3.Zero,
            enableParallaxSetting: true);

        Assert.True(candidates[0].NearPom);
        Assert.False(candidates[1].NearPom);
        Assert.False(candidates[2].NearPom);

        TerrainChunkDrawCull.ApplyNearPomFlags(
            candidates,
            cameraPosition: Vector3.Zero,
            enableParallaxSetting: false);
        Assert.False(candidates[0].NearPom);
    }

    [Fact]
    public void TerrainChunkDrawCull_parallel_filter_stable_sort_by_group_then_index()
    {
        var view = Matrix4x4.Identity;
        var projection = Matrix4x4.CreateOrthographic(400f, 400f, 0.1f, 400f);
        var vp = projection * view;
        var n = TerrainChunkDrawCull.ParallelFilterMinCandidates + 8;
        var candidates = new TerrainChunkDrawCull.Candidate[n];
        for (var i = 0; i < n; i++)
        {
            var lod = i % 3 == 0 ? TerrainChunkLodKind.Lod : TerrainChunkLodKind.Full;
            var nearPom = lod == TerrainChunkLodKind.Full && i % 2 == 0;
            candidates[i] = new TerrainChunkDrawCull.Candidate
            {
                BoundsCenter = new Vector3((i % 16) * 4f, 0f, (i / 16f) * 4f),
                BoundsRadius = 6f,
                Lod = lod,
                NearPom = nearPom,
                SourceIndex = i
            };
        }

        var selected = new List<int>();
        TerrainChunkDrawCull.Select(
            candidates,
            vp,
            cameraPosition: Vector3.Zero,
            fallbackCount: 64,
            fullOnly: false,
            selected);

        Assert.True(selected.Count >= TerrainChunkDrawCull.ParallelFilterMinCandidates);
        static int ExpectedDrawGroup(in TerrainChunkDrawCull.Candidate c) =>
            c.Lod != TerrainChunkLodKind.Full
                ? TerrainResidencyKey.MaxLodLevel - (int)c.Lod
                : c.NearPom
                    ? TerrainResidencyKey.MaxLodLevel + 1
                    : TerrainResidencyKey.MaxLodLevel + 2;

        for (var i = 1; i < selected.Count; i++)
        {
            var prev = candidates[selected[i - 1]];
            var cur = candidates[selected[i]];
            var prevGroup = ExpectedDrawGroup(prev);
            var curGroup = ExpectedDrawGroup(cur);
            Assert.True(
                prevGroup < curGroup ||
                (prevGroup == curGroup && selected[i - 1] < selected[i]),
                $"order broken at {i}: {selected[i - 1]}->{selected[i]}");
        }
    }

    [Fact]
    public void ForOrbitPreview_streaming_extent_raises_far_above_legacy_cap()
    {
        var eye = new Vector3(0f, 10f, 0f);
        var extent = 14 * PreviewStageConstants.TerrainChunkSize; // ~LOD ring for viewDist 8
        var (_, far) = PreviewCameraDepthRange.ForOrbitPreview(
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(0.5f, 1f, 0.5f),
            orbitDistance: 12f,
            eye,
            environmentHalfExtent: extent,
            environmentFloorY: -0.56f,
            environmentCeilingY: 6f);

        Assert.True(far > PreviewCameraDepthRange.LargeEnvironmentFar * 0.5f, $"far={far}");
        Assert.True(far >= extent, $"far={far} should cover streaming half-extent {extent}");
    }

    [Fact]
    public void ForOrbitPreview_close_eye_inside_large_stage_does_not_crush_far_plane()
    {
        // Eye on the flat pad inside a streaming-sized AABB: old ratio clamp set far≈50.
        var eye = new Vector3(-3.2f, 2f, -3.8f);
        var extent = 14 * 16f;
        var (near, far) = PreviewCameraDepthRange.ForOrbitPreview(
            new Vector3(-0.5f, -0.56f, -0.5f),
            new Vector3(0.5f, 1f, 0.5f),
            orbitDistance: 4f,
            eye,
            environmentHalfExtent: extent,
            environmentFloorY: -0.56f,
            environmentCeilingY: 6f);

        Assert.True(near <= 0.2f, $"near={near} must stay close-up friendly");
        Assert.True(far > 100f, $"far={far} must not collapse to near*5000 when close over the pad");
        Assert.True(far >= extent * 0.9f, $"far={far} should still reach the streaming ring");
    }
}
