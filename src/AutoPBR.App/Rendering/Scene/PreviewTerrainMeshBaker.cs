using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Builds Minecraft-style 1m cuboid terrain from column heights with neighbor face occlusion,
/// greedy coplanar merges, and world-tiled UVs. Supports legacy multi-chunk bake and streaming
/// per-chunk Full bakes against infinite <see cref="PreviewTerrainHeightfield.SampleColumn(int,int,in PreviewTerrainWorldGenSettings,int,int,int)"/>.
/// </summary>
public static class PreviewTerrainMeshBaker
{
    public static PreviewTerrainBakeResult Bake(
        ReadOnlySpan<int> heights,
        int halfExtent = PreviewStageConstants.TerrainHalfExtent,
        int fillDepth = PreviewStageConstants.TerrainFillDepth,
        int chunkSize = PreviewStageConstants.TerrainChunkSize,
        float metersPerTile = PreviewStageConstants.MetersPerGrassTile,
        float surfaceWorldY = PreviewStageConstants.GroundPlaneWorldY,
        float nearPomRadius = PreviewStageConstants.TerrainNearPomRadius,
        float lodMaxDistance = PreviewStageConstants.TerrainLodMaxDistance,
        string name = "preview_terrain")
    {
        if (halfExtent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(halfExtent));
        }

        var side = halfExtent * 2;
        if (heights.Length < side * side)
        {
            throw new ArgumentException("Heightfield is smaller than 2*halfExtent squared.", nameof(heights));
        }

        fillDepth = Math.Max(1, fillDepth);
        chunkSize = Math.Max(1, chunkSize);
        if (metersPerTile <= 1e-6f)
        {
            metersPerTile = PreviewStageConstants.MetersPerGrassTile;
        }

        var minH = int.MaxValue;
        var maxH = int.MinValue;
        var heightCopy = heights.ToArray();
        for (var i = 0; i < side * side; i++)
        {
            var h = heightCopy[i];
            minH = Math.Min(minH, h);
            maxH = Math.Max(maxH, h);
        }

        if (minH == int.MaxValue)
        {
            minH = 0;
            maxH = 0;
        }

        var layerMin = ResolveLayerMin(minH, fillDepth);
        var layerMax = maxH;
        int HeightAt(int x, int z) => PreviewTerrainHeightfield.GetHeight(heightCopy, x, z, halfExtent);

        var buckets = CreateMaterialBuckets();
        var batches = new List<PreviewDrawBatch>();
        var allVerts = new List<float>(side * side * 24);
        var allIndices = new List<uint>(side * side * 36);

        for (var cz0 = -halfExtent; cz0 < halfExtent; cz0 += chunkSize)
        {
            for (var cx0 = -halfExtent; cx0 < halfExtent; cx0 += chunkSize)
            {
                var cx1 = Math.Min(cx0 + chunkSize, halfExtent);
                var cz1 = Math.Min(cz0 + chunkSize, halfExtent);
                foreach (var bucket in buckets)
                {
                    bucket.Clear();
                }

                PreviewTerrainColumnSample ColumnAt(int x, int z)
                {
                    var h = HeightAt(x, z);
                    return new PreviewTerrainColumnSample(
                        h,
                        PreviewTerrainBiomeId.Plains,
                        PreviewTerrainBlockKind.Grass,
                        PreviewTerrainBlockKind.Dirt,
                        PreviewTerrainBlockKind.Stone);
                }

                EmitChunkGreedy(
                    HeightAt,
                    ColumnAt,
                    fillDepth,
                    layerMin,
                    layerMax,
                    cx0, cx1, cz0, cz1,
                    surfaceWorldY,
                    metersPerTile,
                    PreviewTerrainGrassBakeSettings.BuiltIn,
                    buckets);

                if (!TryConcatMaterialBuckets(buckets, out var chunkVerts, out var chunkIndices, out _) ||
                    chunkIndices.Length == 0)
                {
                    continue;
                }

                var indexStart = allIndices.Count;
                var baseVertex = (uint)(allVerts.Count / PreviewMesh.FloatsPerVertex);
                allVerts.AddRange(chunkVerts);
                foreach (var idx in chunkIndices)
                {
                    allIndices.Add(baseVertex + idx);
                }

                var indexCount = allIndices.Count - indexStart;
                var centerX = (cx0 + cx1) * 0.5f;
                var centerZ = (cz0 + cz1) * 0.5f;
                var padXz = Math.Max(Math.Abs(centerX), Math.Abs(centerZ));
                var enablePom = padXz <= nearPomRadius;
                var lod = lodMaxDistance <= 0f || padXz <= nearPomRadius ? 0f : lodMaxDistance;
                var minY = surfaceWorldY + layerMin - 1;
                var maxY = surfaceWorldY + layerMax;
                var boundsMin = new Vector3(cx0, minY, cz0);
                var boundsMax = new Vector3(cx1, maxY, cz1);
                var center = (boundsMin + boundsMax) * 0.5f;
                var radius = Vector3.Distance(center, boundsMax);

                batches.Add(new PreviewDrawBatch(indexStart, indexCount, MaterialIndex: 0)
                {
                    EnableParallax = enablePom,
                    BoundsCenter = center,
                    BoundsRadius = radius,
                    LodMaxDistance = lod
                });
            }
        }

        return new PreviewTerrainBakeResult
        {
            Mesh = new PreviewMesh
            {
                Name = name,
                InterleavedVertices = [.. allVerts],
                Indices = [.. allIndices]
            },
            ChunkBatches = [.. batches],
            MinRelativeHeight = layerMin,
            MaxRelativeHeight = maxH
        };
    }

    /// <summary>
    /// Full-detail greedy bake for one streaming chunk. Neighbor occlusion uses infinite
    /// <see cref="PreviewTerrainHeightfield.SampleColumn(int,int,in PreviewTerrainWorldGenSettings,int,int,int)"/> (1-column halo, no resident neighbors required).
    /// </summary>
    public static PreviewTerrainChunkMesh? BakeFullChunk(
        TerrainChunkKey key,
        PreviewTerrainGrassBakeSettings grassSettings = default,
        PreviewTerrainWorldGenSettings worldGen = default,
        PreviewTerrainVegetationBakePlan? vegetation = null,
        int chunkSize = PreviewStageConstants.TerrainChunkSize,
        int fillDepth = PreviewStageConstants.TerrainFillDepth,
        float metersPerTile = PreviewStageConstants.MetersPerGrassTile,
        float surfaceWorldY = PreviewStageConstants.GroundPlaneWorldY,
        int flatPadHalfExtent = PreviewStageConstants.TerrainFlatPadHalfExtent,
        int transitionBlocks = PreviewStageConstants.TerrainTransitionBlocks,
        int maxRelief = PreviewStageConstants.TerrainMaxReliefBlocks,
        int seed = PreviewStageConstants.TerrainHeightSeed)
    {
        // default(grassSettings) looks like BuiltIn for Mode/flags (BuiltInSingleTop == 0).
        // Preserve an explicit VegetationIdentity so tree bakes are not silently disabled.
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

        chunkSize = Math.Max(1, chunkSize);
        fillDepth = Math.Max(1, fillDepth);
        if (metersPerTile <= 1e-6f)
        {
            metersPerTile = PreviewStageConstants.MetersPerGrassTile;
        }

        var cx0 = key.OriginX(chunkSize);
        var cz0 = key.OriginZ(chunkSize);
        var cx1 = cx0 + chunkSize;
        var cz1 = cz0 + chunkSize;

        // Cache halo column samples once. Live biome sampling inside IsSolid would be extremely hot.
        _ = maxRelief;
        var side = chunkSize + 2;
        var board = new PreviewTerrainColumnSample[side * side];
        var ox = cx0 - 1;
        var oz = cz0 - 1;
        var minH = int.MaxValue;
        var maxH = int.MinValue;
        for (var lz = 0; lz < side; lz++)
        {
            for (var lx = 0; lx < side; lx++)
            {
                var sample = PreviewTerrainBiomeSampler.Sample(
                    ox + lx, oz + lz, gen, flatPadHalfExtent, transitionBlocks);
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

        var layerMin = ResolveLayerMin(minH, fillDepth);
        var layerMax = maxH;
        PreviewTerrainColumnSample ColumnAt(int x, int z)
        {
            var lx = x - ox;
            var lz = z - oz;
            if ((uint)lx >= (uint)side || (uint)lz >= (uint)side)
            {
                return PreviewTerrainBiomeSampler.Sample(
                    x, z, gen, flatPadHalfExtent, transitionBlocks);
            }

            return board[lz * side + lx];
        }

        int HeightAt(int x, int z) => ColumnAt(x, z).Height;

        var vegPlan = vegetation is { HasAny: true } ? vegetation : PreviewTerrainVegetationBakePlan.Empty;
        var slotCount = Math.Max(PreviewTerrainGrassSlots.MaxCount, vegPlan.TotalSlotCount);
        var buckets = CreateMaterialBuckets(slotCount);
        EmitChunkGreedy(
            HeightAt,
            ColumnAt,
            fillDepth,
            layerMin,
            layerMax,
            cx0, cx1, cz0, cz1,
            surfaceWorldY,
            metersPerTile,
            grassSettings,
            buckets);

        if (vegPlan.HasAny && grassSettings.EmitVegetation)
        {
            var placements = PreviewTerrainTreePlacer.CollectForChunk(
                cx0, cz0, cx1, cz1, ColumnAt, gen, vegPlan, flatPadHalfExtent);
            PreviewTerrainTreeMeshEmitter.EmitPlacements(
                placements,
                surfaceWorldY,
                metersPerTile,
                buckets,
                ref maxH,
                vegPlan.ModelTemplates);
        }

        if (!TryConcatMaterialBuckets(buckets, out var verts, out var indices, out var batches) ||
            indices.Length == 0)
        {
            return null;
        }

        var minY = surfaceWorldY + layerMin - 1;
        var maxY = surfaceWorldY + maxH;
        var boundsMin = new Vector3(cx0, minY, cz0);
        var boundsMax = new Vector3(cx1, maxY, cz1);
        var center = (boundsMin + boundsMax) * 0.5f;
        return new PreviewTerrainChunkMesh
        {
            Key = key,
            Lod = TerrainChunkLodKind.Full,
            InterleavedVertices = verts,
            Indices = indices,
            DrawBatches = batches,
            BoundsCenter = center,
            BoundsRadius = Vector3.Distance(center, boundsMax),
            MinRelativeHeight = layerMin,
            MaxRelativeHeight = maxH
        };
    }

    private static List<float>[] CreateMaterialBuckets(int slotCount = PreviewTerrainGrassSlots.MaxCount)
    {
        slotCount = Math.Max(PreviewTerrainGrassSlots.MaxCount, slotCount);
        var buckets = new List<float>[slotCount];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new List<float>(256);
        }

        return buckets;
    }

    private static bool TryConcatMaterialBuckets(
        List<float>[] buckets,
        out float[] verts,
        out uint[] indices,
        out PreviewDrawBatch[] batches)
    {
        var totalFloats = 0;
        foreach (var bucket in buckets)
        {
            totalFloats += bucket.Count;
        }

        if (totalFloats == 0)
        {
            verts = [];
            indices = [];
            batches = [];
            return false;
        }

        var vertList = new List<float>(totalFloats);
        var indexList = new List<uint>(totalFloats / PreviewMesh.FloatsPerVertex * 6 / 4);
        var batchList = new List<PreviewDrawBatch>(buckets.Length);
        for (var slot = 0; slot < buckets.Length; slot++)
        {
            var bucket = buckets[slot];
            if (bucket.Count == 0)
            {
                continue;
            }

            var vertexCount = bucket.Count / PreviewMesh.FloatsPerVertex;
            var indexStart = indexList.Count;
            var baseVertex = (uint)(vertList.Count / PreviewMesh.FloatsPerVertex);
            vertList.AddRange(bucket);
            for (var v = 0; v < vertexCount; v += 4)
            {
                var b = baseVertex + (uint)v;
                indexList.Add(b);
                indexList.Add(b + 1);
                indexList.Add(b + 2);
                indexList.Add(b);
                indexList.Add(b + 2);
                indexList.Add(b + 3);
            }

            batchList.Add(new PreviewDrawBatch(indexStart, indexList.Count - indexStart, slot));
        }

        verts = [.. vertList];
        indices = [.. indexList];
        batches = [.. batchList];
        return true;
    }

    private static void EmitChunkGreedy(
        Func<int, int, int> heightAt,
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        int fillDepth,
        int layerMin,
        int layerMax,
        int cx0,
        int cx1,
        int cz0,
        int cz1,
        float surfaceWorldY,
        float metersPerTile,
        PreviewTerrainGrassBakeSettings grassSettings,
        List<float>[] buckets)
    {
        var sizeX = cx1 - cx0;
        var sizeZ = cz1 - cz0;
        var sizeY = layerMax - layerMin + 1;
        if (sizeX <= 0 || sizeZ <= 0 || sizeY <= 0)
        {
            return;
        }

        EmitAxisSlices(heightAt, columnAt, fillDepth, layerMin, layerMax, cx0, cx1, cz0, cz1,
            surfaceWorldY, metersPerTile, axis: 1, grassSettings, buckets);
        EmitAxisSlices(heightAt, columnAt, fillDepth, layerMin, layerMax, cx0, cx1, cz0, cz1,
            surfaceWorldY, metersPerTile, axis: 0, grassSettings, buckets);
        EmitAxisSlices(heightAt, columnAt, fillDepth, layerMin, layerMax, cx0, cx1, cz0, cz1,
            surfaceWorldY, metersPerTile, axis: 2, grassSettings, buckets);
    }

    private static void EmitAxisSlices(
        Func<int, int, int> heightAt,
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        int fillDepth,
        int layerMin,
        int layerMax,
        int cx0,
        int cx1,
        int cz0,
        int cz1,
        float surfaceWorldY,
        float metersPerTile,
        int axis,
        PreviewTerrainGrassBakeSettings grassSettings,
        List<float>[] buckets)
    {
        int uSize, vSize, wMin, wMax;
        if (axis == 0)
        {
            wMin = cx0;
            wMax = cx1;
            uSize = layerMax - layerMin + 1;
            vSize = cz1 - cz0;
        }
        else if (axis == 1)
        {
            EmitYFaces(heightAt, columnAt, fillDepth, layerMin, layerMax, cx0, cx1, cz0, cz1,
                surfaceWorldY, metersPerTile, grassSettings, buckets);
            return;
        }
        else
        {
            wMin = cz0;
            wMax = cz1;
            uSize = cx1 - cx0;
            vSize = layerMax - layerMin + 1;
        }

        var mask = new byte[uSize * vSize];
        for (var dir = 0; dir < 2; dir++)
        {
            var positive = dir == 0;
            for (var w = wMin; w < wMax; w++)
            {
                Array.Clear(mask);
                var any = false;
                for (var v = 0; v < vSize; v++)
                {
                    for (var u = 0; u < uSize; u++)
                    {
                        int bx, by, bz;
                        if (axis == 0)
                        {
                            bx = w;
                            by = layerMin + u;
                            bz = cz0 + v;
                        }
                        else
                        {
                            bx = cx0 + u;
                            by = layerMin + v;
                            bz = w;
                        }

                        if (!IsSolid(heightAt, fillDepth, bx, by, bz))
                        {
                            continue;
                        }

                        int nx = bx, ny = by, nz = bz;
                        if (axis == 0)
                        {
                            nx += positive ? 1 : -1;
                        }
                        else
                        {
                            nz += positive ? 1 : -1;
                        }

                        if (IsSolid(heightAt, fillDepth, nx, ny, nz))
                        {
                            continue;
                        }

                        var mat = ResolveHorizontalFaceMaterial(
                            columnAt, bx, by, bz, nx, nz, grassSettings);
                        mask[v * uSize + u] = (byte)(mat + 1);
                        any = true;
                    }
                }

                if (!any)
                {
                    continue;
                }

                GreedyEmitMask(mask, uSize, vSize, axis, positive, w, cx0, cz0, layerMin,
                    surfaceWorldY, metersPerTile, grassSettings, buckets);
            }
        }
    }

    /// <summary>
    /// Horizontal face material from biome column stack + cliff Δh rules.
    /// Grass BetterGrass/overlay still apply on grass surfaces when BlockModelFaces is active.
    /// </summary>
    internal static int ResolveHorizontalFaceMaterial(
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        int bx,
        int by,
        int bz,
        int neighborX,
        int neighborZ,
        PreviewTerrainGrassBakeSettings grassSettings)
    {
        var col = columnAt(bx, bz);
        var columnH = col.Height;
        if (by != columnH)
        {
            var depthFromSurface = columnH - by;
            return BlockKindToSlot(depthFromSurface <= 1 ? col.Subsurface : col.Deep);
        }

        var neighbor = columnAt(neighborX, neighborZ);
        var delta = columnH - neighbor.Height;

        if (col.Biome == PreviewTerrainBiomeId.Mountains &&
            delta >= PreviewStageConstants.TerrainCliffDeltaBlocks)
        {
            return delta >= PreviewStageConstants.TerrainCliffDeltaBlocks + 2
                ? PreviewTerrainGrassSlots.Gravel
                : PreviewTerrainGrassSlots.Stone;
        }

        return col.Surface switch
        {
            PreviewTerrainBlockKind.Sand => PreviewTerrainGrassSlots.Sand,
            PreviewTerrainBlockKind.Stone => PreviewTerrainGrassSlots.Stone,
            PreviewTerrainBlockKind.Gravel => PreviewTerrainGrassSlots.Gravel,
            PreviewTerrainBlockKind.Dirt => PreviewTerrainGrassSlots.Dirt,
            _ => ResolveGrassSurfaceSide(columnH, neighbor.Height, grassSettings),
        };
    }

    /// <summary>Legacy height-only overload for existing tests.</summary>
    internal static int ResolveHorizontalFaceMaterial(
        Func<int, int, int> heightAt,
        int bx,
        int by,
        int bz,
        int neighborX,
        int neighborZ,
        PreviewTerrainGrassBakeSettings grassSettings) =>
        ResolveHorizontalFaceMaterial(
            (x, z) => new PreviewTerrainColumnSample(
                heightAt(x, z),
                PreviewTerrainBiomeId.Plains,
                PreviewTerrainBlockKind.Grass,
                PreviewTerrainBlockKind.Dirt,
                PreviewTerrainBlockKind.Stone),
            bx, by, bz, neighborX, neighborZ, grassSettings);

    private static int ResolveGrassSurfaceSide(
        int columnH,
        int neighborH,
        PreviewTerrainGrassBakeSettings grassSettings)
    {
        if (grassSettings.Mode != PreviewTerrainGrassMode.BlockModelFaces)
        {
            return PreviewTerrainGrassSlots.Top;
        }

        if (grassSettings.BetterGrassEnabled && neighborH < columnH)
        {
            return PreviewTerrainGrassSlots.Top;
        }

        return PreviewTerrainGrassSlots.Side;
    }

    internal static int BlockKindToSlot(PreviewTerrainBlockKind kind) =>
        kind switch
        {
            PreviewTerrainBlockKind.Sand => PreviewTerrainGrassSlots.Sand,
            PreviewTerrainBlockKind.Stone => PreviewTerrainGrassSlots.Stone,
            PreviewTerrainBlockKind.Gravel => PreviewTerrainGrassSlots.Gravel,
            PreviewTerrainBlockKind.Dirt => PreviewTerrainGrassSlots.Dirt,
            _ => PreviewTerrainGrassSlots.Top,
        };

    private static void EmitYFaces(
        Func<int, int, int> heightAt,
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        int fillDepth,
        int layerMin,
        int layerMax,
        int cx0,
        int cx1,
        int cz0,
        int cz1,
        float surfaceWorldY,
        float metersPerTile,
        PreviewTerrainGrassBakeSettings grassSettings,
        List<float>[] buckets)
    {
        var uSize = cx1 - cx0;
        var vSize = cz1 - cz0;
        var mask = new byte[uSize * vSize];
        for (var dir = 0; dir < 2; dir++)
        {
            var positive = dir == 0;
            for (var by = layerMin; by <= layerMax; by++)
            {
                Array.Clear(mask);
                var any = false;
                for (var v = 0; v < vSize; v++)
                {
                    for (var u = 0; u < uSize; u++)
                    {
                        var bx = cx0 + u;
                        var bz = cz0 + v;
                        if (!IsSolid(heightAt, fillDepth, bx, by, bz))
                        {
                            continue;
                        }

                        var ny = by + (positive ? 1 : -1);
                        if (IsSolid(heightAt, fillDepth, bx, ny, bz))
                        {
                            continue;
                        }

                        var mat = ResolveYFaceMaterial(positive, columnAt(bx, bz), grassSettings);
                        mask[v * uSize + u] = (byte)(mat + 1);
                        any = true;
                    }
                }

                if (!any)
                {
                    continue;
                }

                GreedyEmitMask(mask, uSize, vSize, axis: 1, positive, w: by, cx0, cz0, layerMin,
                    surfaceWorldY, metersPerTile, grassSettings, buckets);
            }
        }
    }

    internal static int ResolveYFaceMaterial(
        bool positiveUp,
        PreviewTerrainColumnSample column,
        PreviewTerrainGrassBakeSettings grassSettings)
    {
        if (positiveUp)
        {
            if (grassSettings.Mode != PreviewTerrainGrassMode.BlockModelFaces &&
                column.Surface == PreviewTerrainBlockKind.Grass)
            {
                return PreviewTerrainGrassSlots.Top;
            }

            return BlockKindToSlot(column.Surface);
        }

        return column.Biome is PreviewTerrainBiomeId.Mountains or PreviewTerrainBiomeId.Desert
            ? PreviewTerrainGrassSlots.Stone
            : PreviewTerrainGrassSlots.Dirt;
    }

    /// <summary>Legacy overload for existing tests (Plains underside/top).</summary>
    internal static int ResolveYFaceMaterial(bool positiveUp, PreviewTerrainGrassBakeSettings grassSettings) =>
        ResolveYFaceMaterial(
            positiveUp,
            new PreviewTerrainColumnSample(
                0,
                PreviewTerrainBiomeId.Plains,
                PreviewTerrainBlockKind.Grass,
                PreviewTerrainBlockKind.Dirt,
                PreviewTerrainBlockKind.Stone),
            grassSettings);

    private static void GreedyEmitMask(
        byte[] mask,
        int uSize,
        int vSize,
        int axis,
        bool positive,
        int w,
        int cx0,
        int cz0,
        int layerMin,
        float surfaceWorldY,
        float metersPerTile,
        PreviewTerrainGrassBakeSettings grassSettings,
        List<float>[] buckets)
    {
        for (var v = 0; v < vSize; v++)
        {
            for (var u = 0; u < uSize;)
            {
                var cell = mask[v * uSize + u];
                if (cell == 0)
                {
                    u++;
                    continue;
                }

                var width = 1;
                while (u + width < uSize && mask[v * uSize + u + width] == cell)
                {
                    width++;
                }

                var height = 1;
                var done = false;
                while (v + height < vSize && !done)
                {
                    for (var k = 0; k < width; k++)
                    {
                        if (mask[(v + height) * uSize + u + k] != cell)
                        {
                            done = true;
                            break;
                        }
                    }

                    if (!done)
                    {
                        height++;
                    }
                }

                for (var dv = 0; dv < height; dv++)
                {
                    for (var du = 0; du < width; du++)
                    {
                        mask[(v + dv) * uSize + u + du] = 0;
                    }
                }

                var material = cell - 1;
                EmitMergedQuad(axis, positive, w, u, v, width, height, cx0, cz0, layerMin,
                    surfaceWorldY, metersPerTile, buckets[material]);

                // Vanilla grass side overlay shell (skipped when BetterGrass replaced the side with Top).
                if (grassSettings.EmitOverlay &&
                    material == PreviewTerrainGrassSlots.Side &&
                    axis != 1)
                {
                    EmitMergedQuad(axis, positive, w, u, v, width, height, cx0, cz0, layerMin,
                        surfaceWorldY, metersPerTile, buckets[PreviewTerrainGrassSlots.Overlay]);
                }

                u += width;
            }
        }
    }

    private static void EmitMergedQuad(
        int axis,
        bool positive,
        int w,
        int u,
        int v,
        int width,
        int height,
        int cx0,
        int cz0,
        int layerMin,
        float surfaceWorldY,
        float metersPerTile,
        List<float> verts)
    {
        Span<Vector3> corners = stackalloc Vector3[4];
        Span<Vector2> uvs = stackalloc Vector2[4];
        Vector3 n;
        Vector3 tFallback;
        float wSign = 1f;

        if (axis == 1)
        {
            var y = positive ? surfaceWorldY + w : surfaceWorldY + w - 1f;
            var x0 = cx0 + u;
            var z0 = cz0 + v;
            var x1 = x0 + width;
            var z1 = z0 + height;
            if (positive)
            {
                n = Vector3.UnitY;
                tFallback = Vector3.UnitX;
                corners[0] = new(x0, y, z0);
                corners[1] = new(x1, y, z0);
                corners[2] = new(x1, y, z1);
                corners[3] = new(x0, y, z1);
            }
            else
            {
                n = -Vector3.UnitY;
                tFallback = Vector3.UnitX;
                wSign = -1f;
                corners[0] = new(x0, y, z1);
                corners[1] = new(x1, y, z1);
                corners[2] = new(x1, y, z0);
                corners[3] = new(x0, y, z0);
            }

            for (var i = 0; i < 4; i++)
            {
                uvs[i] = new(corners[i].X / metersPerTile, corners[i].Z / metersPerTile);
            }
        }
        else if (axis == 0)
        {
            var x = positive ? w + 1f : w;
            var y0 = surfaceWorldY + (layerMin + u) - 1f;
            var y1 = surfaceWorldY + (layerMin + u + width - 1);
            var z0 = cz0 + v;
            var z1 = z0 + height;
            if (positive)
            {
                n = Vector3.UnitX;
                tFallback = new Vector3(0, 0, -1);
                corners[0] = new(x, y0, z1);
                corners[1] = new(x, y0, z0);
                corners[2] = new(x, y1, z0);
                corners[3] = new(x, y1, z1);
            }
            else
            {
                n = -Vector3.UnitX;
                tFallback = new Vector3(0, 0, 1);
                corners[0] = new(x, y0, z0);
                corners[1] = new(x, y0, z1);
                corners[2] = new(x, y1, z1);
                corners[3] = new(x, y1, z0);
            }

            for (var i = 0; i < 4; i++)
            {
                uvs[i] = new(corners[i].Z / metersPerTile, corners[i].Y / metersPerTile);
            }
        }
        else
        {
            var z = positive ? w + 1f : w;
            var x0 = cx0 + u;
            var x1 = x0 + width;
            var y0 = surfaceWorldY + (layerMin + v) - 1f;
            var y1 = surfaceWorldY + (layerMin + v + height - 1);
            if (positive)
            {
                n = Vector3.UnitZ;
                tFallback = Vector3.UnitX;
                corners[0] = new(x0, y0, z);
                corners[1] = new(x1, y0, z);
                corners[2] = new(x1, y1, z);
                corners[3] = new(x0, y1, z);
            }
            else
            {
                n = -Vector3.UnitZ;
                tFallback = -Vector3.UnitX;
                corners[0] = new(x1, y0, z);
                corners[1] = new(x0, y0, z);
                corners[2] = new(x0, y1, z);
                corners[3] = new(x1, y1, z);
            }

            for (var i = 0; i < 4; i++)
            {
                uvs[i] = new(corners[i].X / metersPerTile, corners[i].Y / metersPerTile);
            }
        }

        AddSolidFace(n, tFallback, wSign, corners, uvs, verts);
    }

    /// <summary>
    /// Lowest relative Y included in a Full bake. Always reaches the shared solid floor so
    /// columns with large height deltas cannot leave sky gaps under overhangs.
    /// </summary>
    internal static int ResolveLayerMin(int minColumnHeight, int fillDepth) =>
        Math.Min(
            minColumnHeight - Math.Max(1, fillDepth) + 1,
            PreviewStageConstants.TerrainSolidFloorRelativeY);

    /// <summary>
    /// Inclusive bottom Y of a solid column. Uses the shared world floor so a tall column
    /// remains solid beside a much shorter neighbor (no floating shelves / holes).
    /// </summary>
    internal static int SolidBottomY(int columnHeight, int fillDepth) =>
        Math.Min(
            columnHeight - Math.Max(1, fillDepth) + 1,
            PreviewStageConstants.TerrainSolidFloorRelativeY);

    internal static bool IsSolid(Func<int, int, int> heightAt, int fillDepth, int bx, int by, int bz)
    {
        var h = heightAt(bx, bz);
        if (h == int.MinValue)
        {
            return false;
        }

        var bottom = SolidBottomY(h, fillDepth);
        return by >= bottom && by <= h;
    }

    private static void AddSolidFace(
        Vector3 normal,
        Vector3 fallbackTangent,
        float fallbackWSign,
        ReadOnlySpan<Vector3> cornersIn,
        ReadOnlySpan<Vector2> uvsIn,
        List<float> verts)
    {
        Span<Vector3> corners = stackalloc Vector3[4];
        Span<Vector2> uvs = stackalloc Vector2[4];
        cornersIn.CopyTo(corners);
        uvsIn.CopyTo(uvs);

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

/// <summary>Baked voxel terrain mesh plus per-chunk draw batches for cull/LOD.</summary>
public sealed class PreviewTerrainBakeResult
{
    public required PreviewMesh Mesh { get; init; }
    public required PreviewDrawBatch[] ChunkBatches { get; init; }
    public int MinRelativeHeight { get; init; }
    public int MaxRelativeHeight { get; init; }
}
