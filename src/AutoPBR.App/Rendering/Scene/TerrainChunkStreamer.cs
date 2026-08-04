using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Camera-centered terrain residency: Full inside hard Chebyshev radius, combined LOD sections
/// (2×2 / 4×4 / 8×8) in outer bands, unload past LOD + hysteresis. CPU bakes run on a
/// background worker pool with an in-memory LOD section cache; GL upload is separate.
/// Bake picks follow a clockwise annular soft-start schedule (see <see cref="TerrainStreamSchedule"/>).
/// </summary>
public sealed class TerrainChunkStreamer : IDisposable
{
    private static readonly IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> EmptyDesired =
        new ReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind>(
            new Dictionary<TerrainResidencyKey, TerrainChunkLodKind>());

    private readonly ConcurrentQueue<PreviewTerrainChunkMesh> _ready = new();
    private readonly ConcurrentDictionary<TerrainResidencyKey, TerrainChunkLodKind> _inflight = new();
    private readonly ConcurrentDictionary<TerrainResidencyKey, TerrainChunkLodKind> _resident = new();
    private readonly TerrainLodSectionCache _lodCache = new();
    private readonly object _desiredLock = new();
    private readonly object _pickLock = new();
    private Dictionary<TerrainResidencyKey, TerrainChunkLodKind> _desired = new();
    private IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> _desiredSnapshot = EmptyDesired;
    private CancellationTokenSource? _cts;
    private Task[]? _workers;
    private int _chunkViewDistance = PreviewStageConstants.TerrainDefaultChunkViewDistance;
    private int _lodRingChunks = PreviewStageConstants.TerrainDefaultLodRingChunks;
    private TerrainChunkKey _cameraChunk;
    private TerrainChunkKey _lastDesiredCameraChunk;
    private int _lastDesiredViewDistance = int.MinValue;
    private int _lastDesiredLodRingChunks = int.MinValue;
    private int _scheduleMaxRing = PreviewStageConstants.TerrainStreamSoftStartInitialRing;
    private bool _holdScheduleExpansion;
    private PreviewTerrainGrassBakeSettings _grassBakeSettings = PreviewTerrainGrassBakeSettings.BuiltIn;
    private PreviewTerrainWorldGenSettings _worldGenSettings = PreviewTerrainWorldGenSettings.Default;
    private PreviewTerrainVegetationBakePlan? _vegetationBakePlan;
    private bool _disposed;

    public TerrainLodSectionCache LodCache => _lodCache;

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

    public int LodRingChunks
    {
        get => _lodRingChunks;
        set => _lodRingChunks = Math.Clamp(
            value,
            PreviewStageConstants.TerrainMinLodRingChunks,
            PreviewStageConstants.TerrainMaxLodRingChunks);
    }

    public int HardRadiusChunks => ChunkViewDistance;

    public int LodRadiusChunks => ChunkViewDistance + LodRingChunks;

    public int UnloadRadiusChunks
    {
        get
        {
            var levels = ResolveActiveLodLevelCount(LodRingChunks);
            var coarsestScale = TerrainResidencyKey.ChunksPerSideForLevel((byte)levels);
            var hysteresis = Math.Max(
                PreviewStageConstants.TerrainUnloadHysteresisChunks,
                coarsestScale / 2);
            return LodRadiusChunks + hysteresis;
        }
    }

    public float LodRingWorldRadius =>
        LodRadiusChunks * (float)PreviewStageConstants.TerrainChunkSize;

    public float HardRingWorldRadius =>
        HardRadiusChunks * (float)PreviewStageConstants.TerrainChunkSize;

    public TerrainChunkKey CameraChunk => _cameraChunk;

    /// <summary>
    /// Soft-start Chebyshev unlock radius. Keys with ring greater than this are not baked yet
    /// (and should not be mass-parked as fake-resident by the GL budget path).
    /// </summary>
    public int ScheduleMaxRing
    {
        get
        {
            lock (_pickLock)
            {
                return _scheduleMaxRing;
            }
        }
    }

    /// <summary>
    /// When true, soft-start will not unlock further Chebyshev rings (VRAM pressure hold).
    /// </summary>
    public bool HoldScheduleExpansion
    {
        get
        {
            lock (_pickLock)
            {
                return _holdScheduleExpansion;
            }
        }
        set
        {
            lock (_pickLock)
            {
                _holdScheduleExpansion = value;
            }
        }
    }

    public int WorkerCount => _workers?.Length ?? 0;

    public readonly record struct LodBand(byte Level, int DMin, int DMax);

    public static int ResolveWorkerCount() =>
        Math.Clamp(
            Environment.ProcessorCount - 1,
            1,
            PreviewStageConstants.TerrainMaxBakeWorkers);

    /// <summary>
    /// How many LOD levels to use for a ring. Grows with log2(ring) up to
    /// <see cref="TerrainResidencyKey.MaxLodLevel"/> so extreme rings get 128×128 sections.
    /// </summary>
    public static int ResolveActiveLodLevelCount(int lodRingChunks)
    {
        lodRingChunks = Math.Max(0, lodRingChunks);
        if (lodRingChunks <= 0)
        {
            return 0;
        }

        // floor(log2(ring)) for ring>=2; ring=9 → 3, ring=128 → 7, ring=1024 → 10→7
        var log = 0;
        var v = Math.Max(2, lodRingChunks);
        while (v > 1)
        {
            v >>= 1;
            log++;
        }

        return Math.Clamp(log, 1, TerrainResidencyKey.MaxLodLevel);
    }

    /// <summary>
    /// Fills <paramref name="bands"/> with inclusive Chebyshev distance bands (camera-relative)
    /// for LOD1..N. Near bands stay thin (~a few section widths); the outermost level absorbs
    /// the bulk so extreme rings (256–1024) stay residency-tractable.
    /// </summary>
    public static int ResolveLodBands(int hardRadius, int lodRingChunks, Span<LodBand> bands)
    {
        hardRadius = Math.Max(0, hardRadius);
        lodRingChunks = Math.Max(0, lodRingChunks);
        if (lodRingChunks == 0 || bands.IsEmpty)
        {
            return 0;
        }

        var levelCount = Math.Min(ResolveActiveLodLevelCount(lodRingChunks), bands.Length);
        var d = hardRadius + 1;
        var lodRadius = hardRadius + lodRingChunks;
        var written = 0;
        for (byte level = 1; level <= levelCount; level++)
        {
            var remainingLevels = levelCount - level + 1;
            var remaining = lodRadius - d + 1;
            if (remaining <= 0)
            {
                break;
            }

            int width;
            if (level == levelCount)
            {
                width = remaining;
            }
            else
            {
                var scale = TerrainResidencyKey.ChunksPerSideForLevel(level);
                // ~4 section widths, but bias leftover distance toward coarser outer levels.
                var target = scale * 4;
                var fairShare = Math.Max(scale * 2, remaining / (remainingLevels + 2));
                width = Math.Min(target, fairShare);
                width = Math.Max(scale, width);
                width = Math.Min(width, remaining - (remainingLevels - 1));
                width = Math.Max(1, width);
            }

            var dMax = Math.Min(lodRadius, d + width - 1);
            bands[written++] = new LodBand(level, d, dMax);
            d = dMax + 1;
        }

        return written;
    }

    /// <summary>
    /// Legacy helper: first three band ends (LOD1/2/3). Missing bands repeat the previous end.
    /// </summary>
    public static void ResolveLodBandEnds(int hardRadius, int lodRingChunks, out int lod1End, out int lod2End, out int lod3End)
    {
        Span<LodBand> bands = stackalloc LodBand[TerrainResidencyKey.MaxLodLevel];
        var n = ResolveLodBands(hardRadius, lodRingChunks, bands);
        lod1End = n >= 1 ? bands[0].DMax : hardRadius;
        lod2End = n >= 2 ? bands[1].DMax : lod1End;
        lod3End = n >= 3 ? bands[2].DMax : lod2End;
    }

    /// <summary>
    /// Inclusive Chebyshev distance (chunks from camera) where <paramref name="lodLevel"/> begins.
    /// Full returns 0; unknown levels return the hard radius + 1.
    /// </summary>
    public static int ResolveLodBandStartChunks(int hardRadius, int lodRingChunks, byte lodLevel)
    {
        if (lodLevel == 0)
        {
            return 0;
        }

        Span<LodBand> bands = stackalloc LodBand[TerrainResidencyKey.MaxLodLevel];
        var n = ResolveLodBands(hardRadius, lodRingChunks, bands);
        for (var i = 0; i < n; i++)
        {
            if (bands[i].Level == lodLevel)
            {
                return bands[i].DMin;
            }
        }

        return hardRadius + 1;
    }

    /// <summary>
    /// Inclusive Chebyshev distance where <paramref name="lodLevel"/> ends (Full → hard radius).
    /// Unknown / missing levels return -1.
    /// </summary>
    public static int ResolveLodBandEndChunks(int hardRadius, int lodRingChunks, byte lodLevel)
    {
        if (lodLevel == 0)
        {
            return Math.Max(0, hardRadius);
        }

        Span<LodBand> bands = stackalloc LodBand[TerrainResidencyKey.MaxLodLevel];
        var n = ResolveLodBands(hardRadius, lodRingChunks, bands);
        for (var i = 0; i < n; i++)
        {
            if (bands[i].Level == lodLevel)
            {
                return bands[i].DMax;
            }
        }

        return -1;
    }

    /// <summary>
    /// Chunks of coarser LOD pulled under a finer level so dithered fade-out never reveals sky.
    /// </summary>
    public static int ResolveLodFadeOverlapChunks()
    {
        var chunkSize = Math.Max(1, PreviewStageConstants.TerrainChunkSize);
        return Math.Max(
            2,
            (int)MathF.Ceiling(PreviewStageConstants.TerrainLodDetailFadeWidthMeters / chunkSize) + 1);
    }

    /// <summary>
    /// True when <paramref name="lodLevel"/> is the outermost active band (must stay fully opaque).
    /// </summary>
    public static bool IsOutermostLodLevel(int hardRadius, int lodRingChunks, byte lodLevel)
    {
        if (lodLevel == 0 || lodRingChunks <= 0)
        {
            return false;
        }

        Span<LodBand> bands = stackalloc LodBand[TerrainResidencyKey.MaxLodLevel];
        var n = ResolveLodBands(hardRadius, lodRingChunks, bands);
        return n > 0 && bands[n - 1].Level == lodLevel;
    }

    /// <summary>
    /// World-meter fade-out window for a finer detail level at its outer edge. Coarser underlay
    /// stays opaque; discard dither runs on this level only.
    /// </summary>
    public static void ResolveLodDetailFadeMeters(
        int hardRadius,
        int lodRingChunks,
        byte lodLevel,
        out float fadeStartMeters,
        out float fadeEndMeters)
    {
        var chunkSize = (float)PreviewStageConstants.TerrainChunkSize;
        var fadeWidth = PreviewStageConstants.TerrainLodDetailFadeWidthMeters;
        var bandEnd = ResolveLodBandEndChunks(hardRadius, lodRingChunks, lodLevel);
        if (bandEnd < 0)
        {
            fadeStartMeters = 0f;
            fadeEndMeters = 0f;
            return;
        }

        fadeEndMeters = bandEnd * chunkSize;
        fadeStartMeters = Math.Max(0f, fadeEndMeters - fadeWidth);
    }

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
        _lodCache.Clear();
    }

    public void Tick(Vector3 eye, int chunkViewDistance, int? lodRingChunks = null)
    {
        ChunkViewDistance = chunkViewDistance;
        if (lodRingChunks is int ring)
        {
            LodRingChunks = ring;
        }

        var cam = TerrainChunkKey.FromWorld(eye.X, eye.Z);
        _cameraChunk = cam;

        if (cam.Equals(_lastDesiredCameraChunk) &&
            ChunkViewDistance == _lastDesiredViewDistance &&
            LodRingChunks == _lastDesiredLodRingChunks)
        {
            return;
        }

        var next = BuildDesiredResidency(cam, HardRadiusChunks, LodRingChunks);
        lock (_desiredLock)
        {
            _desired = next;
            _desiredSnapshot = next;
        }

        lock (_pickLock)
        {
            // Preserve soft-start unlock progress across camera moves. Resetting to HardRadius
            // every chunk step left a permanent void behind the Full disk while moving
            // (LOD never stayed unlocked long enough to fill). Floor at hard radius; cap at lod.
            _scheduleMaxRing = Math.Clamp(
                Math.Max(_scheduleMaxRing, HardRadiusChunks),
                HardRadiusChunks,
                Math.Max(HardRadiusChunks, LodRadiusChunks));
        }

        _lastDesiredCameraChunk = cam;
        _lastDesiredViewDistance = ChunkViewDistance;
        _lastDesiredLodRingChunks = LodRingChunks;
    }

    public static Dictionary<TerrainResidencyKey, TerrainChunkLodKind> BuildDesiredResidency(
        TerrainChunkKey cam,
        int hardRadius,
        int lodRingChunks)
    {
        hardRadius = Math.Max(0, hardRadius);
        lodRingChunks = Math.Max(0, lodRingChunks);
        var fullSide = 2 * hardRadius + 1;
        var capacity = fullSide * fullSide + 256;
        var next = new Dictionary<TerrainResidencyKey, TerrainChunkLodKind>(capacity);

        for (var dz = -hardRadius; dz <= hardRadius; dz++)
        {
            for (var dx = -hardRadius; dx <= hardRadius; dx++)
            {
                next[TerrainResidencyKey.Full(cam.X + dx, cam.Z + dz)] = TerrainChunkLodKind.Full;
            }
        }

        if (lodRingChunks <= 0)
        {
            return next;
        }

        Span<LodBand> bands = stackalloc LodBand[TerrainResidencyKey.MaxLodLevel];
        var bandCount = ResolveLodBands(hardRadius, lodRingChunks, bands);
        for (var i = 0; i < bandCount; i++)
        {
            var band = bands[i];
            AddLodSections(next, cam, hardRadius, band.DMin, band.DMax, band.Level);
        }

        return next;
    }

    private static void AddLodSections(
        Dictionary<TerrainResidencyKey, TerrainChunkLodKind> desired,
        TerrainChunkKey cam,
        int hardRadius,
        int dMin,
        int dMax,
        byte lodLevel)
    {
        if (dMax < dMin)
        {
            return;
        }

        var scale = TerrainResidencyKey.ChunksPerSideForLevel(lodLevel);
        var kind = (TerrainChunkLodKind)lodLevel;
        var overlap = ResolveLodFadeOverlapChunks();
        // Pull coarser sections under the finer band so fade-out dither has solid underlay.
        var underlayDMin = Math.Max(0, dMin - overlap);
        // Keep a solid Full core; only the outer Full ring may share coverage with LOD1.
        var fullCoreExclusive = Math.Max(-1, hardRadius - overlap);
        // Iterate section coordinates covering the Chebyshev AABB — not every chunk cell.
        var sMinX = TerrainResidencyKey.FloorDiv(cam.X - dMax, scale);
        var sMaxX = TerrainResidencyKey.FloorDiv(cam.X + dMax, scale);
        var sMinZ = TerrainResidencyKey.FloorDiv(cam.Z - dMax, scale);
        var sMaxZ = TerrainResidencyKey.FloorDiv(cam.Z + dMax, scale);
        for (var sz = sMinZ; sz <= sMaxZ; sz++)
        {
            for (var sx = sMinX; sx <= sMaxX; sx++)
            {
                var section = TerrainResidencyKey.Section(sx, sz, lodLevel);
                if (desired.ContainsKey(section))
                {
                    continue;
                }

                // Skip only when the entire section lies inside the Full core (inside the fade
                // underlay ring). Straddling + underlay sections stay resident.
                if (fullCoreExclusive >= 0 &&
                    section.MaxChebyshevDistanceToChunk(cam) <= fullCoreExclusive)
                {
                    continue;
                }

                if (!SectionIntersectsRing(section, cam, underlayDMin, dMax))
                {
                    continue;
                }

                desired[section] = kind;
            }
        }
    }

    private static bool SectionIntersectsRing(
        TerrainResidencyKey section,
        TerrainChunkKey cam,
        int dMin,
        int dMax)
    {
        var closest = section.ChebyshevDistanceToChunk(cam);
        var farthest = section.MaxChebyshevDistanceToChunk(cam);
        return closest <= dMax && farthest >= dMin;
    }

    public IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> SnapshotDesired()
    {
        lock (_desiredLock)
        {
            return _desiredSnapshot;
        }
    }

    public bool ShouldUnload(TerrainResidencyKey key) =>
        key.ChebyshevDistanceToChunk(_cameraChunk) > UnloadRadiusChunks;

    public void NotifyUploaded(TerrainResidencyKey key, TerrainChunkLodKind lod) =>
        _resident[key] = lod;

    public void NotifyUnloaded(TerrainResidencyKey key)
    {
        _resident.TryRemove(key, out _);
        _inflight.TryRemove(key, out _);
    }

    public void InvalidateForRebuild(TerrainResidencyKey key)
    {
        _resident.TryRemove(key, out _);
        _inflight.TryRemove(key, out _);
    }

    /// <summary>Drop residency, queued bakes, and the CPU LOD cache so the next tick rebakes.</summary>
    public void InvalidateAll()
    {
        while (_ready.TryDequeue(out _))
        {
        }

        _inflight.Clear();
        _resident.Clear();
        _lodCache.Clear();
        _lastDesiredCameraChunk = default;
        _lastDesiredViewDistance = int.MinValue;
        _lastDesiredLodRingChunks = int.MinValue;
        lock (_pickLock)
        {
            _scheduleMaxRing = Math.Max(
                HardRadiusChunks,
                PreviewStageConstants.TerrainStreamSoftStartInitialRing);
            _holdScheduleExpansion = false;
        }
    }

    /// <summary>Clear only the CPU LOD section cache (residency rebuild still required for GPU).</summary>
    public void ClearLodCache() => _lodCache.Clear();

    public int DrainReady(List<PreviewTerrainChunkMesh> destination, int maxCount)
        => DrainReady(destination, maxCount, long.MaxValue);

    public int DrainReady(
        List<PreviewTerrainChunkMesh> destination,
        int maxCount,
        long maxBytes)
    {
        var n = 0;
        long bytes = 0;
        maxBytes = Math.Max(1, maxBytes);
        while (n < maxCount && _ready.TryPeek(out var next))
        {
            if (n > 0 && bytes + next.UploadByteLength > maxBytes)
            {
                break;
            }

            if (!_ready.TryDequeue(out var mesh))
            {
                continue;
            }

            _inflight.TryRemove(mesh.Key, out _);
            destination.Add(mesh);
            bytes += mesh.UploadByteLength;
            n++;
        }

        return n;
    }

    public static bool NeedsRebuild(TerrainChunkLodKind resident, TerrainChunkLodKind desired) =>
        resident != desired;

    /// <summary>
    /// Non-desired multi-chunk LOD that still overlaps the Full disk must unload immediately.
    /// Closest-point Chebyshev is 0 while the camera is inside the section, so distance
    /// hysteresis never fires and obsolete LOD stays stuck under approaching Full detail.
    /// </summary>
    public static bool IsObsoleteLodUnderFullDisk(
        TerrainResidencyKey key,
        bool inDesired,
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks) =>
        !inDesired && key.IsLod && key.OverlapsFullDisk(cameraChunk, hardRadiusChunks);

    /// <summary>
    /// Disposal rank (higher disposed first): obsolete LOD under Full before the far trail.
    /// </summary>
    public static int RankGpuDisposal(
        TerrainResidencyKey key,
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks,
        bool inDesired)
    {
        var dist = key.ChebyshevDistanceToChunk(cameraChunk);
        if (IsObsoleteLodUnderFullDisk(key, inDesired, cameraChunk, hardRadiusChunks))
        {
            // Prefer largest under-eye sections first so Full can claim the pad quickly.
            return int.MaxValue / 2 + key.ChunksPerSide * 4096 - dist;
        }

        return dist;
    }

    private void WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TerrainResidencyKey jobKey;
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
                _resident.TryRemove(jobKey, out _);

                var grassSettings = GrassBakeSettings;
                var worldGen = WorldGenSettings;
                var vegetation = VegetationBakePlan;
                PreviewTerrainChunkMesh? mesh;

                if (needLod == TerrainChunkLodKind.Full)
                {
                    mesh = PreviewTerrainMeshBaker.BakeFullChunk(
                        new TerrainChunkKey(jobKey.X, jobKey.Z),
                        grassSettings,
                        worldGen,
                        vegetation);
                }
                else
                {
                    var fingerprint = TerrainLodCacheFingerprint.From(worldGen, grassSettings, vegetation);
                    var cacheKey = new TerrainLodCacheKey(jobKey, fingerprint);
                    if (_lodCache.TryGet(cacheKey, out mesh))
                    {
                        _ready.Enqueue(mesh);
                        continue;
                    }

                    mesh = PreviewTerrainLodMeshBaker.BakeLodSection(
                        jobKey,
                        worldGen,
                        grassSettings,
                        vegetation);
                    if (mesh is not null)
                    {
                        _lodCache.Store(cacheKey, mesh);
                    }
                }

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

    private bool TryPickJob(out TerrainResidencyKey key, out TerrainChunkLodKind lod)
    {
        key = default;
        lod = TerrainChunkLodKind.Full;
        Dictionary<TerrainResidencyKey, TerrainChunkLodKind> desired;
        lock (_desiredLock)
        {
            desired = _desired;
        }

        var cam = _cameraChunk;
        var maxRing = Math.Max(_scheduleMaxRing, HardRadiusChunks);
        if (maxRing != _scheduleMaxRing)
        {
            _scheduleMaxRing = maxRing;
        }

        var lodCap = LodRadiusChunks;
        TerrainResidencyKey? best = null;
        TerrainStreamSchedule.Rank bestRank = default;
        var haveBest = false;
        var pendingInWindow = false;

        foreach (var (k, want) in desired)
        {
            var ring = TerrainStreamSchedule.RingIndex(k, cam);
            // Full is always eligible (approach must swap LOD→Full). Soft-start gates LOD only.
            if (want != TerrainChunkLodKind.Full && ring > maxRing)
            {
                continue;
            }

            if (_inflight.ContainsKey(k))
            {
                pendingInWindow = true;
                continue;
            }

            if (_resident.TryGetValue(k, out var have) && have == want)
            {
                continue;
            }

            pendingInWindow = true;
            var rank = TerrainStreamSchedule.RankKey(k, cam);
            if (!haveBest || TerrainStreamSchedule.Compare(rank, bestRank) < 0)
            {
                haveBest = true;
                bestRank = rank;
                best = k;
                lod = want;
            }
        }

        if (haveBest && best is not null)
        {
            key = best.Value;
            return true;
        }

        // Soft-start: unlock the next LOD ring when Full + unlocked LOD are saturated.
        if (!pendingInWindow && maxRing < lodCap && !_holdScheduleExpansion)
        {
            _scheduleMaxRing = Math.Min(maxRing + 1, lodCap);
        }

        return false;
    }

    /// <summary>
    /// Test hook: force soft-start unlock radius (clamped to lod radius).
    /// </summary>
    internal void SetScheduleMaxRingForTests(int maxRing)
    {
        lock (_pickLock)
        {
            _scheduleMaxRing = Math.Clamp(maxRing, 0, Math.Max(0, LodRadiusChunks));
        }
    }

    /// <summary>
    /// Test hook: pick next bake job under the same lock workers use.
    /// </summary>
    internal bool TryPickJobForTests(out TerrainResidencyKey key, out TerrainChunkLodKind lod)
    {
        lock (_pickLock)
        {
            return TryPickJob(out key, out lod);
        }
    }
}
