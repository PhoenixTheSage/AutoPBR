using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.Preview;

namespace AutoPBR.App.Tests;

public sealed class TerrainGpuFullMeshParityTests
{
    public const float PositionEpsilon = 1e-3f;

    [Fact]
    public void BakeFullChunkSolidsPerFace_ProducesNonEmptyMeshForOriginChunk()
    {
        var key = new TerrainChunkKey(0, 0);
        var mesh = PreviewTerrainMeshBaker.BakeFullChunkSolidsPerFace(
            key,
            PreviewTerrainGrassBakeSettings.BuiltIn,
            PreviewTerrainWorldGenSettings.Default);

        Assert.NotNull(mesh);
        Assert.Equal(TerrainChunkLodKind.Full, mesh!.Lod);
        Assert.True(mesh.Indices.Length > 0);
        Assert.True(mesh.InterleavedVertices.Length >= PreviewMesh.FloatsPerVertex * 4);
        Assert.Equal(0, mesh.Indices.Length % 6);
    }

    [Fact]
    public void BakeFullChunkSolidsPerFace_MatchesSelfDeterministic()
    {
        var key = new TerrainChunkKey(2, -1);
        var a = PreviewTerrainMeshBaker.BakeFullChunkSolidsPerFace(
            key,
            PreviewTerrainGrassBakeSettings.BuiltIn,
            PreviewTerrainWorldGenSettings.Default);
        var b = PreviewTerrainMeshBaker.BakeFullChunkSolidsPerFace(
            key,
            PreviewTerrainGrassBakeSettings.BuiltIn,
            PreviewTerrainWorldGenSettings.Default);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Indices.Length, b!.Indices.Length);
        Assert.Equal(a.InterleavedVertices.Length, b.InterleavedVertices.Length);
        Assert.Equal(a.MinRelativeHeight, b.MinRelativeHeight);
        Assert.Equal(a.MaxRelativeHeight, b.MaxRelativeHeight);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, -2)]
    [InlineData(-3, 4)]
    public void BakeFullChunkSolidsPerFace_IndexCountExceedsGreedyWhenReliefPresent(int cx, int cz)
    {
        var key = new TerrainChunkKey(cx, cz);
        var perFace = PreviewTerrainMeshBaker.BakeFullChunkSolidsPerFace(
            key,
            PreviewTerrainGrassBakeSettings.BuiltIn,
            PreviewTerrainWorldGenSettings.Default);
        var greedy = PreviewTerrainMeshBaker.BakeFullChunk(
            key,
            PreviewTerrainGrassBakeSettings.BuiltIn,
            PreviewTerrainWorldGenSettings.Default,
            vegetation: null);

        Assert.NotNull(perFace);
        Assert.NotNull(greedy);
        // Per-face never merges; greedy may equal or reduce index count.
        Assert.True(perFace!.Indices.Length >= greedy!.Indices.Length);
    }

    /// <summary>
    /// Compare GPU vs CPU per-face meshes: index count, height bounds, and position multiset.
    /// Vertex order within a material bucket may differ (atomic append vs column scan).
    /// </summary>
    internal static void AssertPerFaceParity(
        PreviewTerrainChunkMesh cpu,
        PreviewTerrainChunkMesh gpu,
        float positionEpsilon = PositionEpsilon)
    {
        Assert.Equal(cpu.Indices.Length, gpu.Indices.Length);
        Assert.Equal(cpu.InterleavedVertices.Length / PreviewMesh.FloatsPerVertex,
            gpu.InterleavedVertices.Length / PreviewMesh.FloatsPerVertex);
        Assert.Equal(cpu.MinRelativeHeight, gpu.MinRelativeHeight);
        Assert.Equal(cpu.MaxRelativeHeight, gpu.MaxRelativeHeight);

        var cpuSorted = ExtractSortedPositions(cpu.InterleavedVertices);
        var gpuSorted = ExtractSortedPositions(gpu.InterleavedVertices);
        Assert.Equal(cpuSorted.Length, gpuSorted.Length);
        var maxPosErr = 0f;
        for (var i = 0; i < cpuSorted.Length; i++)
        {
            maxPosErr = Math.Max(maxPosErr, Math.Abs(cpuSorted[i].X - gpuSorted[i].X));
            maxPosErr = Math.Max(maxPosErr, Math.Abs(cpuSorted[i].Y - gpuSorted[i].Y));
            maxPosErr = Math.Max(maxPosErr, Math.Abs(cpuSorted[i].Z - gpuSorted[i].Z));
        }

        Assert.True(
            maxPosErr <= positionEpsilon,
            $"GPU vs CPU per-face max sorted-position |Δ|={maxPosErr} exceeded epsilon {positionEpsilon}.");
    }

    private static System.Numerics.Vector3[] ExtractSortedPositions(float[] interleaved)
    {
        var count = interleaved.Length / PreviewMesh.FloatsPerVertex;
        var positions = new System.Numerics.Vector3[count];
        for (var v = 0; v < count; v++)
        {
            var i = v * PreviewMesh.FloatsPerVertex;
            positions[v] = new System.Numerics.Vector3(interleaved[i], interleaved[i + 1], interleaved[i + 2]);
        }

        Array.Sort(positions, static (a, b) =>
        {
            var c = a.X.CompareTo(b.X);
            if (c != 0)
            {
                return c;
            }

            c = a.Y.CompareTo(b.Y);
            return c != 0 ? c : a.Z.CompareTo(b.Z);
        });
        return positions;
    }

    [Fact]
    public void BakeFullChunk_SkipsSolidFloorUndersideFaces()
    {
        var key = new TerrainChunkKey(1, -1);
        var mesh = PreviewTerrainMeshBaker.BakeFullChunk(
            key,
            PreviewTerrainGrassBakeSettings.BuiltIn,
            PreviewTerrainWorldGenSettings.Default,
            vegetation: null);
        Assert.NotNull(mesh);

        var floorY = PreviewStageConstants.GroundPlaneWorldY +
                     PreviewStageConstants.TerrainSolidFloorRelativeY - 1f;
        var verts = mesh!.InterleavedVertices;
        for (var i = 0; i < verts.Length; i += PreviewMesh.FloatsPerVertex)
        {
            var y = verts[i + 1];
            var ny = verts[i + 4];
            Assert.False(
                ny < -0.5f && Math.Abs(y - floorY) < 1e-3f,
                $"Found solid-floor underside vertex at y={y}");
        }
    }
}
