using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.Preview;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Distant Horizons / Voxy–style LOD: 1m column silhouette (tops + vertical steps) as one
/// aggressively greedy-merged mesh. No fill-depth underground solids. Surfaces, step faces,
/// and vegetation use the same biome / kit material slots as Full chunks.
/// </summary>
public static class PreviewTerrainLodMeshBaker
{
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
        int seed = PreviewStageConstants.TerrainHeightSeed)
    {
        chunkSize = Math.Max(1, chunkSize);
        if (metersPerTile <= 1e-6f)
        {
            metersPerTile = PreviewStageConstants.MetersPerGrassTile;
        }

        // default(grassSettings) looks like BuiltIn for Mode/flags (BuiltInSingleTop == 0).
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

        var cx0 = key.OriginX(chunkSize);
        var cz0 = key.OriginZ(chunkSize);
        var cx1 = cx0 + chunkSize;
        var cz1 = cz0 + chunkSize;

        // Core + 1-column halo for step occlusion / material against neighbors.
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

        int H(int wx, int wz) => ColumnAt(wx, wz).Height;

        var vegPlan = vegetation is { HasAny: true } ? vegetation : PreviewTerrainVegetationBakePlan.Empty;
        var slotCount = Math.Max(PreviewTerrainGrassSlots.MaxCount, vegPlan.TotalSlotCount);
        var buckets = PreviewTerrainMeshBaker.CreateMaterialBuckets(slotCount);
        EmitTopFaces(H, ColumnAt, grassSettings, cx0, cx1, cz0, cz1, surfaceWorldY, metersPerTile, buckets);
        EmitStepFacesX(H, ColumnAt, grassSettings, cx0, cx1, cz0, cz1, surfaceWorldY, metersPerTile, buckets);
        EmitStepFacesZ(H, ColumnAt, grassSettings, cx0, cx1, cz0, cz1, surfaceWorldY, metersPerTile, buckets);

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
            Lod = TerrainChunkLodKind.Lod,
            InterleavedVertices = verts,
            Indices = indices,
            DrawBatches = batches,
            BoundsCenter = center,
            BoundsRadius = Vector3.Distance(center, boundsMax),
            MinRelativeHeight = minH,
            MaxRelativeHeight = maxH
        };
    }

    private static void EmitTopFaces(
        Func<int, int, int> h,
        Func<int, int, PreviewTerrainColumnSample> columnAt,
        PreviewTerrainGrassBakeSettings grassSettings,
        int cx0, int cx1, int cz0, int cz1,
        float surfaceWorldY, float metersPerTile,
        List<float>[] buckets)
    {
        var uSize = cx1 - cx0;
        var vSize = cz1 - cz0;
        // Group by height + surface material so greedy merge stays coplanar and same slot.
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

                var wx = cx0 + u;
                var wz = cz0 + v;
                var height = h(wx, wz);
                var material = PreviewTerrainMeshBaker.ResolveYFaceMaterial(
                    positiveUp: true, columnAt(wx, wz), grassSettings);
                var width = 1;
                while (u + width < uSize &&
                       !visited[v * uSize + u + width] &&
                       h(cx0 + u + width, cz0 + v) == height &&
                       PreviewTerrainMeshBaker.ResolveYFaceMaterial(
                           positiveUp: true, columnAt(cx0 + u + width, cz0 + v), grassSettings) == material)
                {
                    width++;
                }

                var runH = 1;
                var done = false;
                while (v + runH < vSize && !done)
                {
                    for (var k = 0; k < width; k++)
                    {
                        var nx = cx0 + u + k;
                        var nz = cz0 + v + runH;
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

                var x0 = cx0 + u;
                var z0 = cz0 + v;
                var x1 = x0 + width;
                var z1 = z0 + runH;
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
        float surfaceWorldY, float metersPerTile,
        List<float>[] buckets)
    {
        Span<Vector3> face = stackalloc Vector3[4];
        // Faces on planes x = cx0..cx1 between columns (cx-1,z) and (cx,z).
        for (var x = cx0; x <= cx1; x++)
        {
            for (var z = cz0; z < cz1;)
            {
                var left = h(x - 1, z);
                var right = h(x, z);
                if (left == right)
                {
                    z++;
                    continue;
                }

                var positive = left > right; // face points +X when left is higher
                var hi = Math.Max(left, right);
                var lo = Math.Min(left, right);
                var material = ResolveStepMaterial(
                    columnAt,
                    grassSettings,
                    hiColX: positive ? x - 1 : x,
                    hiColZ: z,
                    neighborX: positive ? x : x - 1,
                    neighborZ: z);
                var run = 1;
                while (z + run < cz1)
                {
                    var l2 = h(x - 1, z + run);
                    var r2 = h(x, z + run);
                    if (Math.Max(l2, r2) != hi || Math.Min(l2, r2) != lo || (l2 > r2) != positive)
                    {
                        break;
                    }

                    var mat2 = ResolveStepMaterial(
                        columnAt,
                        grassSettings,
                        hiColX: positive ? x - 1 : x,
                        hiColZ: z + run,
                        neighborX: positive ? x : x - 1,
                        neighborZ: z + run);
                    if (mat2 != material)
                    {
                        break;
                    }

                    run++;
                }

                // Only emit the step if at least one side is inside the chunk core.
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
        float surfaceWorldY, float metersPerTile,
        List<float>[] buckets)
    {
        Span<Vector3> face = stackalloc Vector3[4];
        for (var z = cz0; z <= cz1; z++)
        {
            for (var x = cx0; x < cx1;)
            {
                var back = h(x, z - 1);
                var fwd = h(x, z);
                if (back == fwd)
                {
                    x++;
                    continue;
                }

                var positive = back > fwd;
                var hi = Math.Max(back, fwd);
                var lo = Math.Min(back, fwd);
                var material = ResolveStepMaterial(
                    columnAt,
                    grassSettings,
                    hiColX: x,
                    hiColZ: positive ? z - 1 : z,
                    neighborX: x,
                    neighborZ: positive ? z : z - 1);
                var run = 1;
                while (x + run < cx1)
                {
                    var b2 = h(x + run, z - 1);
                    var f2 = h(x + run, z);
                    if (Math.Max(b2, f2) != hi || Math.Min(b2, f2) != lo || (b2 > f2) != positive)
                    {
                        break;
                    }

                    var mat2 = ResolveStepMaterial(
                        columnAt,
                        grassSettings,
                        hiColX: x + run,
                        hiColZ: positive ? z - 1 : z,
                        neighborX: x + run,
                        neighborZ: positive ? z : z - 1);
                    if (mat2 != material)
                    {
                        break;
                    }

                    run++;
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
