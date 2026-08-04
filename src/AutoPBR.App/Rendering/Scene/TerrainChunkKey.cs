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

/// <summary>
/// Stream residency unit: Full (LodLevel 0 = 1×1 chunk) or combined LOD section
/// (LodLevel L = 2^L × 2^L chunks, sample step 2^L m). X/Z are section coordinates in units of
/// <see cref="ChunksPerSide"/>. Levels 1–7 cover extreme rings (256/512/1024 chunks).
/// </summary>
public readonly record struct TerrainResidencyKey(int X, int Z, byte LodLevel)
{
    /// <summary>Coarsest combined LOD (128×128 chunks / 128 m samples).</summary>
    public const byte MaxLodLevel = 7;

    public static TerrainResidencyKey Full(int chunkX, int chunkZ) => new(chunkX, chunkZ, 0);

    public static TerrainResidencyKey Full(TerrainChunkKey chunk) => Full(chunk.X, chunk.Z);

    public static TerrainResidencyKey Section(int sectionX, int sectionZ, byte lodLevel)
    {
        lodLevel = ClampLodLevel(lodLevel);
        return new TerrainResidencyKey(sectionX, sectionZ, lodLevel);
    }

    public static TerrainResidencyKey FromChunk(TerrainChunkKey chunk, byte lodLevel)
    {
        lodLevel = ClampLodLevel(lodLevel);
        if (lodLevel == 0)
        {
            return Full(chunk);
        }

        var scale = ChunksPerSideForLevel(lodLevel);
        return Section(FloorDiv(chunk.X, scale), FloorDiv(chunk.Z, scale), lodLevel);
    }

    public static byte ClampLodLevel(byte lodLevel) =>
        lodLevel > MaxLodLevel ? MaxLodLevel : lodLevel;

    public static int ChunksPerSideForLevel(byte lodLevel) =>
        1 << ClampLodLevel(lodLevel);

    public static int SampleStepMetersForLevel(byte lodLevel) =>
        1 << ClampLodLevel(lodLevel);

    public static int FloorDiv(int value, int divisor)
    {
        divisor = Math.Max(1, divisor);
        if (value >= 0)
        {
            return value / divisor;
        }

        return -((-value + divisor - 1) / divisor);
    }

    public TerrainChunkLodKind Kind => (TerrainChunkLodKind)LodLevel;

    public bool IsFull => LodLevel == 0;

    public bool IsLod => LodLevel > 0;

    public int ChunksPerSide => ChunksPerSideForLevel(LodLevel);

    public int SampleStepMeters => SampleStepMetersForLevel(LodLevel);

    public int OriginChunkX => X * ChunksPerSide;

    public int OriginChunkZ => Z * ChunksPerSide;

    public int OriginWorldX(int chunkSize = PreviewStageConstants.TerrainChunkSize) =>
        OriginChunkX * chunkSize;

    public int OriginWorldZ(int chunkSize = PreviewStageConstants.TerrainChunkSize) =>
        OriginChunkZ * chunkSize;

    public int SectionWorldSize(int chunkSize = PreviewStageConstants.TerrainChunkSize) =>
        ChunksPerSide * chunkSize;

    public Vector2 CenterXZ(int chunkSize = PreviewStageConstants.TerrainChunkSize)
    {
        var size = SectionWorldSize(chunkSize);
        return new Vector2(OriginWorldX(chunkSize) + size * 0.5f, OriginWorldZ(chunkSize) + size * 0.5f);
    }

    /// <summary>Chebyshev distance in chunk units from camera chunk to this section's AABB.</summary>
    public int ChebyshevDistanceToChunk(TerrainChunkKey cameraChunk)
    {
        var x0 = OriginChunkX;
        var z0 = OriginChunkZ;
        var x1 = x0 + ChunksPerSide - 1;
        var z1 = z0 + ChunksPerSide - 1;
        var dx = cameraChunk.X < x0 ? x0 - cameraChunk.X : cameraChunk.X > x1 ? cameraChunk.X - x1 : 0;
        var dz = cameraChunk.Z < z0 ? z0 - cameraChunk.Z : cameraChunk.Z > z1 ? cameraChunk.Z - z1 : 0;
        return Math.Max(dx, dz);
    }

    /// <summary>Max Chebyshev distance from camera chunk to any corner of this section.</summary>
    public int MaxChebyshevDistanceToChunk(TerrainChunkKey cameraChunk)
    {
        var x0 = OriginChunkX;
        var z0 = OriginChunkZ;
        var x1 = x0 + ChunksPerSide - 1;
        var z1 = z0 + ChunksPerSide - 1;
        var d00 = Math.Max(Math.Abs(x0 - cameraChunk.X), Math.Abs(z0 - cameraChunk.Z));
        var d10 = Math.Max(Math.Abs(x1 - cameraChunk.X), Math.Abs(z0 - cameraChunk.Z));
        var d01 = Math.Max(Math.Abs(x0 - cameraChunk.X), Math.Abs(z1 - cameraChunk.Z));
        var d11 = Math.Max(Math.Abs(x1 - cameraChunk.X), Math.Abs(z1 - cameraChunk.Z));
        return Math.Max(Math.Max(d00, d10), Math.Max(d01, d11));
    }

    /// <summary>True when any chunk of this section lies inside the Full Chebyshev disk.</summary>
    public bool OverlapsFullDisk(TerrainChunkKey cameraChunk, int hardRadiusChunks) =>
        ChebyshevDistanceToChunk(cameraChunk) <= hardRadiusChunks;
}

public enum TerrainChunkLodKind : byte
{
    Full = 0,
    Lod1 = 1,
    Lod2 = 2,
    Lod3 = 3,
    Lod4 = 4,
    Lod5 = 5,
    Lod6 = 6,
    Lod7 = 7,
    /// <summary>Legacy alias for <see cref="Lod1"/>.</summary>
    Lod = Lod1,
}

public static class TerrainChunkLodKindExtensions
{
    public static bool IsLod(this TerrainChunkLodKind kind) => kind != TerrainChunkLodKind.Full;
}

/// <summary>CPU mesh payload produced by Full or Lod section bakers (uploaded on the GL thread).</summary>
public sealed class PreviewTerrainChunkMesh
{
    public required TerrainResidencyKey Key { get; init; }
    public required TerrainChunkLodKind Lod { get; init; }
    public required float[] InterleavedVertices { get; init; }
    public required uint[] Indices { get; init; }
    public long UploadByteLength =>
        (long)InterleavedVertices.Length * sizeof(float) +
        (long)Indices.Length * sizeof(uint);

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
