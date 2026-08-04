using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.Preview;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Distant Horizons / Voxy–style combined LOD: stepped column silhouette (tops + vertical steps)
/// as one aggressively greedy-merged mesh per section. No fill-depth underground solids.
/// Vegetation is block-space 1:1 with Full (same 1 m roots, trunks, canopy blocks, model stamps).
/// Allowed LOD cost levers are render-side only: texture mips, transparency/alpha policy,
/// and shadow caster distance — never drop or simplify vegetation voxels for budget.
/// </summary>
public static class PreviewTerrainLodMeshBaker
{
    /// <summary>
    /// Single-chunk 1 m LOD silhouette for material/compat tests. Streaming uses
    /// <see cref="BakeLodSection"/>.
    /// </summary>
    public static PreviewTerrainChunkMesh? BakeLodChunk(
        TerrainChunkKey key,
        PreviewTerrainWorldGenSettings worldGen = default,
        PreviewTerrainGrassBakeSettings grassSettings = default,
        PreviewTerrainVegetationBakePlan? vegetation = null,
        int chunkSize = PreviewStageConstants.TerrainChunkSize,
        float metersPerTile = PreviewStageConstants.MetersPerGrassTile,
        float surfaceWorldY = PreviewStageConstants.GroundPlaneWorldY,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks,
        int maxRelief = PreviewStageConstants.TerrainMaxReliefBlocks,
        int seed = PreviewStageConstants.TerrainHeightSeed) =>
        BakeLodRegion(
            TerrainResidencyKey.Section(key.X, key.Z, 1),
            worldOriginX: key.OriginX(chunkSize),
            worldOriginZ: key.OriginZ(chunkSize),
            worldSize: Math.Max(1, chunkSize),
            sampleStep: 1,
            emitVegetation: true,
            worldGen,
            grassSettings,
            vegetation,
            metersPerTile,
            surfaceWorldY,
            flatPadHalfExtent,
            transitionBlocks,
            maxRelief,
            seed);

    public static PreviewTerrainChunkMesh? BakeLodSection(
        TerrainResidencyKey key,
        PreviewTerrainWorldGenSettings worldGen = default,
        PreviewTerrainGrassBakeSettings grassSettings = default,
        PreviewTerrainVegetationBakePlan? vegetation = null,
        int chunkSize = PreviewStageConstants.TerrainChunkSize,
        float metersPerTile = PreviewStageConstants.MetersPerGrassTile,
        float surfaceWorldY = PreviewStageConstants.GroundPlaneWorldY,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks,
        int maxRelief = PreviewStageConstants.TerrainMaxReliefBlocks,
        int seed = PreviewStageConstants.TerrainHeightSeed)
    {
        if (key.LodLevel == 0)
        {
            return null;
        }

        chunkSize = Math.Max(1, chunkSize);
        var worldSize = key.SectionWorldSize(chunkSize);
        var sampleStep = Math.Max(1, key.SampleStepMeters);
        return BakeLodRegion(
            key,
            key.OriginWorldX(chunkSize),
            key.OriginWorldZ(chunkSize),
            worldSize,
            sampleStep,
            emitVegetation: true,
            worldGen,
            grassSettings,
            vegetation,
            metersPerTile,
            surfaceWorldY,
            flatPadHalfExtent,
            transitionBlocks,
            maxRelief,
            seed);
    }

    private static PreviewTerrainChunkMesh? BakeLodRegion(
        TerrainResidencyKey key,
        int worldOriginX,
        int worldOriginZ,
        int worldSize,
        int sampleStep,
        bool emitVegetation,
        PreviewTerrainWorldGenSettings worldGen,
        PreviewTerrainGrassBakeSettings grassSettings,
        PreviewTerrainVegetationBakePlan? vegetation,
        float metersPerTile,
        float surfaceWorldY,
        int flatPadHalfExtent,
        int transitionBlocks,
        int maxRelief,
        int seed)
    {
        worldSize = Math.Max(1, worldSize);
        sampleStep = Math.Max(1, sampleStep);
        if (metersPerTile <= 1e-6f)
        {
            metersPerTile = PreviewStageConstants.MetersPerGrassTile;
        }

        if (grassSettings is
            {
                Mode: PreviewTerrainGrassMode.BuiltInSingleTop,
                BetterGrassEnabled: false,
                EmitOverlay: false,
                VegetationIdentity: ""
            })
        {
            grassSettings = PreviewTerrainGrassBakeSettings.BuiltIn;
        }

        var gen = worldGen.BiomeSize > 0f || worldGen.Amplification > 0f || worldGen.Seed != 0
            ? PreviewTerrainWorldGenSettings.Resolve(worldGen)
            : PreviewTerrainWorldGenSettings.Default with { Seed = seed };

        var cx0 = worldOriginX;
        var cz0 = worldOriginZ;
        var cx1 = cx0 + worldSize;
        var cz1 = cz0 + worldSize;
        var grid = worldSize / sampleStep;
        if (grid <= 0)
        {
            return null;
        }

        // Coarse core + 1-cell halo for step occlusion against neighbors.
        // Core cells use the max height in the sampleStep×sampleStep block so LOD hulls sit
        // above Full detail and edge skirts can hide residual cracks.
        _ = maxRelief;
        var side = grid + 2;
        var board = new PreviewTerrainColumnSample[side * side];
        var ox = cx0 - sampleStep;
        var oz = cz0 - sampleStep;
        var minH = int.MaxValue;
        var maxH = int.MinValue;
        for (var lz = 0; lz < side; lz++)
        {
            for (var lx = 0; lx < side; lx++)
            {
                var wx = ox + lx * sampleStep;
                var wz = oz + lz * sampleStep;
                var sample = SampleLodCell(wx, wz, sampleStep, gen, flatPadHalfExtent, transitionBlocks);
                board[lz * side + lx] = sample;
                if (lx > 0 && lx < side - 1 && lz > 0 && lz < side - 1)
                {
                    minH = Math.Min(minH, sample.Height);
                    maxH = Math.Max(maxH, sample.Height);
                }
            }
        }

        if (minH == int.MaxValue)
        {
            return null;
        }

        PreviewTerrainColumnSample ColumnAt(int x, int z)
        {
            // Snap to sample grid relative to halo origin.
            var lx = (x - ox) / sampleStep;
            var lz = (z - oz) / sampleStep;
            if ((uint)lx >= (uint)side || (uint)lz >= (uint)side)
            {
                return SampleLodCell(x, z, sampleStep, gen, flatPadHalfExtent, transitionBlocks);
            }

            return board[lz * side + lx];
        }

        int H(int wx, int wz) => ColumnAt(wx, wz).Height;

        var vegPlan = vegetation is { HasAny: true } ? vegetation : PreviewTerrainVegetationBakePlan.Empty;
        var slotCount = Math.Max(PreviewTerrainGrassSlots.MaxCount, vegPlan.TotalSlotCount);
        var buckets = PreviewTerrainMeshBaker.CreateMaterialBuckets(slotCount);
        EmitTopFaces(H, ColumnAt, grassSettings, cx0, cx1, cz0, cz1, sampleStep, surfaceWorldY, metersPerTile, buckets);
        EmitStepFacesX(H, ColumnAt, grassSettings, cx0, cx1, cz0, cz1, sampleStep, surfaceWorldY, metersPerTile, buckets);
        EmitStepFacesZ(H, ColumnAt, grassSettings, cx0, cx1, cz0, cz1, sampleStep, surfaceWorldY, metersPerTile, buckets);
        // Edge skirts hide Full↔LOD / LOD↔LOD cracks; skip on 1 m test helper meshes.
        if (sampleStep > 1)
        {
            var skirtDepth = Math.Max(
                PreviewStageConstants.TerrainLodEdgeSkirtMinBlocks,
                sampleStep * 2);
            EmitSectionSkirts(
                H,
                ColumnAt,
                grassSettings,
                cx0, cx1, cz0, cz1,
                sampleStep,
                skirtDepth,
                surfaceWorldY,
                metersPerTile,
                buckets);
        }

        if (emitVegetation && vegPlan.HasAny && grassSettings.EmitVegetation)
        {
            // Exact 1 m columns match Full spawn decisions (density + positions). LOD hull
            // sampling alone would shift biome/surface and thin/shift trees at the fade.
            PreviewTerrainColumnSample ExactColumn(int x, int z) =>
                PreviewTerrainBiomeSampler.Sample(x, z, gen, flatPadHalfExtent, transitionBlocks);

            var placements = PreviewTerrainTreePlacer.CollectForChunk(
                cx0,
                cz0,
                cx1,
                cz1,
                ExactColumn,
                gen,
                vegPlan,
                flatPadHalfExtent,
                placementStep: 1);
            PreviewTerrainTreeMeshEmitter.EmitPlacements(
                placements,
                surfaceWorldY,
                metersPerTile,
                buckets,
                ref maxH,
                vegPlan.ModelTemplates);
        }

        if (!PreviewTerrainMeshBaker.TryConcatMaterialBuckets(buckets, out var verts, out var indices, out var batches) ||
            indices.Length == 0)
        {
            return null;
        }

        var minY = surfaceWorldY + minH - 1;
        var maxY = surfaceWorldY + maxH;
        var boundsMin = new Vector3(cx0, minY, cz0);
        var boundsMax = new Vector3(cx1, maxY, cz1);
        var center = (boundsMin + boundsMax) * 0.5f;
        return new PreviewTerrainChunkMesh
        {
            Key = key,
            Lod = key.Kind,
            InterleavedVertices = verts,
            Indices = indices,
            DrawBatches = batches,
            BoundsCenter = center,
            BoundsRadius = Vector3.Distance(center, boundsMax),
            MinRelativeHeight = minH,
            MaxRelativeHeight = maxH
        };
    }

    private static PreviewTerrainColumnSample SampleLodCell(
        int wx,
        int wz,
        int sampleStep,
        in PreviewTerrainWorldGenSettings gen,
        int flatPadHalfExtent,
        int transitionBlocks)
    {
        if (sampleStep <= 1)
        {
            return PreviewTerrainBiomeSampler.Sample(wx, wz, gen, flatPadHalfExtent, transitionBlocks);
        }

        // Max-height hull over the coarse cell; keep material from the highest column.
        var best = PreviewTerrainBiomeSampler.Sample(wx, wz, gen, flatPadHalfExtent, transitionBlocks);
        for (var dz = 0; dz < sampleStep; dz++)
        {
            for (var dx = 0; dx < sampleStep; dx++)
            {
                if (dx == 0 && dz == 0)
                {
                    continue;
                }

                var s = PreviewTerrainBiomeSampler.Sample(
                    wx + dx, wz + dz, gen, flatPadHalfExtent, transitionBlocks);
                if (s.Height > best.Height)
                {
                    best = s;
                }
            }
        }

        return best;
    }

    private static void EmitSectionSkirts(
        Func<int, int, int> h,
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        PreviewTerrainGrassBakeSettings grassSettings,
        int cx0, int cx1, int cz0, int cz1,
        int sampleStep,
        int skirtDepth,
        float surfaceWorldY,
        float metersPerTile,
        List<float>[] buckets)
    {
        skirtDepth = Math.Max(1, skirtDepth);
        Span<Vector3> face = stackalloc Vector3[4];

        // -Z and +Z edges.
        for (var x = cx0; x < cx1; x += sampleStep)
        {
            var x1 = Math.Min(x + sampleStep, cx1);
            var hSouth = h(x, cz0);
            var matSouth = PreviewTerrainMeshBaker.ResolveHorizontalFaceMaterial(
                columnAt, x, hSouth, cz0, x, cz0 - sampleStep, grassSettings);
            EmitSkirtQuad(
                face, -Vector3.UnitZ, -Vector3.UnitX,
                x, x1, cz0, cz0,
                surfaceWorldY + hSouth, surfaceWorldY + hSouth - skirtDepth,
                metersPerTile, buckets[matSouth]);

            var hNorth = h(x, cz1 - sampleStep);
            var matNorth = PreviewTerrainMeshBaker.ResolveHorizontalFaceMaterial(
                columnAt, x, hNorth, cz1 - sampleStep, x, cz1, grassSettings);
            EmitSkirtQuad(
                face, Vector3.UnitZ, Vector3.UnitX,
                x, x1, cz1, cz1,
                surfaceWorldY + hNorth, surfaceWorldY + hNorth - skirtDepth,
                metersPerTile, buckets[matNorth]);
        }

        // -X and +X edges.
        for (var z = cz0; z < cz1; z += sampleStep)
        {
            var z1 = Math.Min(z + sampleStep, cz1);
            var hWest = h(cx0, z);
            var matWest = PreviewTerrainMeshBaker.ResolveHorizontalFaceMaterial(
                columnAt, cx0, hWest, z, cx0 - sampleStep, z, grassSettings);
            EmitSkirtQuad(
                face, -Vector3.UnitX, new Vector3(0, 0, 1),
                cx0, cx0, z, z1,
                surfaceWorldY + hWest, surfaceWorldY + hWest - skirtDepth,
                metersPerTile, buckets[matWest]);

            var hEast = h(cx1 - sampleStep, z);
            var matEast = PreviewTerrainMeshBaker.ResolveHorizontalFaceMaterial(
                columnAt, cx1 - sampleStep, hEast, z, cx1, z, grassSettings);
            EmitSkirtQuad(
                face, Vector3.UnitX, new Vector3(0, 0, -1),
                cx1, cx1, z, z1,
                surfaceWorldY + hEast, surfaceWorldY + hEast - skirtDepth,
                metersPerTile, buckets[matEast]);
        }
    }

    private static void EmitSkirtQuad(
        Span<Vector3> face,
        Vector3 normal,
        Vector3 tangentHint,
        float x0, float x1, float z0, float z1,
        float yTop, float yBot,
        float metersPerTile,
        List<float> bucket)
    {
        if (yTop <= yBot)
        {
            return;
        }

        if (Math.Abs(normal.X) > 0.5f)
        {
            var x = normal.X > 0f ? x1 : x0;
            if (normal.X > 0f)
            {
                face[0] = new(x, yBot, z1);
                face[1] = new(x, yBot, z0);
                face[2] = new(x, yTop, z0);
                face[3] = new(x, yTop, z1);
            }
            else
            {
                face[0] = new(x, yBot, z0);
                face[1] = new(x, yBot, z1);
                face[2] = new(x, yTop, z1);
                face[3] = new(x, yTop, z0);
            }
        }
        else
        {
            var z = normal.Z > 0f ? z1 : z0;
            if (normal.Z > 0f)
            {
                face[0] = new(x0, yBot, z);
                face[1] = new(x1, yBot, z);
                face[2] = new(x1, yTop, z);
                face[3] = new(x0, yTop, z);
            }
            else
            {
                face[0] = new(x1, yBot, z);
                face[1] = new(x0, yBot, z);
                face[2] = new(x0, yTop, z);
                face[3] = new(x1, yTop, z);
            }
        }

        EmitQuad(normal, tangentHint, 1f, face, metersPerTile, topUv: false, bucket);
    }

    private static void EmitTopFaces(
        Func<int, int, int> h,
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        PreviewTerrainGrassBakeSettings grassSettings,
        int cx0, int cx1, int cz0, int cz1,
        int sampleStep,
        float surfaceWorldY, float metersPerTile,
        List<float>[] buckets)
    {
        var uSize = (cx1 - cx0) / sampleStep;
        var vSize = (cz1 - cz0) / sampleStep;
        var visited = new bool[uSize * vSize];
        Span<Vector3> top = stackalloc Vector3[4];
        for (var v = 0; v < vSize; v++)
        {
            for (var u = 0; u < uSize; u++)
            {
                var idx = v * uSize + u;
                if (visited[idx])
                {
                    continue;
                }

                var wx = cx0 + u * sampleStep;
                var wz = cz0 + v * sampleStep;
                var height = h(wx, wz);
                var material = PreviewTerrainMeshBaker.ResolveYFaceMaterial(
                    positiveUp: true, columnAt(wx, wz), grassSettings);
                var width = 1;
                while (u + width < uSize &&
                       !visited[v * uSize + u + width] &&
                       h(cx0 + (u + width) * sampleStep, cz0 + v * sampleStep) == height &&
                       PreviewTerrainMeshBaker.ResolveYFaceMaterial(
                           positiveUp: true,
                           columnAt(cx0 + (u + width) * sampleStep, cz0 + v * sampleStep),
                           grassSettings) == material)
                {
                    width++;
                }

                var runH = 1;
                var done = false;
                while (v + runH < vSize && !done)
                {
                    for (var k = 0; k < width; k++)
                    {
                        var nx = cx0 + (u + k) * sampleStep;
                        var nz = cz0 + (v + runH) * sampleStep;
                        if (visited[(v + runH) * uSize + u + k] ||
                            h(nx, nz) != height ||
                            PreviewTerrainMeshBaker.ResolveYFaceMaterial(
                                positiveUp: true, columnAt(nx, nz), grassSettings) != material)
                        {
                            done = true;
                            break;
                        }
                    }

                    if (!done)
                    {
                        runH++;
                    }
                }

                for (var dv = 0; dv < runH; dv++)
                {
                    for (var du = 0; du < width; du++)
                    {
                        visited[(v + dv) * uSize + u + du] = true;
                    }
                }

                var x0 = cx0 + u * sampleStep;
                var z0 = cz0 + v * sampleStep;
                var x1 = x0 + width * sampleStep;
                var z1 = z0 + runH * sampleStep;
                var y = surfaceWorldY + height;
                top[0] = new(x0, y, z0);
                top[1] = new(x1, y, z0);
                top[2] = new(x1, y, z1);
                top[3] = new(x0, y, z1);
                EmitQuad(Vector3.UnitY, Vector3.UnitX, 1f, top, metersPerTile, topUv: true, buckets[material]);
            }
        }
    }

    private static void EmitStepFacesX(
        Func<int, int, int> h,
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        PreviewTerrainGrassBakeSettings grassSettings,
        int cx0, int cx1, int cz0, int cz1,
        int sampleStep,
        float surfaceWorldY, float metersPerTile,
        List<float>[] buckets)
    {
        Span<Vector3> face = stackalloc Vector3[4];
        for (var x = cx0; x <= cx1; x += sampleStep)
        {
            for (var z = cz0; z < cz1;)
            {
                var left = h(x - sampleStep, z);
                var right = h(x, z);
                if (left == right)
                {
                    z += sampleStep;
                    continue;
                }

                var positive = left > right;
                var hi = Math.Max(left, right);
                var lo = Math.Min(left, right);
                var material = ResolveStepMaterial(
                    columnAt,
                    grassSettings,
                    hiColX: positive ? x - sampleStep : x,
                    hiColZ: z,
                    neighborX: positive ? x : x - sampleStep,
                    neighborZ: z);
                var run = sampleStep;
                while (z + run < cz1)
                {
                    var l2 = h(x - sampleStep, z + run);
                    var r2 = h(x, z + run);
                    if (Math.Max(l2, r2) != hi || Math.Min(l2, r2) != lo || (l2 > r2) != positive)
                    {
                        break;
                    }

                    var mat2 = ResolveStepMaterial(
                        columnAt,
                        grassSettings,
                        hiColX: positive ? x - sampleStep : x,
                        hiColZ: z + run,
                        neighborX: positive ? x : x - sampleStep,
                        neighborZ: z + run);
                    if (mat2 != material)
                    {
                        break;
                    }

                    run += sampleStep;
                }

                var coreTouches = (x > cx0 && x <= cx1) || (x >= cx0 && x < cx1);
                if (coreTouches && hi > lo)
                {
                    var y0 = surfaceWorldY + lo;
                    var y1 = surfaceWorldY + hi;
                    var z0 = z;
                    var z1 = z + run;
                    var xf = (float)x;
                    if (positive)
                    {
                        face[0] = new(xf, y0, z1);
                        face[1] = new(xf, y0, z0);
                        face[2] = new(xf, y1, z0);
                        face[3] = new(xf, y1, z1);
                        EmitQuad(Vector3.UnitX, new Vector3(0, 0, -1), 1f, face, metersPerTile, topUv: false, buckets[material]);
                    }
                    else
                    {
                        face[0] = new(xf, y0, z0);
                        face[1] = new(xf, y0, z1);
                        face[2] = new(xf, y1, z1);
                        face[3] = new(xf, y1, z0);
                        EmitQuad(-Vector3.UnitX, new Vector3(0, 0, 1), 1f, face, metersPerTile, topUv: false, buckets[material]);
                    }

                    if (grassSettings.EmitOverlay && material == PreviewTerrainGrassSlots.Side)
                    {
                        EmitQuad(
                            positive ? Vector3.UnitX : -Vector3.UnitX,
                            positive ? new Vector3(0, 0, -1) : new Vector3(0, 0, 1),
                            1f,
                            face,
                            metersPerTile,
                            topUv: false,
                            buckets[PreviewTerrainGrassSlots.Overlay]);
                    }
                }

                z += run;
            }
        }
    }

    private static void EmitStepFacesZ(
        Func<int, int, int> h,
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        PreviewTerrainGrassBakeSettings grassSettings,
        int cx0, int cx1, int cz0, int cz1,
        int sampleStep,
        float surfaceWorldY, float metersPerTile,
        List<float>[] buckets)
    {
        Span<Vector3> face = stackalloc Vector3[4];
        for (var z = cz0; z <= cz1; z += sampleStep)
        {
            for (var x = cx0; x < cx1;)
            {
                var back = h(x, z - sampleStep);
                var fwd = h(x, z);
                if (back == fwd)
                {
                    x += sampleStep;
                    continue;
                }

                var positive = back > fwd;
                var hi = Math.Max(back, fwd);
                var lo = Math.Min(back, fwd);
                var material = ResolveStepMaterial(
                    columnAt,
                    grassSettings,
                    hiColX: x,
                    hiColZ: positive ? z - sampleStep : z,
                    neighborX: x,
                    neighborZ: positive ? z : z - sampleStep);
                var run = sampleStep;
                while (x + run < cx1)
                {
                    var b2 = h(x + run, z - sampleStep);
                    var f2 = h(x + run, z);
                    if (Math.Max(b2, f2) != hi || Math.Min(b2, f2) != lo || (b2 > f2) != positive)
                    {
                        break;
                    }

                    var mat2 = ResolveStepMaterial(
                        columnAt,
                        grassSettings,
                        hiColX: x + run,
                        hiColZ: positive ? z - sampleStep : z,
                        neighborX: x + run,
                        neighborZ: positive ? z : z - sampleStep);
                    if (mat2 != material)
                    {
                        break;
                    }

                    run += sampleStep;
                }

                var coreTouches = (z > cz0 && z <= cz1) || (z >= cz0 && z < cz1);
                if (coreTouches && hi > lo)
                {
                    var y0 = surfaceWorldY + lo;
                    var y1 = surfaceWorldY + hi;
                    var x0 = x;
                    var x1 = x + run;
                    var zf = (float)z;
                    if (positive)
                    {
                        face[0] = new(x0, y0, zf);
                        face[1] = new(x1, y0, zf);
                        face[2] = new(x1, y1, zf);
                        face[3] = new(x0, y1, zf);
                        EmitQuad(Vector3.UnitZ, Vector3.UnitX, 1f, face, metersPerTile, topUv: false, buckets[material]);
                    }
                    else
                    {
                        face[0] = new(x1, y0, zf);
                        face[1] = new(x0, y0, zf);
                        face[2] = new(x0, y1, zf);
                        face[3] = new(x1, y1, zf);
                        EmitQuad(-Vector3.UnitZ, -Vector3.UnitX, 1f, face, metersPerTile, topUv: false, buckets[material]);
                    }

                    if (grassSettings.EmitOverlay && material == PreviewTerrainGrassSlots.Side)
                    {
                        EmitQuad(
                            positive ? Vector3.UnitZ : -Vector3.UnitZ,
                            positive ? Vector3.UnitX : -Vector3.UnitX,
                            1f,
                            face,
                            metersPerTile,
                            topUv: false,
                            buckets[PreviewTerrainGrassSlots.Overlay]);
                    }
                }

                x += run;
            }
        }
    }

    private static int ResolveStepMaterial(
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        PreviewTerrainGrassBakeSettings grassSettings,
        int hiColX,
        int hiColZ,
        int neighborX,
        int neighborZ)
    {
        var hi = columnAt(hiColX, hiColZ).Height;
        return PreviewTerrainMeshBaker.ResolveHorizontalFaceMaterial(
            columnAt, hiColX, hi, hiColZ, neighborX, neighborZ, grassSettings);
    }

    private static void EmitQuad(
        Vector3 normal,
        Vector3 fallbackTangent,
        float fallbackWSign,
        ReadOnlySpan<Vector3> cornersIn,
        float metersPerTile,
        bool topUv,
        List<float> verts)
    {
        Span<Vector3> corners = stackalloc Vector3[4];
        Span<Vector2> uvs = stackalloc Vector2[4];
        cornersIn.CopyTo(corners);
        for (var i = 0; i < 4; i++)
        {
            uvs[i] = topUv
                ? new Vector2(corners[i].X / metersPerTile, corners[i].Z / metersPerTile)
                : new Vector2(
                    (Math.Abs(normal.X) > 0.5f ? corners[i].Z : corners[i].X) / metersPerTile,
                    corners[i].Y / metersPerTile);
        }

        var geo = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
        if (Vector3.Dot(geo, normal) < 0f)
        {
            (corners[1], corners[3]) = (corners[3], corners[1]);
            (uvs[1], uvs[3]) = (uvs[3], uvs[1]);
        }

        PreviewTangentBasis.Derive(corners, uvs, normal, fallbackTangent, fallbackWSign, out var tangent, out var wSign);
        for (var i = 0; i < 4; i++)
        {
            var p = corners[i];
            var uv = uvs[i];
            verts.Add(p.X);
            verts.Add(p.Y);
            verts.Add(p.Z);
            verts.Add(normal.X);
            verts.Add(normal.Y);
            verts.Add(normal.Z);
            verts.Add(uv.X);
            verts.Add(uv.Y);
            verts.Add(tangent.X);
            verts.Add(tangent.Y);
            verts.Add(tangent.Z);
            verts.Add(wSign);
        }
    }
}
