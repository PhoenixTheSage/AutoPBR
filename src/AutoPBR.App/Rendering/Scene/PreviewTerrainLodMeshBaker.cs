using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.Preview;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Distant Horizons / Voxy–style LOD: 1m column silhouette (tops + vertical steps) as one
/// aggressively greedy-merged mesh. No fill-depth underground solids.
/// </summary>
public static class PreviewTerrainLodMeshBaker
{
    public static PreviewTerrainChunkMesh? BakeLodChunk(
        TerrainChunkKey key,
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

        var cx0 = key.OriginX(chunkSize);
        var cz0 = key.OriginZ(chunkSize);
        var cx1 = cx0 + chunkSize;
        var cz1 = cz0 + chunkSize;

        // Core + 1-column halo for step occlusion against neighbors.
        var side = chunkSize + 2;
        var heights = new int[side * side];
        var minH = int.MaxValue;
        var maxH = int.MinValue;
        for (var lz = 0; lz < side; lz++)
        {
            for (var lx = 0; lx < side; lx++)
            {
                var wx = cx0 - 1 + lx;
                var wz = cz0 - 1 + lz;
                var h = PreviewTerrainHeightfield.SampleColumn(
                    wx, wz, flatPadHalfExtent, transitionBlocks, maxRelief, seed);
                heights[lz * side + lx] = h;
                if (lx > 0 && lx < side - 1 && lz > 0 && lz < side - 1)
                {
                    minH = Math.Min(minH, h);
                    maxH = Math.Max(maxH, h);
                }
            }
        }

        if (minH == int.MaxValue)
        {
            return null;
        }

        int H(int wx, int wz)
        {
            var lx = wx - (cx0 - 1);
            var lz = wz - (cz0 - 1);
            if ((uint)lx >= (uint)side || (uint)lz >= (uint)side)
            {
                return PreviewTerrainHeightfield.SampleColumn(
                    wx, wz, flatPadHalfExtent, transitionBlocks, maxRelief, seed);
            }

            return heights[lz * side + lx];
        }

        var verts = new List<float>(chunkSize * chunkSize * 24);
        var indices = new List<uint>(chunkSize * chunkSize * 36);

        EmitTopFaces(H, cx0, cx1, cz0, cz1, surfaceWorldY, metersPerTile, verts, indices);
        EmitStepFacesX(H, cx0, cx1, cz0, cz1, surfaceWorldY, metersPerTile, verts, indices);
        EmitStepFacesZ(H, cx0, cx1, cz0, cz1, surfaceWorldY, metersPerTile, verts, indices);

        if (indices.Count == 0)
        {
            return null;
        }

        var minY = surfaceWorldY + minH - 1;
        var maxY = surfaceWorldY + maxH;
        var boundsMin = new Vector3(cx0, minY, cz0);
        var boundsMax = new Vector3(cx1, maxY, cz1);
        var center = (boundsMin + boundsMax) * 0.5f;
        var indexArray = indices.ToArray();
        return new PreviewTerrainChunkMesh
        {
            Key = key,
            Lod = TerrainChunkLodKind.Lod,
            InterleavedVertices = verts.ToArray(),
            Indices = indexArray,
            // Distant LOD stays Top-only (single material slot 0).
            DrawBatches = [new PreviewDrawBatch(0, indexArray.Length, PreviewTerrainGrassSlots.Top)],
            BoundsCenter = center,
            BoundsRadius = Vector3.Distance(center, boundsMax),
            MinRelativeHeight = minH,
            MaxRelativeHeight = maxH
        };
    }

    private static void EmitTopFaces(
        Func<int, int, int> h,
        int cx0, int cx1, int cz0, int cz1,
        float surfaceWorldY, float metersPerTile,
        List<float> verts, List<uint> indices)
    {
        var uSize = cx1 - cx0;
        var vSize = cz1 - cz0;
        // Group by height so greedy merge stays coplanar.
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

                var height = h(cx0 + u, cz0 + v);
                var width = 1;
                while (u + width < uSize &&
                       !visited[v * uSize + u + width] &&
                       h(cx0 + u + width, cz0 + v) == height)
                {
                    width++;
                }

                var runH = 1;
                var done = false;
                while (v + runH < vSize && !done)
                {
                    for (var k = 0; k < width; k++)
                    {
                        if (visited[(v + runH) * uSize + u + k] ||
                            h(cx0 + u + k, cz0 + v + runH) != height)
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
                EmitQuad(Vector3.UnitY, Vector3.UnitX, 1f, top, metersPerTile, topUv: true, verts, indices);
            }
        }
    }

    private static void EmitStepFacesX(
        Func<int, int, int> h,
        int cx0, int cx1, int cz0, int cz1,
        float surfaceWorldY, float metersPerTile,
        List<float> verts, List<uint> indices)
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
                var run = 1;
                while (z + run < cz1)
                {
                    var l2 = h(x - 1, z + run);
                    var r2 = h(x, z + run);
                    if (Math.Max(l2, r2) != hi || Math.Min(l2, r2) != lo || (l2 > r2) != positive)
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
                        EmitQuad(Vector3.UnitX, new Vector3(0, 0, -1), 1f, face, metersPerTile, topUv: false, verts, indices);
                    }
                    else
                    {
                        face[0] = new(xf, y0, z0);
                        face[1] = new(xf, y0, z1);
                        face[2] = new(xf, y1, z1);
                        face[3] = new(xf, y1, z0);
                        EmitQuad(-Vector3.UnitX, new Vector3(0, 0, 1), 1f, face, metersPerTile, topUv: false, verts, indices);
                    }
                }

                z += run;
            }
        }
    }

    private static void EmitStepFacesZ(
        Func<int, int, int> h,
        int cx0, int cx1, int cz0, int cz1,
        float surfaceWorldY, float metersPerTile,
        List<float> verts, List<uint> indices)
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
                var run = 1;
                while (x + run < cx1)
                {
                    var b2 = h(x + run, z - 1);
                    var f2 = h(x + run, z);
                    if (Math.Max(b2, f2) != hi || Math.Min(b2, f2) != lo || (b2 > f2) != positive)
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
                        EmitQuad(Vector3.UnitZ, Vector3.UnitX, 1f, face, metersPerTile, topUv: false, verts, indices);
                    }
                    else
                    {
                        face[0] = new(x1, y0, zf);
                        face[1] = new(x0, y0, zf);
                        face[2] = new(x0, y1, zf);
                        face[3] = new(x1, y1, zf);
                        EmitQuad(-Vector3.UnitZ, -Vector3.UnitX, 1f, face, metersPerTile, topUv: false, verts, indices);
                    }
                }

                x += run;
            }
        }
    }

    private static void EmitQuad(
        Vector3 normal,
        Vector3 fallbackTangent,
        float fallbackWSign,
        ReadOnlySpan<Vector3> cornersIn,
        float metersPerTile,
        bool topUv,
        List<float> verts,
        List<uint> indices)
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
        var baseIndex = (uint)(verts.Count / PreviewMesh.FloatsPerVertex);
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

        indices.Add(baseIndex);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 3);
    }
}
