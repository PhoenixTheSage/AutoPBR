using System.Diagnostics.CodeAnalysis;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Persistent cache contract for baked terrain meshes. Implementations treat failures as cache
/// misses so terrain generation can always fall back to baking.
/// </summary>
public interface ITerrainMeshCache : IDisposable
{
    string RootDirectory { get; }

    bool Contains(in TerrainLodCacheKey key);

    bool TryLoad(
        in TerrainLodCacheKey key,
        [NotNullWhen(true)] out PreviewTerrainChunkMesh? mesh);

    ValueTask<PreviewTerrainChunkMesh?> LoadAsync(
        TerrainLodCacheKey key,
        CancellationToken cancellationToken = default);

    void TryStore(in TerrainLodCacheKey key, PreviewTerrainChunkMesh mesh);

    ValueTask StoreAsync(
        TerrainLodCacheKey key,
        PreviewTerrainChunkMesh mesh,
        CancellationToken cancellationToken = default);

    void Clear();

    TerrainMeshCacheStats GetStats();
}

/// <summary>Point-in-time terrain cache counters and byte usage.</summary>
public readonly record struct TerrainMeshCacheStats(
    long Hits,
    long Misses,
    long Stores,
    long StoreFailures,
    long Recoveries,
    long Evictions,
    long PackBytes,
    int PackCount,
    int IndexedEntries);
