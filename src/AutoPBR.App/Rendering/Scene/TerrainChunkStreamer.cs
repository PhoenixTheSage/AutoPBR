using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Camera-centered terrain residency: Full inside hard Chebyshev radius, Lod in the outer ring,
/// unload past LOD + hysteresis. CPU bakes run on a background worker pool; GL upload is separate.
/// </summary>
public sealed class TerrainChunkStreamer : IDisposable
{
    private static readonly IReadOnlyDictionary<TerrainChunkKey, TerrainChunkLodKind> EmptyDesired =
        new ReadOnlyDictionary<TerrainChunkKey, TerrainChunkLodKind>(
            new Dictionary<TerrainChunkKey, TerrainChunkLodKind>());

    private readonly ConcurrentQueue<PreviewTerrainChunkMesh> _ready = new();
    private readonly ConcurrentDictionary<TerrainChunkKey, TerrainChunkLodKind> _inflight = new();
    private readonly ConcurrentDictionary<TerrainChunkKey, TerrainChunkLodKind> _resident = new();
    private readonly object _desiredLock = new();
    private readonly object _pickLock = new();
    private Dictionary<TerrainChunkKey, TerrainChunkLodKind> _desired = new();
    private IReadOnlyDictionary<TerrainChunkKey, TerrainChunkLodKind> _desiredSnapshot = EmptyDesired;
    private CancellationTokenSource? _cts;
    private Task[]? _workers;
    private int _chunkViewDistance = PreviewStageConstants.TerrainDefaultChunkViewDistance;
    private TerrainChunkKey _cameraChunk;
    private TerrainChunkKey _lastDesiredCameraChunk;
    private int _lastDesiredViewDistance = int.MinValue;
    private PreviewTerrainGrassBakeSettings _grassBakeSettings = PreviewTerrainGrassBakeSettings.BuiltIn;
    private PreviewTerrainWorldGenSettings _worldGenSettings = PreviewTerrainWorldGenSettings.Default;
    private PreviewTerrainVegetationBakePlan? _vegetationBakePlan;
    private bool _disposed;

    public PreviewTerrainGrassBakeSettings GrassBakeSettings
    {
        get
        {
            lock (_desiredLock)
            {
                return _grassBakeSettings;
            }
        }
        set
        {
            lock (_desiredLock)
            {
                _grassBakeSettings = value;
            }
        }
    }

    public PreviewTerrainVegetationBakePlan? VegetationBakePlan
    {
        get
        {
            lock (_desiredLock)
            {
                return _vegetationBakePlan;
            }
        }
        set
        {
            lock (_desiredLock)
            {
                _vegetationBakePlan = value;
            }
        }
    }

    public PreviewTerrainWorldGenSettings WorldGenSettings
    {
        get
        {
            lock (_desiredLock)
            {
                return _worldGenSettings;
            }
        }
        set
        {
            lock (_desiredLock)
            {
                _worldGenSettings = PreviewTerrainWorldGenSettings.Resolve(value);
            }
        }
    }

    public int ChunkViewDistance
    {
        get => _chunkViewDistance;
        set => _chunkViewDistance = Math.Clamp(
            value,
            PreviewStageConstants.TerrainMinChunkViewDistance,
            PreviewStageConstants.TerrainMaxChunkViewDistance);
    }

    public int HardRadiusChunks => ChunkViewDistance;

    public int LodRadiusChunks => ChunkViewDistance + PreviewStageConstants.TerrainLodRingChunks;

    public int UnloadRadiusChunks =>
        LodRadiusChunks + PreviewStageConstants.TerrainUnloadHysteresisChunks;

    public float LodRingWorldRadius =>
        LodRadiusChunks * (float)PreviewStageConstants.TerrainChunkSize;

    public float HardRingWorldRadius =>
        HardRadiusChunks * (float)PreviewStageConstants.TerrainChunkSize;

    public TerrainChunkKey CameraChunk => _cameraChunk;

    public int WorkerCount => _workers?.Length ?? 0;

    public static int ResolveWorkerCount() =>
        Math.Clamp(
            Environment.ProcessorCount - 1,
            1,
            PreviewStageConstants.TerrainMaxBakeWorkers);

    public void Start()
    {
        if (_workers is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var n = ResolveWorkerCount();
        var workers = new Task[n];
        for (var i = 0; i < n; i++)
        {
            workers[i] = Task.Factory.StartNew(
                () => WorkerLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        _workers = workers;
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_workers is not null)
        {
            try
            {
                Task.WaitAll(_workers, TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }

        _workers = null;
        _cts?.Dispose();
        _cts = null;
        while (_ready.TryDequeue(out _))
        {
        }

        _inflight.Clear();
        _resident.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    public void Tick(Vector3 eye, int chunkViewDistance)
    {
        ChunkViewDistance = chunkViewDistance;
        var cam = TerrainChunkKey.FromWorld(eye.X, eye.Z);
        _cameraChunk = cam;

        if (cam.Equals(_lastDesiredCameraChunk) && ChunkViewDistance == _lastDesiredViewDistance)
        {
            return;
        }

        var hard = HardRadiusChunks;
        var lod = LodRadiusChunks;
        var next = new Dictionary<TerrainChunkKey, TerrainChunkLodKind>((2 * lod + 1) * (2 * lod + 1));
        for (var dz = -lod; dz <= lod; dz++)
        {
            for (var dx = -lod; dx <= lod; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dz));
                var key = new TerrainChunkKey(cam.X + dx, cam.Z + dz);
                next[key] = dist <= hard ? TerrainChunkLodKind.Full : TerrainChunkLodKind.Lod;
            }
        }

        lock (_desiredLock)
        {
            _desired = next;
            _desiredSnapshot = next;
        }

        _lastDesiredCameraChunk = cam;
        _lastDesiredViewDistance = ChunkViewDistance;
    }

    public IReadOnlyDictionary<TerrainChunkKey, TerrainChunkLodKind> SnapshotDesired()
    {
        lock (_desiredLock)
        {
            return _desiredSnapshot;
        }
    }

    public bool ShouldUnload(TerrainChunkKey key) =>
        key.ChebyshevDistanceTo(_cameraChunk) > UnloadRadiusChunks;

    public void NotifyUploaded(TerrainChunkKey key, TerrainChunkLodKind lod) =>
        _resident[key] = lod;

    public void NotifyUnloaded(TerrainChunkKey key)
    {
        _resident.TryRemove(key, out _);
        _inflight.TryRemove(key, out _);
    }

    public void InvalidateForRebuild(TerrainChunkKey key)
    {
        _resident.TryRemove(key, out _);
        _inflight.TryRemove(key, out _);
    }

    /// <summary>Drop residency and queued bakes so the next tick rebakes all desired chunks.</summary>
    public void InvalidateAll()
    {
        while (_ready.TryDequeue(out _))
        {
        }

        _inflight.Clear();
        _resident.Clear();
        _lastDesiredCameraChunk = default;
        _lastDesiredViewDistance = int.MinValue;
    }

    public int DrainReady(List<PreviewTerrainChunkMesh> destination, int maxCount)
    {
        var n = 0;
        while (n < maxCount && _ready.TryDequeue(out var mesh))
        {
            _inflight.TryRemove(mesh.Key, out _);
            destination.Add(mesh);
            n++;
        }

        return n;
    }

    public static bool NeedsRebuild(TerrainChunkLodKind resident, TerrainChunkLodKind desired) =>
        resident != desired;

    private void WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TerrainChunkKey jobKey;
            TerrainChunkLodKind needLod;
            var haveJob = false;
            lock (_pickLock)
            {
                if (TryPickJob(out jobKey, out needLod) && _inflight.TryAdd(jobKey, needLod))
                {
                    haveJob = true;
                }
                else
                {
                    jobKey = default;
                    needLod = default;
                }
            }

            if (!haveJob)
            {
                try
                {
                    Task.Delay(8, ct).Wait(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                // Drop stale resident so GL replaces after upload.
                _resident.TryRemove(jobKey, out _);

                var grassSettings = GrassBakeSettings;
                var worldGen = WorldGenSettings;
                var vegetation = VegetationBakePlan;
                PreviewTerrainChunkMesh? mesh = needLod == TerrainChunkLodKind.Full
                    ? PreviewTerrainMeshBaker.BakeFullChunk(jobKey, grassSettings, worldGen, vegetation)
                    : PreviewTerrainLodMeshBaker.BakeLodChunk(jobKey, worldGen);

                if (mesh is not null)
                {
                    _ready.Enqueue(mesh);
                }
                else
                {
                    _inflight.TryRemove(jobKey, out _);
                }
            }
            catch
            {
                _inflight.TryRemove(jobKey, out _);
            }
        }
    }

    private bool TryPickJob(out TerrainChunkKey key, out TerrainChunkLodKind lod)
    {
        key = default;
        lod = TerrainChunkLodKind.Full;
        Dictionary<TerrainChunkKey, TerrainChunkLodKind> desired;
        lock (_desiredLock)
        {
            desired = _desired;
        }

        TerrainChunkKey? bestCoreFull = null;
        TerrainChunkKey? bestFull = null;
        TerrainChunkKey? bestLod = null;
        var bestCoreDist = int.MaxValue;
        var bestFullDist = int.MaxValue;
        var bestLodDist = int.MaxValue;

        foreach (var (k, want) in desired)
        {
            if (_inflight.ContainsKey(k))
            {
                continue;
            }

            if (_resident.TryGetValue(k, out var have) && have == want)
            {
                continue;
            }

            var dist = k.ChebyshevDistanceTo(_cameraChunk);
            if (want == TerrainChunkLodKind.Full)
            {
                // Prefer a 3×3 Full core under the eye after large camera jumps.
                if (dist <= 1)
                {
                    if (dist < bestCoreDist)
                    {
                        bestCoreDist = dist;
                        bestCoreFull = k;
                    }
                }
                else if (dist < bestFullDist)
                {
                    bestFullDist = dist;
                    bestFull = k;
                }
            }
            else if (dist < bestLodDist)
            {
                bestLodDist = dist;
                bestLod = k;
            }
        }

        if (bestCoreFull is not null)
        {
            key = bestCoreFull.Value;
            lod = TerrainChunkLodKind.Full;
            return true;
        }

        if (bestFull is not null)
        {
            key = bestFull.Value;
            lod = TerrainChunkLodKind.Full;
            return true;
        }

        if (bestLod is not null)
        {
            key = bestLod.Value;
            lod = TerrainChunkLodKind.Lod;
            return true;
        }

        return false;
    }
}
