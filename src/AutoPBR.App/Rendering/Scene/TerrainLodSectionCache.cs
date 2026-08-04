using System.Diagnostics.CodeAnalysis;

using AutoPBR.Preview;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// In-memory CPU cache for combined LOD section meshes. Cleared on seed / mod bake /
/// shader invalidation / manual Clear LOD Cache.
/// </summary>
public sealed class TerrainLodSectionCache
{
    private readonly object _gate = new();
    private readonly Dictionary<TerrainLodCacheKey, Entry> _entries = new();
    private readonly LinkedList<TerrainLodCacheKey> _lru = new();
    private long _totalBytes;
    private long _hits;
    private long _misses;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    public long Hits
    {
        get
        {
            lock (_gate)
            {
                return _hits;
            }
        }
    }

    public long Misses
    {
        get
        {
            lock (_gate)
            {
                return _misses;
            }
        }
    }

    public bool TryGet(in TerrainLodCacheKey key, [NotNullWhen(true)] out PreviewTerrainChunkMesh? mesh)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                _hits++;
                TouchLru(entry.Node);
                mesh = entry.Mesh;
                return true;
            }

            _misses++;
            mesh = null;
            return false;
        }
    }

    public void Store(in TerrainLodCacheKey key, PreviewTerrainChunkMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!mesh.Key.IsLod)
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _totalBytes -= existing.Mesh.UploadByteLength;
                _lru.Remove(existing.Node);
                _entries.Remove(key);
            }

            var node = _lru.AddLast(key);
            _entries[key] = new Entry(mesh, node);
            _totalBytes += mesh.UploadByteLength;
            EvictOverflow();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _totalBytes = 0;
            _hits = 0;
            _misses = 0;
        }
    }

    private void TouchLru(LinkedListNode<TerrainLodCacheKey> node)
    {
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private void EvictOverflow()
    {
        while ((_entries.Count > PreviewStageConstants.TerrainLodCacheMaxEntries ||
                _totalBytes > PreviewStageConstants.TerrainLodCacheMaxBytes) &&
               _lru.First is { } oldest)
        {
            var key = oldest.Value;
            _lru.RemoveFirst();
            if (_entries.Remove(key, out var entry))
            {
                _totalBytes -= entry.Mesh.UploadByteLength;
            }
        }

        if (_totalBytes < 0)
        {
            _totalBytes = 0;
        }
    }

    private readonly record struct Entry(
        PreviewTerrainChunkMesh Mesh,
        LinkedListNode<TerrainLodCacheKey> Node);
}

public readonly record struct TerrainLodCacheKey(
    TerrainResidencyKey Residency,
    TerrainLodCacheFingerprint Fingerprint);

/// <summary>Stable identity for worldgen + grass/veg bake inputs that affect LOD meshes.</summary>
public readonly record struct TerrainLodCacheFingerprint(
    int Seed,
    float BiomeSize,
    float Amplification,
    float ErosionStrength,
    float Continentalness,
    PreviewTerrainGrassMode Mode,
    bool BetterGrassEnabled,
    bool EmitOverlay,
    bool HasStone,
    bool HasSand,
    bool HasGravel,
    string VegetationIdentity,
    int BakeRevision = 0)
{
    /// <summary>Bump when LOD mesh topology/sampling contract changes (skirts, max-height cells, …).</summary>
    public const int CurrentBakeRevision = 6;

    public static TerrainLodCacheFingerprint From(
        in PreviewTerrainWorldGenSettings worldGen,
        in PreviewTerrainGrassBakeSettings grass,
        PreviewTerrainVegetationBakePlan? vegetation)
    {
        var gen = PreviewTerrainWorldGenSettings.Resolve(worldGen);
        var vegId = vegetation is { HasAny: true }
            ? vegetation.Identity
            : grass.VegetationIdentity ?? "";
        return new TerrainLodCacheFingerprint(
            gen.Seed,
            gen.BiomeSize,
            gen.Amplification,
            gen.ErosionStrength,
            gen.Continentalness,
            grass.Mode,
            grass.BetterGrassEnabled,
            grass.EmitOverlay,
            grass.HasStone,
            grass.HasSand,
            grass.HasGravel,
            vegId,
            CurrentBakeRevision);
    }
}
