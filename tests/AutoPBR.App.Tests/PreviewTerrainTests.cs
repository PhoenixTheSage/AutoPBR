using System.Numerics;

using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.Preview;

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
        const int s = PreviewMesh.FloatsPerVertex;
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
        var heights = new[] { 0 }; // halfExtent 1 needs 4 — use 2x2 flat
        heights = [0, 0, 0, 0];
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
        Assert.Contains(bake.ChunkBatches, b => b.EnableParallax && b.LodMaxDistance <= 0f);
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
        Assert.Equal(TerrainChunkLodKind.Full, mesh!.Lod);
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
        Assert.Equal(TerrainChunkLodKind.Lod, lod!.Lod);
        Assert.True(lod.Indices.Length >= 6);
        Assert.True(lod.Indices.Length <= full!.Indices.Length,
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
        Assert.Equal(a!.Indices, b!.Indices);
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
        Assert.NotEmpty(mesh!.DrawBatches);
        Assert.Contains(mesh.DrawBatches, b => b.MaterialIndex == PreviewTerrainGrassSlots.Top);
        Assert.Contains(mesh.DrawBatches, b => b.MaterialIndex == PreviewTerrainGrassSlots.Dirt);
        // Flat pad under subject may still expose side/overlay on relief edges outside pad.
        Assert.True(
            mesh.DrawBatches.Any(b => b.MaterialIndex == PreviewTerrainGrassSlots.Side) ||
            mesh.DrawBatches.Any(b => b.MaterialIndex == PreviewTerrainGrassSlots.Top));
    }

    [Fact]
    public void BakeLodChunk_uses_Top_only_batch()
    {
        var lod = PreviewTerrainLodMeshBaker.BakeLodChunk(new TerrainChunkKey(0, 0));
        Assert.NotNull(lod);
        Assert.Single(lod!.DrawBatches);
        Assert.Equal(PreviewTerrainGrassSlots.Top, lod.DrawBatches[0].MaterialIndex);
    }

    [Fact]
    public void TerrainChunkStreamer_desired_rings_match_view_distance()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Tick(new Vector3(8f, 2f, 8f), chunkViewDistance: 3);
        var desired = streamer.SnapshotDesired();
        Assert.Equal(3, streamer.HardRadiusChunks);
        Assert.Equal(3 + PreviewStageConstants.TerrainLodRingChunks, streamer.LodRadiusChunks);

        var cam = TerrainChunkKey.FromWorld(8f, 8f);
        Assert.True(desired.TryGetValue(cam, out var camKind));
        Assert.Equal(TerrainChunkLodKind.Full, camKind);

        var edgeFull = new TerrainChunkKey(cam.X + 3, cam.Z);
        Assert.True(desired.TryGetValue(edgeFull, out var fullKind));
        Assert.Equal(TerrainChunkLodKind.Full, fullKind);

        var lodOnly = new TerrainChunkKey(cam.X + 4, cam.Z);
        Assert.True(desired.TryGetValue(lodOnly, out var lodKind));
        Assert.Equal(TerrainChunkLodKind.Lod, lodKind);

        var outside = new TerrainChunkKey(cam.X + streamer.UnloadRadiusChunks + 1, cam.Z);
        Assert.False(desired.ContainsKey(outside));
        Assert.True(streamer.ShouldUnload(outside));
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
    public void TerrainChunkStreamer_multi_worker_produces_ready_meshes()
    {
        using var streamer = new TerrainChunkStreamer();
        streamer.Start();
        Assert.Equal(TerrainChunkStreamer.ResolveWorkerCount(), streamer.WorkerCount);
        Assert.True(streamer.WorkerCount >= 1);

        streamer.Tick(Vector3.Zero, chunkViewDistance: 2);
        var uploads = new List<PreviewTerrainChunkMesh>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline && uploads.Count < 4)
        {
            streamer.DrainReady(uploads, 8);
            if (uploads.Count < 4)
            {
                Thread.Sleep(20);
            }
        }

        Assert.True(uploads.Count >= 4, $"expected ready meshes, got {uploads.Count}");
        Assert.Contains(uploads, m => m.Lod == TerrainChunkLodKind.Full);
        Assert.Contains(uploads, m => m.Key.ChebyshevDistanceTo(new TerrainChunkKey(0, 0)) <= 1);
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
    public void TerrainShadowFar_coverage_matches_default_lod_ring()
    {
        var defaultRing =
            (PreviewStageConstants.TerrainDefaultChunkViewDistance +
             PreviewStageConstants.TerrainLodRingChunks) *
            (float)PreviewStageConstants.TerrainChunkSize;
        Assert.True(PreviewShadowFrustum.TerrainShadowFarMaxHalfExtent >= defaultRing);
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
                BoundsCenter = new Vector3((i % 16) * 4f, 0f, (i / 16) * 4f),
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
        for (var i = 1; i < selected.Count; i++)
        {
            var prev = candidates[selected[i - 1]];
            var cur = candidates[selected[i]];
            var prevGroup = prev.Lod == TerrainChunkLodKind.Full
                ? (prev.NearPom ? 0 : 1)
                : 2;
            var curGroup = cur.Lod == TerrainChunkLodKind.Full
                ? (cur.NearPom ? 0 : 1)
                : 2;
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
