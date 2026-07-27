using System.Numerics;

using AutoPBR.Preview;

namespace AutoPBR.App.Rendering.Scene;

public readonly record struct TerrainChunkKey(int X, int Z)
{
    public static TerrainChunkKey FromWorld(float worldX, float worldZ, int chunkSize = PreviewStageConstants.TerrainChunkSize)
    {
        chunkSize = Math.Max(1, chunkSize);
        var cx = (int)MathF.Floor(worldX / chunkSize);
        var cz = (int)MathF.Floor(worldZ / chunkSize);
        return new TerrainChunkKey(cx, cz);
    }

    public int ChebyshevDistanceTo(TerrainChunkKey other) =>
        Math.Max(Math.Abs(X - other.X), Math.Abs(Z - other.Z));

    public int OriginX(int chunkSize = PreviewStageConstants.TerrainChunkSize) => X * chunkSize;

    public int OriginZ(int chunkSize = PreviewStageConstants.TerrainChunkSize) => Z * chunkSize;

    public Vector2 CenterXZ(int chunkSize = PreviewStageConstants.TerrainChunkSize)
    {
        var half = chunkSize * 0.5f;
        return new Vector2(OriginX(chunkSize) + half, OriginZ(chunkSize) + half);
    }
}

public enum TerrainChunkLodKind : byte
{
    Full = 0,
    Lod = 1,
}

/// <summary>CPU mesh payload produced by Full or Lod bakers (uploaded on the GL thread).</summary>
public sealed class PreviewTerrainChunkMesh
{
    public required TerrainChunkKey Key { get; init; }
    public required TerrainChunkLodKind Lod { get; init; }
    public required float[] InterleavedVertices { get; init; }
    public required uint[] Indices { get; init; }

    /// <summary>
    /// Contiguous index ranges by terrain grass material slot. Empty means draw the full index buffer
    /// with material slot 0 (BuiltIn).
    /// </summary>
    public PreviewDrawBatch[] DrawBatches { get; init; } = [];

    public Vector3 BoundsCenter { get; init; }
    public float BoundsRadius { get; init; }
    public int MinRelativeHeight { get; init; }
    public int MaxRelativeHeight { get; init; }
}
