using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Camera-centered terrain residency: Full inside hard Chebyshev radius, combined LOD sections
/// (2×2 / 4×4 / 8×8) in outer bands, unload past LOD + hysteresis. CPU bakes run on a
/// background worker pool with an in-memory LOD section cache; GL upload is separate.
/// Bake picks follow a clockwise annular soft-start schedule (see <see cref="TerrainStreamSchedule"/>).
/// When the GPU desired window is idle, workers also disk-warm every active LOD level over the
/// lod ring so approach/recede swaps hit cache instead of live-baking a missing band.
/// </summary>
public sealed class TerrainChunkStreamer : IDisposable
{
    private static readonly IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> EmptyDesired =
        new ReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind>(
            new Dictionary<TerrainResidencyKey, TerrainChunkLodKind>());

    private readonly ConcurrentQueue<PreviewTerrainChunkMesh> _ready = new();
    private readonly ConcurrentQueue<TerrainGpuFullJob> _gpuFullJobs = new();
    private readonly ConcurrentQueue<TerrainGpuLodJob> _gpuLodJobs = new();
    private readonly ConcurrentDictionary<TerrainResidencyKey, TerrainChunkLodKind> _inflight = new();
    private readonly ConcurrentDictionary<TerrainResidencyKey, TerrainChunkLodKind> _resident = new();
    private readonly TerrainLodSectionCache _lodCache = new();
    private readonly TerrainLodDiskCache _lodDiskCache;
    private readonly object _desiredLock = new();
    private readonly object _pickLock = new();
    private Dictionary<TerrainResidencyKey, TerrainChunkLodKind> _desired = new();
    private IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> _desiredSnapshot = EmptyDesired;
    private Dictionary<TerrainResidencyKey, TerrainChunkLodKind> _diskPrefetch = new();
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
    private int _pickLaneCounter;
    private PreviewTerrainGrassBakeSettings _grassBakeSettings = PreviewTerrainGrassBakeSettings.BuiltIn;
    private PreviewTerrainWorldGenSettings _worldGenSettings = PreviewTerrainWorldGenSettings.Default;
    private PreviewTerrainVegetationBakePlan? _vegetationBakePlan;
    private bool _preferGpuFullMeshing;
    private bool _preferGpuLodMeshing;
    private long _fullBakeCompletedCount;
    private long _lodBakeCompletedCount;
    private long _diskWarmCompletedCount;
    private long _bakeFaultCount;
    private long _lastFullBakeMilliseconds;
    private string? _lastBakeFault;
    private bool _disposed;

    public TerrainChunkStreamer()
        : this(diskCache: null)
    {
    }

    public TerrainChunkStreamer(TerrainLodDiskCache? diskCache)
    {
        _lodDiskCache = diskCache ?? new TerrainLodDiskCache();
    }

    public TerrainLodSectionCache LodCache => _lodCache;

    public TerrainLodDiskCache LodDiskCache => _lodDiskCache;

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
    /// When true, Full jobs are queued for GL compute meshing instead of
    /// <see cref="PreviewTerrainMeshBaker.BakeFullChunk"/> on worker threads.
    /// LOD / vegetation still bake on CPU. Disabled by default until the backend enables it.
    /// </summary>
    public bool PreferGpuFullMeshing
    {
        get => Volatile.Read(ref _preferGpuFullMeshing);
        set => Volatile.Write(ref _preferGpuFullMeshing, value);
    }

    /// <summary>
    /// When true, LOD≥<see cref="PreviewStageConstants.TerrainGpuLodMinLevel"/> sections are queued for
    /// Stage-2 budgeted bake instead of worker-thread <see cref="PreviewTerrainLodMeshBaker.BakeLodSection"/>.
    /// </summary>
    public bool PreferGpuLodMeshing
    {
        get => Volatile.Read(ref _preferGpuLodMeshing);
        set => Volatile.Write(ref _preferGpuLodMeshing, value);
    }

    /// <summary>Queued Full keys waiting for Stage-2 GL compute meshing.</summary>
    public int GpuFullJobCount => _gpuFullJobs.Count;

    /// <summary>Queued LOD≥3 sections waiting for Stage-2 bake.</summary>
    public int GpuLodJobCount => _gpuLodJobs.Count;

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

    public int InflightCount => _inflight.Count;

    public int ReadyCount => _ready.Count;

    public long FullBakeCompletedCount => Interlocked.Read(ref _fullBakeCompletedCount);

    public long LodBakeCompletedCount => Interlocked.Read(ref _lodBakeCompletedCount);

    public long DiskWarmCompletedCount => Interlocked.Read(ref _diskWarmCompletedCount);

    public long BakeFaultCount => Interlocked.Read(ref _bakeFaultCount);

    public long LastFullBakeMilliseconds => Interlocked.Read(ref _lastFullBakeMilliseconds);

    public string LastBakeFault => Volatile.Read(ref _lastBakeFault) ?? "none";

    public readonly record struct LodBand(byte Level, int DMin, int DMax);

    public static int ResolveWorkerCount() =>
        Math.Clamp(
            Math.Max(1, Environment.ProcessorCount / 2),
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
    /// Next soft-start unlock radius after <paramref name="currentMaxRing"/> saturates.
    /// Jumps to the next LOD band end (not +1) so a 128-chunk ring unlocks in ~level-count steps.
    /// </summary>
    public static int ResolveNextScheduleMaxRing(int hardRadius, int lodRingChunks, int currentMaxRing)
    {
        hardRadius = Math.Max(0, hardRadius);
        lodRingChunks = Math.Max(0, lodRingChunks);
        var lodCap = hardRadius + lodRingChunks;
        currentMaxRing = Math.Clamp(currentMaxRing, hardRadius, Math.Max(hardRadius, lodCap));
        if (currentMaxRing >= lodCap || lodRingChunks <= 0)
        {
            return lodCap;
        }

        Span<LodBand> bands = stackalloc LodBand[TerrainResidencyKey.MaxLodLevel];
        var n = ResolveLodBands(hardRadius, lodRingChunks, bands);
        for (var i = 0; i < n; i++)
        {
            if (bands[i].DMax > currentMaxRing)
            {
                return Math.Min(bands[i].DMax, lodCap);
            }
        }

        return Math.Min(currentMaxRing + 1, lodCap);
    }

    /// <summary>
    /// Keep-radius used when pinning out-of-desired LOD: transition skirt only
    /// (hard disk + fade overlap), not the entire lod ring.
    /// </summary>
    public static int ResolveLodPinKeepRadiusChunks(int hardRadiusChunks) =>
        hardRadiusChunks + Math.Max(ResolveLodFadeOverlapChunks() * 2, 4);

    /// <summary>
    /// True when <paramref name="key"/> is eligible regardless of soft-start unlock
    /// (Full disk + LOD1/LOD2 transition seam).
    /// </summary>
    public static bool IsSoftStartUngatedKey(
        TerrainResidencyKey key,
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks) =>
        key.IsFull || IsTransitionCoverageKey(key, cameraChunk, hardRadiusChunks);

    /// <summary>
    /// Keys that must stay drawable (or keep retrying upload) for seam coverage: Full disk,
    /// LOD1 underlay under the Full fade, and one adjacent LOD2 ring at that seam.
    /// Coarse distant sections are not protected — they fill remaining VRAM after reserved coverage.
    /// </summary>
    public static bool IsTransitionCoverageKey(
        TerrainResidencyKey key,
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks)
    {
        hardRadiusChunks = Math.Max(0, hardRadiusChunks);
        var dist = key.ChebyshevDistanceToChunk(cameraChunk);
        if (key.IsFull)
        {
            return dist <= hardRadiusChunks;
        }

        var overlap = ResolveLodFadeOverlapChunks();
        // Fixed protect disk — do not scale by section ChunksPerSide (that falsely protected Lod7).
        var protect = hardRadiusChunks + Math.Max(overlap * 2, 4);
        if (key.LodLevel == 1)
        {
            return dist <= protect;
        }

        if (key.LodLevel == 2)
        {
            return dist <= protect + TerrainResidencyKey.ChunksPerSideForLevel(2);
        }

        return false;
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
    /// True when every Full-disk chunk covered by <paramref name="lodSection"/> reports as
    /// GPU-resident via <paramref name="isGpuResident"/>. Used to pin obsolete LOD until
    /// approach Full replacement is drawable.
    /// </summary>
    public static bool HasFullDiskGpuCoverageForLodSection(
        TerrainResidencyKey lodSection,
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks,
        Func<TerrainResidencyKey, bool> isGpuResident)
    {
        ArgumentNullException.ThrowIfNull(isGpuResident);
        if (!lodSection.IsLod)
        {
            return true;
        }

        hardRadiusChunks = Math.Max(0, hardRadiusChunks);
        var x0 = lodSection.OriginChunkX;
        var z0 = lodSection.OriginChunkZ;
        var side = lodSection.ChunksPerSide;
        for (var z = z0; z < z0 + side; z++)
        {
            for (var x = x0; x < x0 + side; x++)
            {
                var chebyshev = Math.Max(Math.Abs(x - cameraChunk.X), Math.Abs(z - cameraChunk.Z));
                if (chebyshev > hardRadiusChunks)
                {
                    continue;
                }

                if (!isGpuResident(TerrainResidencyKey.Full(x, z)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// True when every chunk cell of <paramref name="leaving"/> that still lies inside
    /// <paramref name="keepRadiusChunks"/> is covered by some other GPU resident (Full or LOD).
    /// Pins band-shifted LOD until the replacement mesh is drawable — Full-disk-only checks
    /// miss the LOD skirt and punch holes while flying.
    /// </summary>
    public static bool HasFootprintReplacementCoverage(
        TerrainResidencyKey leaving,
        TerrainChunkKey cameraChunk,
        int keepRadiusChunks,
        IEnumerable<TerrainResidencyKey> gpuResidents)
    {
        ArgumentNullException.ThrowIfNull(gpuResidents);
        if (!leaving.IsLod)
        {
            return true;
        }

        keepRadiusChunks = Math.Max(0, keepRadiusChunks);
        var lx0 = leaving.OriginChunkX;
        var lz0 = leaving.OriginChunkZ;
        var lSide = leaving.ChunksPerSide;
        var lx1 = lx0 + lSide;
        var lz1 = lz0 + lSide;

        // Cheap reject: if no cell is inside keep radius, nothing to protect.
        var anyKept = false;
        for (var z = lz0; z < lz1 && !anyKept; z++)
        {
            for (var x = lx0; x < lx1; x++)
            {
                var chebyshev = Math.Max(Math.Abs(x - cameraChunk.X), Math.Abs(z - cameraChunk.Z));
                if (chebyshev <= keepRadiusChunks)
                {
                    anyKept = true;
                    break;
                }
            }
        }

        if (!anyKept)
        {
            return true;
        }

        var covered = new HashSet<(int X, int Z)>();
        foreach (var key in gpuResidents)
        {
            if (key == leaving)
            {
                continue;
            }

            var ox = key.OriginChunkX;
            var oz = key.OriginChunkZ;
            var side = key.ChunksPerSide;
            var ix0 = Math.Max(lx0, ox);
            var iz0 = Math.Max(lz0, oz);
            var ix1 = Math.Min(lx1, ox + side);
            var iz1 = Math.Min(lz1, oz + side);
            if (ix0 >= ix1 || iz0 >= iz1)
            {
                continue;
            }

            for (var z = iz0; z < iz1; z++)
            {
                for (var x = ix0; x < ix1; x++)
                {
                    covered.Add((x, z));
                }
            }
        }

        for (var z = lz0; z < lz1; z++)
        {
            for (var x = lx0; x < lx1; x++)
            {
                var chebyshev = Math.Max(Math.Abs(x - cameraChunk.X), Math.Abs(z - cameraChunk.Z));
                if (chebyshev > keepRadiusChunks)
                {
                    continue;
                }

                if (!covered.Contains((x, z)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Soft-unload distance past which a non-desired key may leave GPU residency.
    /// Scales with section size so multi-chunk LOD is not dropped while the camera is still
    /// inside / near its footprint.
    /// </summary>
    public static int ResolveSoftUnloadHysteresisChunks(TerrainResidencyKey key) =>
        Math.Max(
            PreviewStageConstants.TerrainSoftUnloadHysteresisChunks,
            key.IsLod ? key.ChunksPerSide : 0);

    /// <summary>
    /// World-meter fade-out window for a finer detail level at its outer edge. Coarser underlay
    /// stays opaque; discard dither runs on this level only. Distances are Chebyshev-compatible
    /// (band end × chunk size) so the morph matches the square residency disk.
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
            var workerIndex = i;
            workers[i] = Task.Factory.StartNew(
                () => WorkerLoop(workerIndex == 0, _cts.Token),
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

        while (_gpuFullJobs.TryDequeue(out _))
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
        // Keep on-disk LOD cache across streamer lifetimes (session / app restart reuse).
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
        Dictionary<TerrainResidencyKey, TerrainChunkLodKind> priorDesired;
        lock (_desiredLock)
        {
            priorDesired = _desired;
        }

        // Keep prior LOD sections for one soft-hysteresis margin so band/grid membership does
        // not thrash every camera-chunk step while replacements catch up.
        ApplyDesiredMembershipHysteresis(next, priorDesired, cam, HardRadiusChunks, LodRingChunks);

        var nextPrefetch = BuildDiskPrefetchResidency(cam, HardRadiusChunks, LodRingChunks);
        lock (_desiredLock)
        {
            _desired = next;
            _desiredSnapshot = next;
            _diskPrefetch = nextPrefetch;
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
            // Adjacent underlay only: LOD1 into Full; LOD≥2 into the previous band — not deeper.
            var underlayFloor = i == 0 ? 0 : bands[i - 1].DMin;
            AddLodSections(next, cam, hardRadius, band.DMin, band.DMax, band.Level, underlayFloor);
        }

        return next;
    }

    /// <summary>
    /// Retains prior LOD section keys that still intersect an expanded band margin so desired
    /// membership does not flicker every camera-chunk step.
    /// </summary>
    public static void ApplyDesiredMembershipHysteresis(
        Dictionary<TerrainResidencyKey, TerrainChunkLodKind> next,
        IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> prior,
        TerrainChunkKey cam,
        int hardRadius,
        int lodRingChunks)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(prior);
        if (prior.Count == 0 || lodRingChunks <= 0)
        {
            return;
        }

        var lodRadius = hardRadius + lodRingChunks;
        foreach (var (key, kind) in prior)
        {
            if (!key.IsLod || next.ContainsKey(key))
            {
                continue;
            }

            var hysteresis = ResolveSoftUnloadHysteresisChunks(key);
            var dist = key.ChebyshevDistanceToChunk(cam);
            if (dist > lodRadius + hysteresis)
            {
                continue;
            }

            // Drop only when the entire section is inside the Full core (no longer useful cover).
            var overlap = ResolveLodFadeOverlapChunks();
            var fullCoreExclusive = Math.Max(-1, hardRadius - overlap);
            if (fullCoreExclusive >= 0 &&
                key.MaxChebyshevDistanceToChunk(cam) <= fullCoreExclusive)
            {
                continue;
            }

            next[key] = kind;
        }
    }

    /// <summary>
    /// Every active LOD level over the lod-ring footprint (Full core excluded). Used to warm
    /// mem/disk caches so approach/recede can swap into a level that is not currently GPU-desired.
    /// Does not drive GPU residency — only the banded <see cref="BuildDesiredResidency"/> set does.
    /// </summary>
    public static Dictionary<TerrainResidencyKey, TerrainChunkLodKind> BuildDiskPrefetchResidency(
        TerrainChunkKey cam,
        int hardRadius,
        int lodRingChunks)
    {
        hardRadius = Math.Max(0, hardRadius);
        lodRingChunks = Math.Max(0, lodRingChunks);
        var next = new Dictionary<TerrainResidencyKey, TerrainChunkLodKind>(256);
        if (lodRingChunks <= 0)
        {
            return next;
        }

        var levelCount = ResolveActiveLodLevelCount(lodRingChunks);
        var lodRadius = hardRadius + lodRingChunks;
        // underlayFloor=0 lets each level pull into the Full fade ring the same way LOD1 does,
        // so every level covering a world cell near the seam is on disk.
        for (byte level = 1; level <= levelCount; level++)
        {
            AddLodSections(
                next,
                cam,
                hardRadius,
                dMin: hardRadius + 1,
                dMax: lodRadius,
                lodLevel: level,
                underlayFloor: 0);
        }

        return next;
    }

    private static void AddLodSections(
        Dictionary<TerrainResidencyKey, TerrainChunkLodKind> desired,
        TerrainChunkKey cam,
        int hardRadius,
        int dMin,
        int dMax,
        byte lodLevel,
        int underlayFloor)
    {
        if (dMax < dMin)
        {
            return;
        }

        var scale = TerrainResidencyKey.ChunksPerSideForLevel(lodLevel);
        var kind = (TerrainChunkLodKind)lodLevel;
        var overlap = ResolveLodFadeOverlapChunks();
        // Pull coarser sections under the finer band so fade-out dither has solid underlay.
        var underlayDMin = Math.Max(underlayFloor, dMin - overlap);
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

    /// <summary>
    /// True when the streamer treats the key as resident (real GPU upload or budget fake-park).
    /// </summary>
    public bool IsMarkedResident(TerrainResidencyKey key) => _resident.ContainsKey(key);

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

    /// <summary>Drop residency, queued bakes, GPU Full jobs, and the CPU LOD cache so the next tick rebakes.</summary>
    public void InvalidateAll()
    {
        while (_ready.TryDequeue(out _))
        {
        }

        while (_gpuFullJobs.TryDequeue(out _))
        {
        }

        while (_gpuLodJobs.TryDequeue(out _))
        {
        }

        _inflight.Clear();
        _resident.Clear();
        _lodCache.Clear();
        _lodDiskCache.Clear();
        lock (_desiredLock)
        {
            _desired = new Dictionary<TerrainResidencyKey, TerrainChunkLodKind>();
            _desiredSnapshot = EmptyDesired;
            _diskPrefetch = new Dictionary<TerrainResidencyKey, TerrainChunkLodKind>();
        }
        _lastDesiredCameraChunk = default;
        _lastDesiredViewDistance = int.MinValue;
        _lastDesiredLodRingChunks = int.MinValue;
        lock (_pickLock)
        {
            _scheduleMaxRing = Math.Max(
                HardRadiusChunks,
                PreviewStageConstants.TerrainStreamSoftStartInitialRing);
        }
    }

    /// <summary>Clear the CPU + on-disk LOD section caches (residency rebuild still required for GPU).</summary>
    public void ClearLodCache()
    {
        _lodCache.Clear();
        _lodDiskCache.Clear();
    }

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

    /// <summary>
    /// Drain ready meshes into separate Full / LOD quotas so Full catch-up cannot starve LOD uploads.
    /// Overflow stays on the ready queue (inflight restored).
    /// </summary>
    public void DrainReadySplit(
        List<PreviewTerrainChunkMesh> fullDestination,
        List<PreviewTerrainChunkMesh> lodDestination,
        int maxFull,
        int maxLod,
        long maxFullBytes,
        long maxLodBytes)
    {
        ArgumentNullException.ThrowIfNull(fullDestination);
        ArgumentNullException.ThrowIfNull(lodDestination);
        maxFull = Math.Max(0, maxFull);
        maxLod = Math.Max(0, maxLod);
        maxFullBytes = Math.Max(1, maxFullBytes);
        maxLodBytes = Math.Max(1, maxLodBytes);

        var totalCap = maxFull + maxLod;
        if (totalCap <= 0)
        {
            return;
        }

        var buffer = new List<PreviewTerrainChunkMesh>(totalCap);
        DrainReady(buffer, totalCap, maxFullBytes + maxLodBytes);

        long fullBytes = 0;
        long lodBytes = 0;
        List<PreviewTerrainChunkMesh>? overflow = null;
        foreach (var mesh in buffer)
        {
            if (mesh.Key.IsFull)
            {
                if (fullDestination.Count < maxFull &&
                    (fullDestination.Count == 0 || fullBytes + mesh.UploadByteLength <= maxFullBytes))
                {
                    fullDestination.Add(mesh);
                    fullBytes += mesh.UploadByteLength;
                    continue;
                }
            }
            else if (lodDestination.Count < maxLod &&
                     (lodDestination.Count == 0 || lodBytes + mesh.UploadByteLength <= maxLodBytes))
            {
                lodDestination.Add(mesh);
                lodBytes += mesh.UploadByteLength;
                continue;
            }

            overflow ??= [];
            overflow.Add(mesh);
        }

        if (overflow is null)
        {
            return;
        }

        foreach (var mesh in overflow)
        {
            ReturnReady(mesh);
        }
    }

    /// <summary>Re-queue a drained mesh that lost the per-frame Full/LOD upload split.</summary>
    public void ReturnReady(PreviewTerrainChunkMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        _inflight.TryAdd(mesh.Key, mesh.Lod);
        _ready.Enqueue(mesh);
    }

    /// <summary>Pull the next Full chunk queued for Stage-2 GL compute meshing.</summary>
    public bool TryDequeueGpuFullJob(out TerrainGpuFullJob job) => _gpuFullJobs.TryDequeue(out job);

    /// <summary>Pull the next LOD≥3 section queued for Stage-2 bake.</summary>
    public bool TryDequeueGpuLodJob(out TerrainGpuLodJob job) => _gpuLodJobs.TryDequeue(out job);

    /// <summary>Publish a GPU-produced Full mesh onto the ready upload queue.</summary>
    public void CompleteGpuFullMesh(PreviewTerrainChunkMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        _ready.Enqueue(mesh);
    }

    /// <summary>Publish a Stage-2 LOD mesh onto the ready upload queue and warm caches.</summary>
    public void CompleteGpuLodMesh(PreviewTerrainChunkMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Key.IsLod)
        {
            var fingerprint = TerrainLodCacheFingerprint.From(
                WorldGenSettings,
                GrassBakeSettings,
                VegetationBakePlan);
            var cacheKey = new TerrainLodCacheKey(mesh.Key, fingerprint);
            _lodCache.Store(cacheKey, mesh);
            _lodDiskCache.TryStore(cacheKey, mesh);
        }

        _ready.Enqueue(mesh);
    }

    /// <summary>
    /// Drop inflight so the schedule can pick the key again (e.g. after GPU demote to CPU bake).
    /// </summary>
    public void AbandonGpuFullJob(TerrainChunkKey key) =>
        _inflight.TryRemove(TerrainResidencyKey.Full(key), out _);

    /// <summary>Drop LOD Stage-2 inflight so the schedule can re-pick the section.</summary>
    public void AbandonGpuLodJob(TerrainResidencyKey key) =>
        _inflight.TryRemove(key, out _);

    /// <summary>
    /// After PreferGpuLodMeshing turns off, abandon every queued Stage-2 LOD job so workers
    /// can rebake those keys on the CPU path instead of leaving them latched in-flight.
    /// </summary>
    public void DrainAbandonedGpuLodJobs()
    {
        while (_gpuLodJobs.TryDequeue(out var job))
        {
            _inflight.TryRemove(job.Key, out _);
        }
    }

    /// <summary>
    /// After PreferGpuFullMeshing turns off, abandon every queued Stage-2 Full job so workers
    /// can rebake those chunks on the CPU path.
    /// </summary>
    public void DrainAbandonedGpuFullJobs()
    {
        while (_gpuFullJobs.TryDequeue(out var job))
        {
            _inflight.TryRemove(TerrainResidencyKey.Full(job.Key), out _);
        }
    }

    /// <summary>Test hook: push a Stage-2 LOD job without a worker claim.</summary>
    internal void EnqueueGpuLodJobForTests(in TerrainGpuLodJob job) => _gpuLodJobs.Enqueue(job);

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

    private void WorkerLoop(bool nearFullLatencyLane, CancellationToken ct)
    {
        // Mesh generation is sustained background work, not latency-critical UI work. Running
        // ProcessorCount-1 normal-priority greedy/vegetation bakes starved WGL presentation and
        // made the preview appear locked even though the GL thread itself was not blocked. Keep
        // one normal-priority latency lane only until the camera-local 5x5 Full pad is resident;
        // otherwise the post-Core cloud/TAA load can starve all BelowNormal terrain workers.
        ThreadPriority? workerPriority = null;

        while (!ct.IsCancellationRequested)
        {
            var startupPadResident = HasCameraNearFullPadResident();
            var desiredPriority = nearFullLatencyLane && !startupPadResident
                ? ThreadPriority.Normal
                : ThreadPriority.BelowNormal;
            if (desiredPriority != workerPriority)
            {
                try
                {
                    Thread.CurrentThread.Priority = desiredPriority;
                    workerPriority = desiredPriority;
                }
                catch (ThreadStateException)
                {
                    // Priority is a best-effort scheduling hint.
                }
            }

            // One latency lane builds the camera-local 5x5 first. Additional workers stay parked
            // until the viewport is paintable, and all lanes stop when GPU upload falls behind.
            if ((!nearFullLatencyLane && !startupPadResident) ||
                _inflight.Count >= PreviewStageConstants.TerrainMaxBakeJobsAhead)
            {
                if (ct.WaitHandle.WaitOne(8))
                {
                    break;
                }

                continue;
            }

            TerrainResidencyKey jobKey;
            TerrainChunkLodKind needLod;
            var diskWarmOnly = false;
            var haveJob = false;
            // Alternate lanes: half workers prefer unlocked LOD; every 4th pick may disk-warm
            // ahead even while GPU desired still has work.
            var lane = Interlocked.Increment(ref _pickLaneCounter);
            var preferLodLane = (lane & 1) == 1 && startupPadResident;
            var preferDiskWarm = (lane & 3) == 3;
            lock (_pickLock)
            {
                if (TryPickJob(
                        out jobKey,
                        out needLod,
                        out diskWarmOnly,
                        allowDiskPrefetch: true,
                        preferLodLane,
                        preferDiskWarm) &&
                    _inflight.TryAdd(jobKey, needLod))
                {
                    haveJob = true;
                }
                else
                {
                    jobKey = default;
                    needLod = default;
                    diskWarmOnly = false;
                }
            }

            if (!haveJob)
            {
                if (ct.WaitHandle.WaitOne(8))
                {
                    break;
                }

                continue;
            }

            try
            {
                if (!diskWarmOnly)
                {
                    _resident.TryRemove(jobKey, out _);
                }

                var grassSettings = GrassBakeSettings;
                var worldGen = WorldGenSettings;
                var vegetation = VegetationBakePlan;
                PreviewTerrainChunkMesh? mesh;

                if (diskWarmOnly)
                {
                    // Disk warm never goes through Stage-2 GPU — always CPU bake → mem + disk.
                    var fingerprint = TerrainLodCacheFingerprint.From(worldGen, grassSettings, vegetation);
                    var cacheKey = new TerrainLodCacheKey(jobKey, fingerprint);
                    if (_lodCache.Contains(cacheKey) || _lodDiskCache.Contains(cacheKey))
                    {
                        _inflight.TryRemove(jobKey, out _);
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
                        _lodDiskCache.TryStore(cacheKey, mesh);
                        Interlocked.Increment(ref _diskWarmCompletedCount);
                        // If this section became GPU-desired while baking, promote to upload.
                        if (SnapshotDesired().ContainsKey(jobKey))
                        {
                            _ready.Enqueue(mesh);
                            continue;
                        }
                    }

                    _inflight.TryRemove(jobKey, out _);
                    continue;
                }

                if (needLod == TerrainChunkLodKind.Full)
                {
                    if (PreferGpuFullMeshing)
                    {
                        _gpuFullJobs.Enqueue(new TerrainGpuFullJob(
                            new TerrainChunkKey(jobKey.X, jobKey.Z),
                            grassSettings,
                            worldGen,
                            vegetation));
                        continue;
                    }

                    var fullBakeStarted = Stopwatch.GetTimestamp();
                    mesh = PreviewTerrainMeshBaker.BakeFullChunk(
                        new TerrainChunkKey(jobKey.X, jobKey.Z),
                        grassSettings,
                        worldGen,
                        vegetation);
                    Interlocked.Exchange(
                        ref _lastFullBakeMilliseconds,
                        (long)Stopwatch.GetElapsedTime(fullBakeStarted).TotalMilliseconds);
                    if (mesh is not null)
                    {
                        Interlocked.Increment(ref _fullBakeCompletedCount);
                    }
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

                    if (_lodDiskCache.TryLoad(cacheKey, out mesh))
                    {
                        _lodCache.Store(cacheKey, mesh);
                        _ready.Enqueue(mesh);
                        continue;
                    }

                    if (PreferGpuLodMeshing &&
                        jobKey.LodLevel >= PreviewStageConstants.TerrainGpuLodMinLevel)
                    {
                        _gpuLodJobs.Enqueue(new TerrainGpuLodJob(
                            jobKey,
                            grassSettings,
                            worldGen,
                            vegetation));
                        continue;
                    }

                    mesh = PreviewTerrainLodMeshBaker.BakeLodSection(
                        jobKey,
                        worldGen,
                        grassSettings,
                        vegetation);
                    if (mesh is not null)
                    {
                        Interlocked.Increment(ref _lodBakeCompletedCount);
                        _lodCache.Store(cacheKey, mesh);
                        _lodDiskCache.TryStore(cacheKey, mesh);
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
            catch (Exception ex)
            {
                Interlocked.Increment(ref _bakeFaultCount);
                Volatile.Write(ref _lastBakeFault, ex.GetType().Name + ": " + ex.Message);
                _inflight.TryRemove(jobKey, out _);
            }
        }
    }

    private bool HasCameraNearFullPadResident()
    {
        var cam = _cameraChunk;
        for (var dz = -2; dz <= 2; dz++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var key = TerrainResidencyKey.Full(cam.X + dx, cam.Z + dz);
                if (!_resident.TryGetValue(key, out var have) ||
                    have != TerrainChunkLodKind.Full)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool TryPickJob(
        out TerrainResidencyKey key,
        out TerrainChunkLodKind lod,
        out bool diskWarmOnly,
        bool allowDiskPrefetch,
        bool preferLodLane,
        bool preferDiskWarm)
    {
        key = default;
        lod = TerrainChunkLodKind.Full;
        diskWarmOnly = false;
        Dictionary<TerrainResidencyKey, TerrainChunkLodKind> desired;
        Dictionary<TerrainResidencyKey, TerrainChunkLodKind> diskPrefetch;
        lock (_desiredLock)
        {
            desired = _desired;
            diskPrefetch = _diskPrefetch;
        }

        // Workers start before the first camera Tick during GL bootstrap. An empty desired set
        // means "not initialized", not "the current ring is saturated"; expanding here raced
        // straight from the hard radius to LodRadius (8 + 512 = 520) before the first frame.
        if (desired.Count == 0)
        {
            return false;
        }

        var cam = _cameraChunk;
        var hardRadius = HardRadiusChunks;
        var maxRing = Math.Max(_scheduleMaxRing, hardRadius);
        if (maxRing != _scheduleMaxRing)
        {
            _scheduleMaxRing = maxRing;
        }

        var lodCap = LodRadiusChunks;
        var warmRing = allowDiskPrefetch
            ? Math.Min(
                lodCap,
                Math.Max(
                    maxRing,
                    ResolveNextScheduleMaxRing(hardRadius, LodRingChunks, maxRing)))
            : maxRing;

        TerrainResidencyKey? bestFull = null;
        TerrainResidencyKey? bestNearLod = null;
        TerrainResidencyKey? bestFar = null;
        TerrainStreamSchedule.Rank bestFullRank = default;
        TerrainStreamSchedule.Rank bestNearLodRank = default;
        TerrainStreamSchedule.Rank bestFarRank = default;
        var haveFull = false;
        var haveNearLod = false;
        var haveFar = false;
        // Soft-start must count Full as pending. Ignoring Full made scheduleMax race to the
        // full lod radius (e.g. 520) while the hard disk was still empty.
        var pendingInUnlockedWindow = false;

        foreach (var (k, want) in desired)
        {
            var ring = TerrainStreamSchedule.RingIndex(k, cam);
            var ungated = IsSoftStartUngatedKey(k, cam, hardRadius);
            // Full + transition LOD always eligible; coarser LOD waits on soft-start unlock.
            if (!ungated && ring > maxRing)
            {
                continue;
            }

            if (_inflight.ContainsKey(k))
            {
                if (ungated || ring <= maxRing)
                {
                    pendingInUnlockedWindow = true;
                }

                continue;
            }

            if (_resident.TryGetValue(k, out var have) && have == want)
            {
                continue;
            }

            if (ungated || ring <= maxRing)
            {
                pendingInUnlockedWindow = true;
            }

            var rank = TerrainStreamSchedule.RankKey(k, cam);
            if (k.IsFull)
            {
                if (!haveFull || TerrainStreamSchedule.Compare(rank, bestFullRank) < 0)
                {
                    haveFull = true;
                    bestFullRank = rank;
                    bestFull = k;
                }
            }
            else if (ungated)
            {
                if (!haveNearLod || TerrainStreamSchedule.Compare(rank, bestNearLodRank) < 0)
                {
                    haveNearLod = true;
                    bestNearLodRank = rank;
                    bestNearLod = k;
                }
            }
            else
            {
                if (!haveFar || TerrainStreamSchedule.Compare(rank, bestFarRank) < 0)
                {
                    haveFar = true;
                    bestFarRank = rank;
                    bestFar = k;
                }
            }
        }

        // Stage-2 LOD queue still blocks unlock (2/frame pump). Full Stage-2 must NOT freeze
        // distant LOD expansion — Full keeps pumping independently.
        if (!pendingInUnlockedWindow && !_gpuLodJobs.IsEmpty)
        {
            pendingInUnlockedWindow = true;
        }

        // Dedicated LOD lanes keep transition/coarse coverage progressing while Full lanes bake.
        // Soft-start expansion still waits on pending Full above so the unlock ring cannot race.
        if (preferLodLane && haveNearLod && bestNearLod is not null)
        {
            key = bestNearLod.Value;
            lod = desired[key];
            return true;
        }

        if (preferLodLane && haveFar && bestFar is not null)
        {
            key = bestFar.Value;
            lod = desired[key];
            return true;
        }

        if (haveFull && bestFull is not null)
        {
            key = bestFull.Value;
            lod = desired[key];
            return true;
        }

        if (haveNearLod && bestNearLod is not null)
        {
            key = bestNearLod.Value;
            lod = desired[key];
            return true;
        }

        if (haveFar && bestFar is not null)
        {
            key = bestFar.Value;
            lod = desired[key];
            return true;
        }

        // Soft-start: unlock the next LOD *band* when the unlocked window is saturated.
        if (!pendingInUnlockedWindow && maxRing < lodCap && !_holdScheduleExpansion)
        {
            _scheduleMaxRing = ResolveNextScheduleMaxRing(hardRadius, LodRingChunks, maxRing);
            return false;
        }

        // Disk-warm only after the unlocked GPU desired window is satisfied. preferDiskWarm must
        // not bypass this — warming while Full/near LOD were still pending stole workers and let
        // scheduleMax race (LodRing=512 → 520 unlocked while gpuResident≈0).
        _ = preferDiskWarm;
        if (allowDiskPrefetch &&
            !pendingInUnlockedWindow &&
            TryPickDiskPrefetchJob(desired, diskPrefetch, cam, warmRing, out key, out lod))
        {
            diskWarmOnly = true;
            return true;
        }

        return false;
    }

    private bool TryPickDiskPrefetchJob(
        Dictionary<TerrainResidencyKey, TerrainChunkLodKind> desired,
        Dictionary<TerrainResidencyKey, TerrainChunkLodKind> diskPrefetch,
        TerrainChunkKey cam,
        int maxRing,
        out TerrainResidencyKey key,
        out TerrainChunkLodKind lod)
    {
        key = default;
        lod = TerrainChunkLodKind.Full;
        if (diskPrefetch.Count == 0)
        {
            return false;
        }

        var fingerprint = TerrainLodCacheFingerprint.From(
            WorldGenSettings,
            GrassBakeSettings,
            VegetationBakePlan);
        TerrainResidencyKey? best = null;
        TerrainStreamSchedule.Rank bestRank = default;
        var haveBest = false;

        foreach (var (k, want) in diskPrefetch)
        {
            if (!k.IsLod)
            {
                continue;
            }

            // GPU desired path already bakes + stores these; focus warm on non-banded levels.
            if (desired.ContainsKey(k))
            {
                continue;
            }

            // Stay within the unlocked soft-start radius so warm work tracks camera progress.
            if (TerrainStreamSchedule.RingIndex(k, cam) > maxRing)
            {
                continue;
            }

            if (_inflight.ContainsKey(k))
            {
                continue;
            }

            var cacheKey = new TerrainLodCacheKey(k, fingerprint);
            if (_lodCache.Contains(cacheKey) || _lodDiskCache.Contains(cacheKey))
            {
                continue;
            }

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
    /// Disk prefetch is off by default so soft-start tests stay deterministic.
    /// </summary>
    internal bool TryPickJobForTests(
        out TerrainResidencyKey key,
        out TerrainChunkLodKind lod,
        bool allowDiskPrefetch = false,
        bool preferLodLane = false,
        bool preferDiskWarm = false)
    {
        lock (_pickLock)
        {
            return TryPickJob(
                out key,
                out lod,
                out _,
                allowDiskPrefetch,
                preferLodLane,
                preferDiskWarm);
        }
    }

    /// <summary>Test hook: snapshot of disk-prefetch residency built on the last Tick.</summary>
    internal IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> SnapshotDiskPrefetchForTests()
    {
        lock (_desiredLock)
        {
            return new Dictionary<TerrainResidencyKey, TerrainChunkLodKind>(_diskPrefetch);
        }
    }
}

/// <summary>Full chunk handed from streamer workers to Stage-2 GL mesh pump.</summary>
public readonly record struct TerrainGpuFullJob(
    TerrainChunkKey Key,
    PreviewTerrainGrassBakeSettings GrassSettings,
    PreviewTerrainWorldGenSettings WorldGen,
    PreviewTerrainVegetationBakePlan? Vegetation = null);

/// <summary>LOD≥3 section handed from streamer workers to Stage-2 budgeted bake pump.</summary>
public readonly record struct TerrainGpuLodJob(
    TerrainResidencyKey Key,
    PreviewTerrainGrassBakeSettings GrassSettings,
    PreviewTerrainWorldGenSettings WorldGen,
    PreviewTerrainVegetationBakePlan? Vegetation = null);
