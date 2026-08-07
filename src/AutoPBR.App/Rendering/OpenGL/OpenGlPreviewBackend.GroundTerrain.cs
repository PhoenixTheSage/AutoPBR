using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

using AutoPBR.App.Lang;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private sealed class TerrainGpuChunk
    {
        public required TerrainChunkLodKind Lod { get; set; }
        public GlTerrainMeshPool.Allocation Allocation { get; set; }
        public PreviewDrawBatch[] DrawBatches { get; set; } = [];
        public Vector3 BoundsCenter { get; set; }
        public float BoundsRadius { get; set; }
        public int MinRelativeHeight { get; set; }
        public int MaxRelativeHeight { get; set; }
        public double ActivatedAtSeconds { get; set; }
        public int IndexCount => Allocation.IndexCount;
    }

    private readonly struct TerrainDrawItem
    {
        public required int FirstIndex { get; init; }
        public required int IndexCount { get; init; }
        public required int BaseVertex { get; init; }
        public required int MaterialIndex { get; init; }
        public required bool Cutout { get; init; }
        public required bool NearPom { get; init; }
        public required bool HasCoarserUnderlay { get; init; }
        public required float TransitionKeep { get; init; }
        public required TerrainChunkLodKind Lod { get; init; }
        public required int SourceOrder { get; init; }
    }

    private GlTerrainMeshPool? _terrainMeshPool;
    private GlTerrainUploadStagingRing? _terrainUploadStaging;
    private GlTerrainMeshArena? _terrainMeshArena;
    private GlTerrainTransferQueue? _terrainTransferQueue;
    private readonly Dictionary<TerrainResidencyKey, GlTerrainMeshArena.Allocation> _terrainArenaAllocations = new();
    private TerrainCoverageGraph? _terrainCoverageGraph;
    private long _terrainTransferFrameToken;
    private GlTerrainGpuFullMeshBaker? _terrainGpuFullMeshBaker;
    private GlTerrainGpuLodMeshBaker? _terrainGpuLodMeshBaker;
    private GlShaderProgram? _terrainColumnBoardComputeProgram;
    private GlShaderProgram? _terrainFullMeshEmitComputeProgram;
    private GlShaderProgram? _terrainLodBoardComputeProgram;
    private bool _loggedTerrainGpuMeshCompute;
    private GlIndirectDrawCommandBuffer? _terrainIndirectCommands;
    private readonly List<TerrainDrawItem> _terrainDrawItems = new(1024);
    private uint[] _terrainIndirectScratch = [];
    private GlTexture2DArray? _groundAlbedoArray;
    private GlTexture2DArray? _groundNormalArray;
    private GlTexture2DArray? _groundSpecArray;
    private GlTexture2DArray? _groundHeightArray;
    private GenesisMaterialTextureArrayPlan? _groundTextureArrayPlan;
    private bool _groundTextureArraysReady;
    private PreviewMaterial[]? _groundArraySlotMaterialsFingerprint;
    private GlTerrainMeshPool.MultiDrawElementsIndirectCountProc? _terrainMultiDrawIndirectCount;
    private bool _loggedTerrainMeshPoolReady;
    private bool _loggedTerrainUploadStaging;
    private bool _loggedTerrainMultiDraw;
    private bool _loggedTerrainPoolLimit;
    private bool _terrainPoolPressureLatched;
    private int _terrainArenaFailedVertexBytes;
    private int _terrainArenaFailedIndexBytes;
    private long _terrainRecoveryEvictionCount;
    private readonly HashSet<TerrainResidencyKey> _terrainDeferredChunks = [];
    private readonly Dictionary<TerrainResidencyKey, double> _terrainRetireAfterSeconds = [];
    private TerrainChunkKey? _terrainDeferredCameraChunk;
    private const double TerrainTransitionSeconds = 0.30;
    private TerrainStreamingProfile _terrainStreamingProfile;
    private TerrainAdaptiveBudgetController? _terrainAdaptiveBudgetController;

    private void ApplyTerrainGrassBakeSettings(PreviewTerrainGrassBakeSettings settings)
    {
        EnsureTerrainStreamer();
        if (_terrainStreamer!.GrassBakeSettings.Equals(settings))
        {
            return;
        }

        _terrainStreamer!.GrassBakeSettings = settings;
        DisposeTerrainGpuChunks();
        _terrainStreamer.InvalidateAll();
        lock (_sync)
        {
            _terrainStreamingNeedsFrames = true;
        }
    }

    private void ApplyTerrainVegetationBakePlan(PreviewTerrainVegetationBakePlan? plan)
    {
        EnsureTerrainStreamer();
        var next = plan is { HasAny: true } ? plan : null;
        var previousIdentity = _terrainStreamer!.VegetationBakePlan?.Identity ?? "";
        var nextIdentity = next?.Identity ?? "";
        if (string.Equals(previousIdentity, nextIdentity, StringComparison.Ordinal))
        {
            return;
        }

        _terrainStreamer.VegetationBakePlan = next;
        DisposeTerrainGpuChunks();
        _terrainStreamer.InvalidateAll();
        lock (_sync)
        {
            _terrainStreamingNeedsFrames = true;
        }
    }

    private void ApplyTerrainWorldGenSettings(PreviewTerrainWorldGenSettings settings)
    {
        EnsureTerrainStreamer();
        var previous = _terrainStreamer!.WorldGenSettings;
        var next = PreviewTerrainWorldGenSettings.Resolve(settings);
        if (previous.Equals(next))
        {
            return;
        }

        _terrainStreamer.WorldGenSettings = next;
        // Clear streamed chunks and rebake; flat pad regenerates as height-0 Plains.
        DisposeTerrainGpuChunks();
        _terrainStreamer.InvalidateAll();
        _terrainOccluderWorldGenRevision++;

        lock (_sync)
        {
            _terrainStreamingNeedsFrames = true;
        }
    }

    private readonly List<TerrainChunkDrawCull.Candidate> _terrainDrawCandidates = new(256);
    private readonly List<int> _terrainDrawSelected = new(256);
    private readonly List<TerrainGpuChunk> _terrainDrawChunkScratch = new(256);

    /// <summary>
    /// Frame-shared residency list for shadow / depth / shaded terrain cull.
    /// Invalidated when GPU chunk set changes (upload/dispose) or at frame start.
    /// NearPom is applied at select/draw time, not during collect.
    /// </summary>
    private int _terrainCandidatesChunkVersion;
    private int _terrainCandidatesBuiltVersion = -1;
    private readonly List<int> _terrainShadowSelectedNear = new(256);
    private readonly List<int> _terrainShadowSelectedMid = new(256);
    private readonly List<int> _terrainShadowSelectedFar = new(256);
    private TerrainShadowCullRecord[] _terrainShadowCullRecordScratch = [];
    private uint[] _terrainShadowSourceCommandScratch = [];
    private GlIndirectDrawCommandBuffer? _terrainShadowSourceCommands;
    private bool _terrainShadowGpuIndirectReady;
    private const int TerrainShadowParallelCullMinCandidates = 64;
    private static readonly ParallelOptions TerrainShadowParallelOptions = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(
            Environment.ProcessorCount / 2,
            1,
            3),
    };

    private void EnsureTerrainStreamer()
    {
        if (_terrainStreamer is not null)
        {
            return;
        }

        PreviewTerrainGrassBakeSettings grass;
        PreviewTerrainVegetationBakePlan? vegetation;
        PreviewTerrainWorldGenSettings worldGen;
        lock (_sync)
        {
            grass = _terrainGrassBakeSettings;
            vegetation = _terrainVegetationBakePlan is { HasAny: true }
                ? _terrainVegetationBakePlan
                : null;
            worldGen = _terrainWorldGenSettings;
        }

        _terrainStreamer = new TerrainChunkStreamer
        {
            GrassBakeSettings = grass,
            VegetationBakePlan = vegetation,
            WorldGenSettings = worldGen,
        };
        // Keep schedule held while bootstrap publishes its initial desired camera window.
        _terrainStreamer.HoldScheduleExpansion = true;
        EmitDiagnostic(
            "[3D preview] Terrain LOD disk cache: " +
            _terrainStreamer.LodDiskCache.RootDirectory + ".");
    }

    private void DisposeTerrainGpuChunks()
    {
        if (_terrainMeshPool is not null)
        {
            foreach (var chunk in _terrainGpuChunks.Values)
            {
                _terrainMeshPool.Free(chunk.Allocation);
            }
        }

        foreach (var key in _terrainGpuChunks.Keys.ToArray())
        {
            ReleaseTerrainArenaAllocation(key);
            _terrainStreamer?.NotifyUnloaded(key);
        }

        _terrainGpuChunks.Clear();
        _terrainArenaAllocations.Clear();
        _terrainRetireAfterSeconds.Clear();
        _terrainCoverageGraph = null;
        _terrainPoolPressureLatched = false;
        _terrainArenaFailedVertexBytes = 0;
        _terrainArenaFailedIndexBytes = 0;
        _terrainRecoveryEvictionCount = 0;
        foreach (var key in _terrainDeferredChunks)
        {
            _terrainStreamer?.NotifyUnloaded(key);
        }

        _terrainDeferredChunks.Clear();
        _terrainDeferredCameraChunk = null;
        _terrainCandidatesChunkVersion++;
        InvalidateTerrainShadowWorldAabbCache();
    }

    private void DisposeTerrainMeshPool()
    {
        _terrainIndirectCommands?.Dispose();
        _terrainIndirectCommands = null;
        _terrainShadowSourceCommands?.Dispose();
        _terrainShadowSourceCommands = null;
        _terrainMeshPool?.Dispose();
        _terrainMeshPool = null;
        _terrainUploadStaging?.Dispose();
        _terrainUploadStaging = null;
        _terrainTransferQueue = null;
        _terrainMeshArena = null;
        _terrainArenaAllocations.Clear();
        _terrainCoverageGraph = null;
        _terrainPoolPressureLatched = false;
        _terrainArenaFailedVertexBytes = 0;
        _terrainArenaFailedIndexBytes = 0;
        _terrainRecoveryEvictionCount = 0;
        _terrainMultiDrawIndirectCount = null;
        _terrainShadowGpuIndirectReady = false;
    }

    private void InitTerrainStreaming(GL gl)
    {
        EnsureTerrainStreamer();
        _groundMesh ??= new GlMeshBuffer(gl);
        // The selected/hardware-resolved streaming profile is applied in TickTerrainStreaming.
        // Creating the immutable arena here used the streamer's Balanced constructor default,
        // then reported High after Tick without ever rebuilding the undersized arena.
        EnsureTerrainGpuFullMeshBaker(gl);
        _terrainEnvFloorY = PreviewStageConstants.GroundPlaneWorldY +
                            PreviewStageConstants.TerrainSolidFloorRelativeY;
        _terrainEnvCeilingY = PreviewStageConstants.GroundPlaneWorldY +
                              PreviewStageConstants.TerrainMountainMaxReliefBlocks;
        // Start the DDA height atlas bake now so it can finish during later bootstrap steps.
        PrefetchTerrainOccluderAtlas(gl);
    }

    private void EnsureTerrainGpuFullMeshBaker(GL gl)
    {
        _ = gl;
        EnsureTerrainStreamer();
        // PreferGpuFullMeshing parks Full in a Stage-2 ThreadPool queue that still runs CPU
        // BakeFullChunk, but only pumps a few jobs/frame. Workers claim the whole hard disk as
        // inflight, then idle — startup sticks at gpuResident=0 / uploadedFull=0. PreferGpuLod
        // had the same soft-start starvation. Production stays on LongRunning worker bakes.
        // Do not compile board/emit/LOD board here: they blocked bootstrap step 3 and bought
        // nothing while PreferGpu stays off (live smoke compiles them separately).
        _terrainStreamer!.PreferGpuFullMeshing = false;
        _terrainStreamer.PreferGpuLodMeshing = false;
        _terrainStreamer.DrainAbandonedGpuFullJobs();
        _terrainStreamer.DrainAbandonedGpuLodJobs();
        if (!_loggedTerrainGpuMeshCompute)
        {
            _loggedTerrainGpuMeshCompute = true;
            EmitDiagnostic(
                "[3D preview] Terrain Full + LOD bake on worker CPU " +
                "(PreferGpu Stage-2 pumps off — queue claim starved startup residency).");
        }
    }

    private int _terrainGpuFullBakeInflight;
    private int _terrainGpuLodBakeInflight;

    /// <summary>
    /// Stage-2: claim a small budget of Full mesh jobs outside the Scene GPU timer.
    /// Production bakes greedy solids (+ veg) on the thread pool so the GL thread stays free;
    /// compute board/emit stays compiled for live parity / v1.1 greedy GPU port.
    /// </summary>
    private void PumpGpuFullMeshJobs()
    {
        if (_gl is null ||
            _terrainStreamer is null ||
            !_terrainStreamer.PreferGpuFullMeshing ||
            _terrainGpuFullMeshBaker is not { IsHealthy: true })
        {
            return;
        }

        var budget = GlTerrainGpuFullMeshBaker.JobsPerFrameBudget;
        // Startup / hard-disk catch-up: PreferGpu Full is ThreadPool-pumped; raise claim rate so
        // LongRunning workers are not left idle while waiting for resident Full coverage.
        if (_terrainStreamingNeedsFrames)
        {
            budget = Math.Max(budget, 8);
        }

        while (Volatile.Read(ref _terrainGpuFullBakeInflight) < budget &&
               _terrainStreamer.TryDequeueGpuFullJob(out var job))
        {
            Interlocked.Increment(ref _terrainGpuFullBakeInflight);
            var streamer = _terrainStreamer;
            var baker = _terrainGpuFullMeshBaker;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    PreviewTerrainChunkMesh? mesh;
                    try
                    {
                        mesh = baker.TryBake(job);
                    }
                    catch (Exception ex)
                    {
                        DemoteTerrainGpuFullMeshing("exception: " + ex.Message);
                        streamer.AbandonGpuFullJob(job.Key);
                        return;
                    }

                    if (mesh is null)
                    {
                        DemoteTerrainGpuFullMeshing(baker.LastError ?? "bake returned null");
                        streamer.AbandonGpuFullJob(job.Key);
                        return;
                    }

                    streamer.CompleteGpuFullMesh(mesh);
                }
                finally
                {
                    Interlocked.Decrement(ref _terrainGpuFullBakeInflight);
                }
            });
        }
    }

    /// <summary>
    /// Stage-2: claim a small budget of LOD≥3 section jobs outside the Scene GPU timer.
    /// Production uses CPU BakeLodSection on the thread pool (veg keep-mask included).
    /// </summary>
    private void PumpGpuLodMeshJobs()
    {
        if (_gl is null ||
            _terrainStreamer is null ||
            !_terrainStreamer.PreferGpuLodMeshing ||
            _terrainGpuLodMeshBaker is not { IsHealthy: true })
        {
            return;
        }

        var budget = GlTerrainGpuLodMeshBaker.JobsPerFrameBudget;
        while (Volatile.Read(ref _terrainGpuLodBakeInflight) < budget &&
               _terrainStreamer.TryDequeueGpuLodJob(out var job))
        {
            Interlocked.Increment(ref _terrainGpuLodBakeInflight);
            var streamer = _terrainStreamer;
            var baker = _terrainGpuLodMeshBaker;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    PreviewTerrainChunkMesh? mesh;
                    try
                    {
                        mesh = baker.TryBake(job);
                    }
                    catch (Exception ex)
                    {
                        DemoteTerrainGpuLodMeshing("exception: " + ex.Message);
                        streamer.AbandonGpuLodJob(job.Key);
                        return;
                    }

                    if (mesh is null)
                    {
                        // Empty section is valid — do not demote the whole Stage-2 path.
                        streamer.AbandonGpuLodJob(job.Key);
                        return;
                    }

                    streamer.CompleteGpuLodMesh(mesh);
                }
                finally
                {
                    Interlocked.Decrement(ref _terrainGpuLodBakeInflight);
                }
            });
        }
    }

    private void DemoteTerrainGpuLodMeshing(string reason)
    {
        _terrainGpuLodMeshBaker?.Demote(reason);
        if (_terrainStreamer is not null)
        {
            _terrainStreamer.PreferGpuLodMeshing = false;
            _terrainStreamer.DrainAbandonedGpuLodJobs();
        }

        EmitDiagnostic(
            "[3D preview] Terrain LOD mesh Stage-2 demoted to worker BakeLodSection: " + reason + ".");
    }

    private void DemoteTerrainGpuFullMeshing(string reason)
    {
        _terrainGpuFullMeshBaker?.Demote(reason);
        _terrainGpuLodMeshBaker?.Demote(reason);
        if (_terrainStreamer is not null)
        {
            _terrainStreamer.PreferGpuFullMeshing = false;
            _terrainStreamer.PreferGpuLodMeshing = false;
            _terrainStreamer.DrainAbandonedGpuLodJobs();
            _terrainStreamer.DrainAbandonedGpuFullJobs();
        }

        EmitDiagnostic(
            "[3D preview] Terrain Full mesh compute demoted to CPU BakeFullChunk: " + reason + ".");
    }

    private void DisposeTerrainGpuFullMeshBaker()
    {
        _terrainGpuFullMeshBaker?.Dispose();
        _terrainGpuFullMeshBaker = null;
        _terrainGpuLodMeshBaker?.Dispose();
        _terrainGpuLodMeshBaker = null;
        _terrainColumnBoardComputeProgram?.Dispose();
        _terrainColumnBoardComputeProgram = null;
        _terrainFullMeshEmitComputeProgram?.Dispose();
        _terrainFullMeshEmitComputeProgram = null;
        _terrainLodBoardComputeProgram?.Dispose();
        _terrainLodBoardComputeProgram = null;
        _loggedTerrainGpuMeshCompute = false;
        if (_terrainStreamer is not null)
        {
            _terrainStreamer.PreferGpuFullMeshing = false;
            _terrainStreamer.PreferGpuLodMeshing = false;
        }
    }

    private void EnsureTerrainMeshPool(GL gl)
    {
        if (_terrainMeshPool is { IsValid: true })
        {
            EnsureTerrainUploadStaging(gl);
            EnsureTerrainArenaAndTransferQueue();
            return;
        }

        _terrainMeshPool?.Dispose();
        _terrainMeshPool = new GlTerrainMeshPool(
            gl,
            useBaseVertex: _glCapabilities?.IsOpenGlEs != true);
        EnsureTerrainUploadStaging(gl);
        EnsureTerrainArenaAndTransferQueue();
        // The arena is the fixed-capacity admission authority. Materialize the same capacity in
        // the GL buffers before the first upload, then disable live growth. Previously the arena
        // advertised hundreds of MiB while the GL pool was frozen at its 3 MiB constructor size.
        if (_terrainMeshArena is { } arena &&
            !_terrainMeshPool.TryPreallocateFixedCapacity(
                arena.VertexCapacityBytes,
                arena.IndexCapacityBytes))
        {
            // Allocation failure must remain recoverable: keep transactional pool growth enabled
            // under its VRAM ceiling instead of permanently stranding terrain at bootstrap size.
            _terrainMeshPool.AllowLiveBufferGrowth = true;
            EmitDiagnostic(
                "[3D preview] Terrain fixed mesh-pool preallocation failed; retaining bounded " +
                $"transactional growth (failure={_terrainMeshPool.LastFailureReason}, " +
                $"glError={_terrainMeshPool.LastFailure}).");
        }
        _terrainIndirectCommands ??= new GlIndirectDrawCommandBuffer(gl);
        if (_terrainMultiDrawIndirectCount is null &&
            (gl.Context.TryGetProcAddress("glMultiDrawElementsIndirectCount", out var proc) ||
             gl.Context.TryGetProcAddress("glMultiDrawElementsIndirectCountARB", out proc)))
        {
            _terrainMultiDrawIndirectCount =
                Marshal.GetDelegateForFunctionPointer<GlTerrainMeshPool.MultiDrawElementsIndirectCountProc>(proc);
        }

        if (!_loggedTerrainMeshPoolReady)
        {
            _loggedTerrainMeshPoolReady = true;
            EmitDiagnostic(
                "[3D preview] Terrain mesh pool ready: shared VAO/VBO/EBO + segmented arena admission " +
                $"(baseVertex desktop; remapped indices on GLES; fixedCapacity=" +
                $"{_terrainMeshPool.TotalCapacityBytes / (1024 * 1024)} MiB; " +
                $"arena={_terrainMeshArena?.SegmentCount ?? 0}x" +
                $"{(_terrainMeshArena?.VertexSegmentBytes ?? 0) / (1024 * 1024)} MiB-vbo/" +
                $"{(_terrainMeshArena?.IndexSegmentBytes ?? 0) / (1024 * 1024)} MiB-ebo; " +
                $"page={(_terrainMeshArena?.VertexPageBytes ?? 0) / 1024} KiB; " +
                $"transitionReserve={((long)(_terrainMeshArena?.TransitionVertexHeadroomBytes ?? 0) + (_terrainMeshArena?.TransitionIndexHeadroomBytes ?? 0)) / (1024 * 1024)} MiB; " +
                $"liveGrowth={(_terrainMeshPool.AllowLiveBufferGrowth ? "fallback" : "off")}).");
        }
    }

    private void EnsureTerrainArenaAndTransferQueue()
    {
        if (_terrainMeshArena is not null && _terrainTransferQueue is not null)
        {
            return;
        }

        var profile = _terrainStreamingProfile.MeshArenaBytes > 0
            ? _terrainStreamingProfile
            : TerrainStreamingProfile.Resolve(
                PreviewTerrainStreamingMode.Balanced,
                Environment.ProcessorCount,
                dedicatedVramBytes: 0,
                persistentTransferSupported: true);
        _terrainStreamingProfile = profile;
        var segmentBytes = Math.Max(profile.TransferSegmentBytes, profile.MeshArenaPageBytes);
        var preferredSegmentCount = Math.Clamp(profile.TransferSegmentCount, 2, 8);
        var pageBytes = Math.Max(4 * 1024, profile.MeshArenaPageBytes);
        // Align segment size to page multiples required by the arena.
        segmentBytes = Math.Max(pageBytes, (segmentBytes / pageBytes) * pageBytes);
        var pairBytes = checked(segmentBytes * 2);
        var requestedArenaBytes = Math.Max(
            profile.MeshArenaBytes,
            (long)pairBytes * preferredSegmentCount);
        var layout = GlTerrainMeshArena.ResolveSegmentLayout(
            requestedArenaBytes,
            preferredSegmentCount,
            pairBytes,
            pageBytes);
        var segmentCount = layout.SegmentCount;
        var vertexSegmentBytes = layout.VertexSegmentBytes;
        var indexSegmentBytes = layout.IndexSegmentBytes;
        var realizedVertexBytes = checked((long)vertexSegmentBytes * segmentCount);
        var realizedIndexBytes = checked((long)indexSegmentBytes * segmentCount);
        var realizedArenaBytes = checked(realizedVertexBytes + realizedIndexBytes);
        var vertexHeadroom = GlTerrainMeshArena.ResolveTransitionHeadroomPerSegment(
            requestedArenaBytes: requestedArenaBytes,
            requestedTransitionReserveBytes: profile.TransitionReserveBytes,
            realizedArenaBytes: realizedArenaBytes,
            realizedStreamBytes: realizedVertexBytes,
            segmentCount: segmentCount,
            pageBytes: pageBytes,
            segmentBytes: vertexSegmentBytes);
        var indexHeadroom = GlTerrainMeshArena.ResolveTransitionHeadroomPerSegment(
            requestedArenaBytes: requestedArenaBytes,
            requestedTransitionReserveBytes: profile.TransitionReserveBytes,
            realizedArenaBytes: realizedArenaBytes,
            realizedStreamBytes: realizedIndexBytes,
            segmentCount: segmentCount,
            pageBytes: pageBytes,
            segmentBytes: indexSegmentBytes);
        _terrainMeshArena = new GlTerrainMeshArena(
            segmentCount: segmentCount,
            vertexSegmentBytes: vertexSegmentBytes,
            indexSegmentBytes: indexSegmentBytes,
            vertexPageBytes: pageBytes,
            indexPageBytes: pageBytes,
            transitionVertexHeadroomBytes: vertexHeadroom,
            transitionIndexHeadroomBytes: indexHeadroom);
        _terrainTransferQueue = new GlTerrainTransferQueue(
            _terrainMeshArena,
            stagingSegmentCount: Math.Max(2, profile.TransferSegmentCount),
            stagingSegmentBytes: Math.Max(1, profile.TransferSegmentBytes),
            maxBytesPerFrame: Math.Max(1, (int)Math.Min(int.MaxValue, profile.UploadBytesPerFrame)),
            maxChunksPerFrame: Math.Max(1, PreviewStageConstants.TerrainMaxChunkUploadsPerFrameCatchUp));

        var token = _terrainStreamer?.DemandTracker.CurrentToken ?? new TerrainDemandToken(1, 0);
        _terrainCoverageGraph = new TerrainCoverageGraph(
            realizedArenaBytes,
            token.ContentGeneration,
            token.DemandRevision);
    }

    private void EnsureTerrainUploadStaging(GL gl)
    {
        if (_terrainUploadStaging is { IsValid: true })
        {
            return;
        }

        if (_glCapabilities?.CanUsePersistentUploadRing != true)
        {
            return;
        }

        _terrainUploadStaging?.Dispose();
        _terrainUploadStaging = new GlTerrainUploadStagingRing(gl, preferPersistent: true);
        if (!_loggedTerrainUploadStaging)
        {
            _loggedTerrainUploadStaging = true;
            var mode = _terrainUploadStaging.UsesPersistentMapping ? "persistent-mapped" : "BufferSubData";
            EmitDiagnostic(
                $"[3D preview] Terrain upload staging ring ready ({mode}, P10.1; pack+EndFrame, no per-chunk flush).");
        }
    }

    /// <summary>
    /// During late bootstrap (PassScene not running yet), tick Full streaming so workers bake and
    /// GPU uploads land before CoreReady — avoids dismissing into / lingering on a black pad.
    /// </summary>
    private void WarmStartTerrainStreamingBootstrap()
    {
        if (_gl is null ||
            !_settings.ShowGroundMesh ||
            !_grassGroundReady ||
            _terrainStreamer is null)
        {
            return;
        }

        ComposeOrbitEye(
            _orbitBaseTarget,
            _orbitPan,
            _orbitYaw,
            _orbitPitch,
            _orbitDistance,
            out var eye,
            out _);
        var frame = new GlRenderFrame
        {
            Gl = _gl,
            Settings = _settings,
            Eye = eye,
        };
        TickTerrainStreaming(ref frame);
        // EnsureTerrainMeshPool preallocates the arena-sized immutable backing store. Do not
        // blindly disable growth here: if that one-time allocation failed, bounded transactional
        // growth is the recovery path rather than a permanent 3 MiB residency ceiling.
    }

    private void TickTerrainStreaming(ref GlRenderFrame frame)
    {
        var clearLodCache = false;
        lock (_sync)
        {
            if (_pendingTerrainLodCacheClear)
            {
                clearLodCache = true;
                _pendingTerrainLodCacheClear = false;
                _terrainStreamingNeedsFrames = true;
            }
        }

        if (clearLodCache)
        {
            EnsureTerrainStreamer();
            DisposeTerrainGpuChunks();
            _terrainStreamer!.InvalidateAll();
        }

        if (!frame.Settings.ShowGroundMesh)
        {
            return;
        }

        EnsureTerrainStreamer();
        var terrainStreamStarted = Stopwatch.GetTimestamp();
        var resolvedProfile = TerrainStreamingProfile.Resolve(
            frame.Settings.TerrainStreamingMode,
            Environment.ProcessorCount,
            _glCapabilities?.DedicatedVideoMemoryBytes ?? 0,
            _glCapabilities?.CanUsePersistentUploadRing == true);
        if (_terrainAdaptiveBudgetController is null ||
            _terrainStreamingProfile != resolvedProfile)
        {
            _terrainStreamingProfile = resolvedProfile;
            _terrainAdaptiveBudgetController = new TerrainAdaptiveBudgetController(resolvedProfile);
            _terrainStreamer!.StreamingProfile = resolvedProfile;
        }

        // Set the bootstrap gate before Tick publishes desired work. Workers run concurrently,
        // so assigning this later in the method allowed them to unlock the 512-chunk LOD ring
        // between Tick and the pool-pressure calculation.
        var bootstrapHold = !_gpuAlive ||
                            _gpuBootstrap is not null ||
                            !_gpuInitProgress.IsFullyReady ||
                            !_terrainStartupReadyLatched;
        _terrainStreamer!.HoldScheduleExpansion = bootstrapHold;
        var viewDist = frame.Settings.ChunkViewDistance;
        _terrainStreamer.Tick(frame.Eye, viewDist, frame.Settings.LodRingChunks);
        // Do not spend CPU baking bootstrap settings that the first full PassSetup may replace.
        // PassSetup runs before this post-Core scene tick, so workers see final grass/worldgen
        // fingerprints and no longer bake hundreds of meshes that InvalidateAll immediately drops.
        if (_gpuAlive && _gpuBootstrap is null)
        {
            _terrainStreamer.Start();
        }

        var cameraChunk = _terrainStreamer.CameraChunk;
        if (_terrainDeferredCameraChunk is { } deferredCamera && deferredCamera != cameraChunk)
        {
            // Camera moved: drop deferred marks so uploads can retry, but only un-park
            // (NotifyUnloaded) keys that are still without a GPU mesh. Mass-unparking every
            // chunk step thrashed bake/upload without helping near coverage.
            ReleaseDeferredTerrainMarks(unparkMissingGpu: true);
            _terrainDeferredCameraChunk = cameraChunk;
        }
        else
        {
            _terrainDeferredCameraChunk ??= cameraChunk;
        }

        // Soft-start grew: re-open budget-parked keys now inside the unlocked annular window.
        UnparkDeferredInsideScheduleWindow();

        // Rebuilds when desired LOD differs from resident.
        var desired = _terrainStreamer.SnapshotDesired();
        foreach (var (key, have) in _terrainGpuChunks)
        {
            if (desired.TryGetValue(key, out var want) &&
                TerrainChunkStreamer.NeedsRebuild(have.Lod, want))
            {
                _terrainStreamer.InvalidateForRebuild(key);
            }
        }

        EnsureTerrainMeshPool(frame.Gl);
        var dedicatedVram = _glCapabilities?.DedicatedVideoMemoryBytes ?? 0;
        var liveHighWater = ResolveTerrainMeshPoolLiveHighWaterBytes();
        var targetBudget = PreviewStageConstants.ResolveTerrainMeshPoolBudgetBytes(
            _terrainStreamer.HardRadiusChunks,
            _terrainStreamer.LodRingChunks,
            dedicatedVram,
            liveHighWater);
        var absoluteCeiling = PreviewStageConstants.ResolveTerrainMeshPoolCeilingBytes(dedicatedVram);
        _terrainMeshPool?.ConfigureBudgetCeiling(targetBudget, absoluteCeiling);
        MaybeLogTerrainMeshPoolBudget(targetBudget, absoluteCeiling, dedicatedVram);

        var poolPressure = UpdateTerrainPoolPressureLatch();
        var hardRadius = _terrainStreamer.HardRadiusChunks;
        var pinKeepRadius = TerrainChunkStreamer.ResolveLodPinKeepRadiusChunks(hardRadius);
        var transitionIncomplete = HasIncompleteTerrainTransitionCoverage(
            desired, cameraChunk, hardRadius);
        var pinnedObsoleteLod = HasPinnedObsoleteLodWithoutReplacement(
            desired, cameraChunk, pinKeepRadius);
        // Never freeze soft-start unlock while Full/LOD1 seam coverage is still incomplete —
        // that latches holes. Also keep unlocking while obsolete LOD is pinned waiting for
        // band replacements (otherwise pins hold VRAM forever under pressure).
        // Hold expansion for the whole Core bootstrap so distant LOD / disk-warm cannot starve
        // the native WGL 14ms/frame compile slices (logs: Core GPU init ~157s with scheduleMax=520).
        _terrainStreamer.HoldScheduleExpansion =
            bootstrapHold ||
            (poolPressure && !transitionIncomplete && !pinnedObsoleteLod);

        var disposalCap = poolPressure
            ? PreviewStageConstants.TerrainMaxChunkDisposalsPerFramePressure
            : PreviewStageConstants.TerrainMaxChunkDisposalsPerFrame;

        // Upload replacements BEFORE disposing obsolete LOD — split Full/LOD quotas so Full
        // catch-up cannot starve LOD section uploads (shared caps made LOD feel stuck).
        var telemetryBefore = _terrainStreamer.Telemetry.Snapshot();
        var adaptiveBudget = _terrainAdaptiveBudgetController!.Update(
            frame.RenderTime,
            telemetryBefore.StreamCpuP95Ms,
            stagingBackpressured: false,
            memoryPressured: poolPressure,
            coverageDebt: transitionIncomplete ? 1 : 0);
        var uploadBytes = adaptiveBudget.UploadBytes;
        var maxFullUploads = Math.Max(1, adaptiveBudget.BakeConcurrency);
        var maxLodUploads = Math.Max(1, adaptiveBudget.BakeConcurrency);
        var maxFullBytes = Math.Max(256 * 1024L, uploadBytes / 2);
        var maxLodBytes = Math.Max(256 * 1024L, uploadBytes - maxFullBytes);
        var fullUploads = new List<PreviewTerrainChunkMesh>(maxFullUploads);
        var lodUploads = new List<PreviewTerrainChunkMesh>(maxLodUploads);
        _terrainStreamer.DrainReadySplit(
            fullUploads,
            lodUploads,
            maxFullUploads,
            maxLodUploads,
            maxFullBytes,
            maxLodBytes);
        PrioritizeTerrainUploadsByCameraDistance(fullUploads, cameraChunk);
        PrioritizeTerrainUploadsByCameraDistance(lodUploads, cameraChunk);
        var uploads = new List<PreviewTerrainChunkMesh>(fullUploads.Count + lodUploads.Count);
        uploads.AddRange(fullUploads);
        uploads.AddRange(lodUploads);
        _lastTerrainFullUploads = fullUploads.Count;
        _lastTerrainLodUploads = lodUploads.Count;

        // Drop stale deferred marks for keys we are about to retry so a prior budget-ceiling
        // cannot permanently skip camera-local uploads after VRAM was freed.
        foreach (var cpu in uploads)
        {
            _terrainDeferredChunks.Remove(cpu.Key);
            UploadTerrainChunk(frame.Gl, cpu);
        }

        // One fence for the packed staging segment — not per-chunk (avoids mid-frame GPU stalls).
        _terrainUploadStaging?.EndFrame();

        // Evict trailing / out-of-desired residents so the bounded pool can follow the camera.
        // Do NOT force-dump the soft trail every pressure frame — that flashes near chunks.
        // Out-of-desired LOD stays pinned until footprint replacements are GPU-resident.
        var gpuBeforeDispose = _terrainGpuChunks.Count;
        DisposeTerrainGpuResidents(desired, cameraChunk, disposalCap, forceNonDesired: false);
        _lastTerrainDisposals = Math.Max(0, gpuBeforeDispose - _terrainGpuChunks.Count);

        if (_terrainDeferredChunks.Count > 0)
        {
            List<TerrainResidencyKey>? deferredToRemove = null;
            foreach (var key in _terrainDeferredChunks)
            {
                if (desired.ContainsKey(key) && !_terrainStreamer.ShouldUnload(key))
                {
                    continue;
                }

                deferredToRemove ??= [];
                deferredToRemove.Add(key);
            }

            if (deferredToRemove is not null)
            {
                foreach (var key in deferredToRemove)
                {
                    _terrainDeferredChunks.Remove(key);
                    _terrainStreamer.NotifyUnloaded(key);
                }
            }
        }

        RefreshTerrainEnvBounds();

        // Deferred-without-mesh must NOT count as satisfied — that stopped the render pump
        // while near Full keys were still empty and obsolete LOD covered the pad.
        var scheduleMax = _terrainStreamer.ScheduleMaxRing;
        var scheduleComplete = scheduleMax >= _terrainStreamer.LodRadiusChunks;
        var missingDesiredGpu = 0;
        foreach (var key in desired.Keys)
        {
            if (_terrainGpuChunks.ContainsKey(key))
            {
                continue;
            }

            // Soft-start: only unlocked LOD rings (+ always-on Full) must be GPU-resident.
            if (key.IsFull ||
                TerrainStreamSchedule.RingIndex(key, cameraChunk) <= scheduleMax)
            {
                missingDesiredGpu++;
            }
        }

        var obsoleteLodUnderFull = false;
        var hard = _terrainStreamer.HardRadiusChunks;
        foreach (var key in _terrainGpuChunks.Keys)
        {
            if (TerrainChunkStreamer.IsObsoleteLodUnderFullDisk(
                    key,
                    desired.ContainsKey(key),
                    cameraChunk,
                    hard))
            {
                obsoleteLodUnderFull = true;
                break;
            }
        }

        var hasRetryDeferred = false;
        foreach (var key in _terrainDeferredChunks)
        {
            if (!_terrainGpuChunks.ContainsKey(key))
            {
                hasRetryDeferred = true;
                break;
            }
        }

        var needsFrames = missingDesiredGpu > 0 ||
                          !scheduleComplete ||
                          obsoleteLodUnderFull ||
                          pinnedObsoleteLod ||
                          uploads.Count > 0 ||
                          poolPressure ||
                          transitionIncomplete ||
                          hasRetryDeferred;
        lock (_sync)
        {
            _terrainStreamingNeedsFrames = needsFrames;
        }

        var arenaTelemetry = _terrainMeshArena?.GetTelemetry();
        _terrainStreamer.Telemetry.SetGpuState(
            arenaTelemetry is { } arenaState
                ? arenaState.LiveVertexBytes + arenaState.LiveIndexBytes
                : SumTerrainGpuResidentBytes(),
            arenaTelemetry is { } reservedState
                ? reservedState.ReservedVertexBytes + reservedState.ReservedIndexBytes
                : 0,
            arenaTelemetry is { } retiringState
                ? retiringState.RetiringVertexBytes + retiringState.RetiringIndexBytes
                : 0);
        _terrainStreamer.Telemetry.SetCoverageState(
            missingDesiredGpu,
            transitionIncomplete || pinnedObsoleteLod ? 1 : 0);
        _terrainStreamer.Telemetry.RecordStreamCpuFrame(
            Stopwatch.GetElapsedTime(terrainStreamStarted).TotalMilliseconds);
        MaybeLogTerrainResidencyBreakdown();

        // Keep GPU init overlay honest while the Full disk is still empty after CoreReady.
        if (!_terrainStartupReadyLatched &&
            !_gpuInitProgress.IsFullyReady &&
            _gpuInitTier.HasAll(PreviewGpuInitTier.Core) &&
            ResolveTerrainInitProgressFraction() < 1.0)
        {
            RaiseGpuInitProgress(PreviewGpuInitPhases.UploadingMeshes, frame.Settings);
        }
    }

    private void ReleaseDeferredTerrainMarks(bool unparkMissingGpu)
    {
        if (_terrainStreamer is null || _terrainDeferredChunks.Count == 0)
        {
            return;
        }

        if (unparkMissingGpu)
        {
            foreach (var key in _terrainDeferredChunks)
            {
                // Fake-parked keys have no GPU mesh — clear the streamer resident mark so
                // workers can rebake. Keys that already uploaded keep residency.
                if (!_terrainGpuChunks.ContainsKey(key))
                {
                    _terrainStreamer.NotifyUnloaded(key);
                }
            }
        }

        _terrainDeferredChunks.Clear();
    }

    private bool UpdateTerrainPoolPressureLatch()
    {
        var pool = _terrainMeshPool;
        if (pool is null)
        {
            _terrainPoolPressureLatched = false;
            return false;
        }

        // Use live resident bytes — TotalCapacityBytes never shrinks after growth and would
        // latch pressure forever (soft-start HoldScheduleExpansion → empty LOD rings).
        var liveBytes = SumTerrainGpuResidentBytes();
        var poolRatio = liveBytes / (double)Math.Max(1L, pool.MaxTotalBufferBytes);
        var arenaRatio = 0d;
        var arenaBlocked = false;
        if (_terrainMeshArena is { } arena)
        {
            var telemetry = arena.GetTelemetry();
            var ordinaryVertexCapacity = Math.Max(
                1L,
                (long)telemetry.VertexCapacityBytes - arena.TransitionVertexHeadroomBytes);
            var ordinaryIndexCapacity = Math.Max(
                1L,
                (long)telemetry.IndexCapacityBytes - arena.TransitionIndexHeadroomBytes);
            arenaRatio = Math.Max(
                (telemetry.VertexCapacityBytes - telemetry.FreeVertexBytes) /
                    (double)ordinaryVertexCapacity,
                (telemetry.IndexCapacityBytes - telemetry.FreeIndexBytes) /
                    (double)ordinaryIndexCapacity);
            arenaBlocked =
                (_terrainArenaFailedVertexBytes > 0 &&
                 telemetry.LargestFreeVertexRangeBytes < _terrainArenaFailedVertexBytes) ||
                (_terrainArenaFailedIndexBytes > 0 &&
                 telemetry.LargestFreeIndexRangeBytes < _terrainArenaFailedIndexBytes);
        }

        var ratio = Math.Max(poolRatio, arenaRatio);
        if (_terrainPoolPressureLatched)
        {
            if (!arenaBlocked &&
                ratio < PreviewStageConstants.TerrainMeshPoolPressureExitRatio)
            {
                _terrainPoolPressureLatched = false;
                _terrainArenaFailedVertexBytes = 0;
                _terrainArenaFailedIndexBytes = 0;
            }
        }
        else if (arenaBlocked ||
                 ratio >= PreviewStageConstants.TerrainMeshPoolPressureEnterRatio)
        {
            _terrainPoolPressureLatched = true;
        }

        return _terrainPoolPressureLatched;
    }

    private long ResolveTerrainMeshPoolLiveHighWaterBytes()
    {
        if (_terrainMeshPool is null)
        {
            return 0;
        }

        return _terrainMeshPool.VertexHighWaterBytes + _terrainMeshPool.IndexHighWaterBytes;
    }

    private long SumTerrainGpuResidentBytes()
    {
        long liveBytes = 0;
        foreach (var chunk in _terrainGpuChunks.Values)
        {
            liveBytes += (long)chunk.Allocation.VertexFloatCount * sizeof(float);
            liveBytes += (long)chunk.Allocation.IndexCount * sizeof(uint);
        }

        return liveBytes;
    }

    private bool TryRaiseTerrainMeshPoolBudget()
    {
        if (_terrainMeshPool is null || _terrainStreamer is null)
        {
            return false;
        }

        var dedicatedVram = _glCapabilities?.DedicatedVideoMemoryBytes ?? 0;
        var target = PreviewStageConstants.ResolveTerrainMeshPoolBudgetBytes(
            _terrainStreamer.HardRadiusChunks,
            _terrainStreamer.LodRingChunks,
            dedicatedVram,
            ResolveTerrainMeshPoolLiveHighWaterBytes());
        var absoluteCeiling = PreviewStageConstants.ResolveTerrainMeshPoolCeilingBytes(dedicatedVram);
        // If already at the scaled target, nudge toward the hardware-derived ceiling before evicting.
        if (target <= _terrainMeshPool.MaxTotalBufferBytes)
        {
            target = Math.Min(
                absoluteCeiling,
                _terrainMeshPool.MaxTotalBufferBytes + 256L * 1024L * 1024L);
        }

        _terrainMeshPool.ConfigureBudgetCeiling(
            Math.Max(target, _terrainMeshPool.MaxTotalBufferBytes),
            absoluteCeiling);
        return _terrainMeshPool.TryRaiseBudgetCeiling(target);
    }

    private bool _loggedTerrainMeshPoolBudget;

    private void MaybeLogTerrainMeshPoolBudget(long targetBudget, long absoluteCeiling, long dedicatedVram)
    {
        if (_loggedTerrainMeshPoolBudget || _terrainMeshPool is null)
        {
            return;
        }

        _loggedTerrainMeshPoolBudget = true;
        var source = _glCapabilities?.VideoMemorySource ?? "unavailable";
        var residency = FormatTerrainResidencyDiagBreakdown();
        if (dedicatedVram > 0)
        {
            EmitDiagnostic(
                "[3D preview] Terrain mesh pool budget " +
                $"{targetBudget / (1024 * 1024)} MiB " +
                $"(ceiling {absoluteCeiling / (1024 * 1024)} MiB from {dedicatedVram / (1024 * 1024)} MiB " +
                $"dedicated VRAM via {source}; overflow stays on CPU defer/evict; {residency}).");
        }
        else
        {
            EmitDiagnostic(
                "[3D preview] Terrain mesh pool budget " +
                $"{targetBudget / (1024 * 1024)} MiB " +
                $"(VRAM undetected — using unknown-adapter ceiling " +
                $"{absoluteCeiling / (1024 * 1024)} MiB; overflow stays on CPU defer/evict; {residency}).");
        }
    }

    private bool _loggedTerrainResidencyBreakdown;
    private long _lastTerrainResidencyDiagTickMs;
    private int _lastTerrainFullUploads;
    private int _lastTerrainLodUploads;
    private int _lastTerrainDisposals;

    private void MaybeLogTerrainResidencyBreakdown()
    {
        if (_terrainStreamer is null)
        {
            return;
        }

        var desired = _terrainStreamer.SnapshotDesired();
        if (desired.Count < 8)
        {
            return;
        }

        var nowMs = Environment.TickCount64;
        var intervalMs = (long)(PreviewStageConstants.TerrainResidencyDiagIntervalSeconds * 1000.0);
        if (_loggedTerrainResidencyBreakdown &&
            nowMs - _lastTerrainResidencyDiagTickMs < intervalMs)
        {
            return;
        }

        _loggedTerrainResidencyBreakdown = true;
        _lastTerrainResidencyDiagTickMs = nowMs;
        var pinned = HasPinnedObsoleteLodWithoutReplacement(
            desired,
            _terrainStreamer.CameraChunk,
            TerrainChunkStreamer.ResolveLodPinKeepRadiusChunks(_terrainStreamer.HardRadiusChunks));
        var telemetry = _terrainStreamer.Telemetry.Snapshot();
        var cacheStats = _terrainStreamer.LodDiskCache.GetStats();
        var arenaStats = _terrainMeshArena?.GetTelemetry();
        var arenaDiagnostic = arenaStats is { } stats
            ? $", arenaLive={stats.LiveVertexBytes}/{stats.LiveIndexBytes}, " +
              $"arenaReserved={stats.ReservedVertexBytes}/{stats.ReservedIndexBytes}, " +
              $"arenaFree={stats.FreeVertexBytes}/{stats.FreeIndexBytes}, " +
              $"arenaLargest={stats.LargestFreeVertexRangeBytes}/" +
              $"{stats.LargestFreeIndexRangeBytes}, " +
              $"arenaFragmentation={stats.VertexFragmentation:0.###}/" +
              $"{stats.IndexFragmentation:0.###}"
            : "";
        EmitDiagnostic(
            "[3D preview] Terrain residency " +
            FormatTerrainResidencyDiagBreakdown(desired) +
            $", uploadedFull={_lastTerrainFullUploads}, uploadedLod={_lastTerrainLodUploads}, " +
            $"disposed={_lastTerrainDisposals}, pinnedObsolete={pinned}, " +
            $"workers={_terrainStreamer.WorkerCount}, inflight={_terrainStreamer.InflightCount}, " +
            $"ready={_terrainStreamer.ReadyCount}/{_terrainStreamer.ReadyUniqueCount}, " +
            $"bakedFull={_terrainStreamer.FullBakeCompletedCount}, " +
            $"bakedLod={_terrainStreamer.LodBakeCompletedCount}, " +
            $"diskWarm={_terrainStreamer.DiskWarmCompletedCount}, " +
            $"cacheStores={cacheStats.Stores}, cacheHits={cacheStats.Hits}, " +
            $"cacheMisses={cacheStats.Misses}, cacheStoreFaults={cacheStats.StoreFailures}, " +
            $"cacheRecoveries={cacheStats.Recoveries}, cacheEvictions={cacheStats.Evictions}, " +
            $"lastFullBakeMs={_terrainStreamer.LastFullBakeMilliseconds}, " +
            $"streamCpuP95Ms={telemetry.StreamCpuP95Ms:0.###}, " +
            $"plannerMs={telemetry.PlannerMilliseconds:0.###}, " +
            $"staleDrops={telemetry.SchedulerStaleDrops}, coverageDebt={telemetry.CoverageDebt}, " +
            $"recoveryEvicted={_terrainRecoveryEvictionCount}, " +
            $"gpuActiveBytes={telemetry.ActiveGpuBytes}, gpuReservedBytes={telemetry.ReservedGpuBytes}, " +
            $"arenaPressure={_terrainPoolPressureLatched}{arenaDiagnostic}, " +
            $"profile={_terrainStreamingProfile.Mode}, " +
            $"bakeFaults={_terrainStreamer.BakeFaultCount}, " +
            $"lastBakeFault={_terrainStreamer.LastBakeFault}.");
    }

    private string FormatTerrainResidencyDiagBreakdown(
        IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind>? desired = null)
    {
        if (_terrainStreamer is null)
        {
            return "gpuResident=0, fakeParked=0, unlockedDesired=0, desiredTotal=0, deferredRetry=0, scheduleMax=0";
        }

        desired ??= _terrainStreamer.SnapshotDesired();
        var counts = TerrainResidencyDiagnostics.Count(
            desired,
            _terrainGpuChunks.Keys,
            _terrainDeferredChunks,
            _terrainStreamer.IsMarkedResident,
            _terrainStreamer.CameraChunk,
            _terrainStreamer.ScheduleMaxRing);
        return counts.Format();
    }

    private static void PrioritizeTerrainUploadsByCameraDistance(
        List<PreviewTerrainChunkMesh> uploads,
        TerrainChunkKey cameraChunk)
    {
        if (uploads.Count <= 1)
        {
            return;
        }

        uploads.Sort((a, b) =>
            TerrainStreamSchedule.CompareKeys(a.Key, b.Key, cameraChunk));
    }

    /// <summary>
    /// Unloads hard-out-of-range residents, then soft-unloads / pressure-evicts keys that left
    /// the desired set so extreme LOD rings can track the camera inside the VRAM budget.
    /// Residents crossing the camera-local transition skirt stay pinned until every protected
    /// cell has replacement GPU coverage. The far trail is retired by distance/pressure only.
    /// </summary>
    private void DisposeTerrainGpuResidents(
        IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> desired,
        TerrainChunkKey cameraChunk,
        int disposalCap,
        bool forceNonDesired)
    {
        if (_terrainStreamer is null || _terrainGpuChunks.Count == 0 || disposalCap <= 0)
        {
            return;
        }

        var hardRadius = _terrainStreamer.HardRadiusChunks;
        var renderRadius = _terrainStreamer.LodRadiusChunks;
        var gpuResidents = _terrainGpuChunks.Keys.ToHashSet();
        List<(int Rank, TerrainResidencyKey Key)>? ranked = null;
        foreach (var (key, _) in _terrainGpuChunks)
        {
            var inDesired = desired.ContainsKey(key);
            var hardUnload = _terrainStreamer.ShouldUnload(key);
            if (inDesired && !hardUnload)
            {
                _terrainRetireAfterSeconds.Remove(key);
                continue;
            }

            var dist = key.ChebyshevDistanceToChunk(cameraChunk);
            // Protect real GPU coverage throughout the render window. Soft-start controls bake
            // order only; distant residents remain visible and must survive until replacement.
            // The resident HashSet is shared across the pass to keep this linear-time.
            var protectionRadius = !hardUnload && !inDesired
                ? TerrainChunkStreamer.ResolveReplacementProtectionRadiusChunks(
                    key,
                    cameraChunk,
                    hardRadius,
                    renderRadius)
                : -1;
            if (protectionRadius >= 0 &&
                !HasGpuFootprintReplacementCoverage(
                    key,
                    cameraChunk,
                    protectionRadius,
                    gpuResidents))
            {
                // Coverage can regress after a retirement timer starts (camera movement or an
                // allocation retry). Cancel the fade immediately; retaining a timer whose end
                // has passed keeps the only available underlay resident but fully transparent.
                _terrainRetireAfterSeconds.Remove(key);
                continue;
            }

            if (!hardUnload && !inDesired)
            {
                if (!_terrainRetireAfterSeconds.TryGetValue(key, out var retireAfter))
                {
                    _terrainRetireAfterSeconds[key] = _renderTimeAccum + TerrainTransitionSeconds;
                    continue;
                }

                if (_renderTimeAccum < retireAfter)
                {
                    continue;
                }
            }

            // Pin out-of-desired LOD only across the transition skirt (hard + fade), not the
            // entire lod ring — full-ring pins held VRAM and stalled unlock under pressure.
            if (!hardUnload && key.IsLod && !inDesired)
            {
                var softHysteresis = TerrainChunkStreamer.ResolveSoftUnloadHysteresisChunks(key);
                if (protectionRadius < 0 &&
                    dist <= softHysteresis &&
                    !(forceNonDesired && !inDesired))
                {
                    continue;
                }
            }
            else if (!hardUnload)
            {
                var softHysteresis = TerrainChunkStreamer.ResolveSoftUnloadHysteresisChunks(key);
                var obsoleteUnderFull = TerrainChunkStreamer.IsObsoleteLodUnderFullDisk(
                    key, inDesired, cameraChunk, hardRadius);
                var softUnload = obsoleteUnderFull || (!inDesired && dist > softHysteresis);
                if (!softUnload && !(forceNonDesired && !inDesired))
                {
                    continue;
                }
            }

            ranked ??= new List<(int, TerrainResidencyKey)>(_terrainGpuChunks.Count);
            ranked.Add((
                TerrainChunkStreamer.RankGpuDisposal(key, cameraChunk, hardRadius, inDesired),
                key));
        }

        if (ranked is null || ranked.Count == 0)
        {
            return;
        }

        ranked.Sort(static (a, b) => b.Rank.CompareTo(a.Rank));
        var disposed = 0;
        foreach (var (_, key) in ranked)
        {
            if (!_terrainGpuChunks.Remove(key, out var gpu))
            {
                continue;
            }

            ReleaseTerrainArenaAllocation(key);
            _terrainMeshPool?.Free(gpu.Allocation);
            _terrainStreamer.NotifyUnloaded(key);
            _terrainDeferredChunks.Remove(key);
            _terrainRetireAfterSeconds.Remove(key);
            disposed++;
            if (disposed >= disposalCap)
            {
                break;
            }
        }

        if (disposed > 0)
        {
            _terrainCandidatesChunkVersion++;
            InvalidateTerrainShadowWorldAabbCache();
        }
    }

    private bool HasGpuFootprintReplacementCoverage(
        TerrainResidencyKey lodSection,
        TerrainChunkKey cameraChunk,
        int keepRadiusChunks,
        IReadOnlySet<TerrainResidencyKey>? gpuResidents = null) =>
        TerrainChunkStreamer.HasFootprintReplacementCoverage(
            lodSection,
            cameraChunk,
            keepRadiusChunks,
            gpuResidents ?? _terrainGpuChunks.Keys.ToHashSet());

    private void UploadTerrainChunk(GL gl, PreviewTerrainChunkMesh cpu)
    {
        EnsureTerrainMeshPool(gl);
        var pool = _terrainMeshPool!;
        var staging = _terrainUploadStaging;
        if (_terrainStreamer is not null &&
            !_terrainGpuChunks.ContainsKey(cpu.Key) &&
            _terrainPoolPressureLatched)
        {
            var cam = _terrainStreamer.CameraChunk;
            var hard = _terrainStreamer.HardRadiusChunks;
            var desired = _terrainStreamer.SnapshotDesired();
            // Hard-reserve: under pressure, fill Full + transition underlay before distant LOD.
            if (!TerrainChunkStreamer.IsTransitionCoverageKey(cpu.Key, cam, hard) &&
                HasIncompleteTerrainTransitionCoverage(desired, cam, hard))
            {
                _terrainDeferredChunks.Add(cpu.Key);
                _terrainDeferredCameraChunk ??= cam;
                _terrainStreamer.ReturnReady(cpu);
                return;
            }
        }

        var replacingResident = _terrainGpuChunks.ContainsKey(cpu.Key);
        var isTransition = replacingResident ||
            HasOverlappingTerrainGpuRepresentation(cpu.Key) ||
            (_terrainStreamer is not null &&
             TerrainChunkStreamer.IsTransitionCoverageKey(
                 cpu.Key,
                 _terrainStreamer.CameraChunk,
                 _terrainStreamer.HardRadiusChunks));
        if (!TryAdmitTerrainArenaReservation(cpu, isTransition, out var arenaReservation))
        {
            _terrainDeferredChunks.Add(cpu.Key);
            _terrainDeferredCameraChunk ??= _terrainStreamer?.CameraChunk;
            _terrainStreamer?.ReturnReady(cpu);
            return;
        }

        if (_terrainGpuChunks.TryGetValue(cpu.Key, out var existing))
        {
            var replacement = pool.Upload(cpu.InterleavedVertices, cpu.Indices, staging);
            if (replacement.IsEmpty &&
                (pool.LastFailureReason is "budget-ceiling" or "live-growth-disabled") &&
                (TryRaiseTerrainMeshPoolBudget() || TryEvictTerrainForUploadBudget()))
            {
                // Temporary growth permission only for budget recovery after eviction.
                var priorGrowth = pool.AllowLiveBufferGrowth;
                pool.AllowLiveBufferGrowth = true;
                replacement = pool.Upload(cpu.InterleavedVertices, cpu.Indices, staging);
                pool.AllowLiveBufferGrowth = priorGrowth;
            }

            if (replacement.IsEmpty)
            {
                _terrainMeshArena?.Cancel(arenaReservation);
                // Preserve the last visible mesh; keep streamer resident so we do not spin rebakes.
                _terrainStreamer?.NotifyUploaded(cpu.Key, cpu.Lod);
                EmitTerrainPoolLimitDiagnostic(pool, cpu.Key, replacingVisibleChunk: true);
                return;
            }

            ReleaseTerrainArenaAllocation(cpu.Key);
            pool.Free(existing.Allocation);
            existing.Allocation = replacement;
            existing.Lod = cpu.Lod;
            existing.DrawBatches = RemapBatchesToPool(cpu.DrawBatches, existing.Allocation);
            existing.BoundsCenter = cpu.BoundsCenter;
            existing.BoundsRadius = cpu.BoundsRadius;
            existing.MinRelativeHeight = cpu.MinRelativeHeight;
            existing.MaxRelativeHeight = cpu.MaxRelativeHeight;
            existing.ActivatedAtSeconds = _renderTimeAccum;
            _terrainRetireAfterSeconds.Remove(cpu.Key);
            PublishTerrainArenaReservation(cpu.Key, arenaReservation);
            _terrainStreamer?.NotifyUploaded(cpu.Key, cpu.Lod);
            _terrainStreamer?.Telemetry.RecordUpload(cpu.UploadByteLength);
            _terrainCandidatesChunkVersion++;
            InvalidateTerrainShadowWorldAabbCache();
            return;
        }

        var allocation = pool.Upload(cpu.InterleavedVertices, cpu.Indices, staging);
        if (allocation.IsEmpty &&
            (pool.LastFailureReason is "budget-ceiling" or "live-growth-disabled") &&
            (TryRaiseTerrainMeshPoolBudget() || TryEvictTerrainForUploadBudget()))
        {
            var priorGrowth = pool.AllowLiveBufferGrowth;
            pool.AllowLiveBufferGrowth = true;
            allocation = pool.Upload(cpu.InterleavedVertices, cpu.Indices, staging);
            pool.AllowLiveBufferGrowth = priorGrowth;
        }

        if (allocation.IsEmpty)
        {
            _terrainMeshArena?.Cancel(arenaReservation);
            _terrainDeferredChunks.Add(cpu.Key);
            _terrainDeferredCameraChunk ??= _terrainStreamer?.CameraChunk;
            _terrainStreamer?.ReturnReady(cpu);

            if (pool.LastFailureReason is "budget-ceiling" or "live-growth-disabled")
            {
                DeferRemainingTerrainChunksAtPoolLimit();
            }

            EmitTerrainPoolLimitDiagnostic(pool, cpu.Key, replacingVisibleChunk: false);
            return;
        }

        _terrainDeferredChunks.Remove(cpu.Key);
        _terrainGpuChunks[cpu.Key] = new TerrainGpuChunk
        {
            Lod = cpu.Lod,
            Allocation = allocation,
            DrawBatches = RemapBatchesToPool(cpu.DrawBatches, allocation),
            BoundsCenter = cpu.BoundsCenter,
            BoundsRadius = cpu.BoundsRadius,
            MinRelativeHeight = cpu.MinRelativeHeight,
            MaxRelativeHeight = cpu.MaxRelativeHeight,
            ActivatedAtSeconds = _renderTimeAccum,
        };
        _terrainRetireAfterSeconds.Remove(cpu.Key);
        PublishTerrainArenaReservation(cpu.Key, arenaReservation);
        _terrainStreamer?.NotifyUploaded(cpu.Key, cpu.Lod);
        _terrainStreamer?.Telemetry.RecordUpload(cpu.UploadByteLength);
        _terrainCandidatesChunkVersion++;
        InvalidateTerrainShadowWorldAabbCache();
    }

    private bool TryAdmitTerrainArenaReservation(
        PreviewTerrainChunkMesh cpu,
        bool isTransition,
        out GlTerrainMeshArena.Reservation reservation)
    {
        reservation = default;
        EnsureTerrainArenaAndTransferQueue();
        var arena = _terrainMeshArena;
        if (arena is null)
        {
            return true;
        }

        var vertexBytes = checked(cpu.InterleavedVertices.Length * sizeof(float));
        var indexBytes = checked(cpu.Indices.Length * sizeof(uint));
        if (arena.TryReserve(vertexBytes, indexBytes, isTransition, out reservation))
        {
            return true;
        }

        // Reclaim fence-complete retirement before refusing refinement.
        arena.ReclaimCompleted(static _ => true);
        if (arena.TryReserve(vertexBytes, indexBytes, isTransition, out reservation))
        {
            return true;
        }

        // Arena admission is the real fixed-capacity authority. Surface its pressure to the
        // same eviction/throttle path as the GL pool and make room from safe, obsolete trail
        // residents immediately so queued replacements do not retry forever.
        _terrainArenaFailedVertexBytes = Math.Max(_terrainArenaFailedVertexBytes, vertexBytes);
        _terrainArenaFailedIndexBytes = Math.Max(_terrainArenaFailedIndexBytes, indexBytes);
        _terrainPoolPressureLatched = true;
        return TryEvictTerrainForUploadBudget() &&
               arena.TryReserve(vertexBytes, indexBytes, isTransition, out reservation);
    }

    private bool HasOverlappingTerrainGpuRepresentation(TerrainResidencyKey incoming)
    {
        var incomingX0 = incoming.OriginChunkX;
        var incomingZ0 = incoming.OriginChunkZ;
        var incomingX1 = incomingX0 + incoming.ChunksPerSide;
        var incomingZ1 = incomingZ0 + incoming.ChunksPerSide;
        foreach (var resident in _terrainGpuChunks.Keys)
        {
            if (resident == incoming)
            {
                continue;
            }

            var residentX0 = resident.OriginChunkX;
            var residentZ0 = resident.OriginChunkZ;
            var residentX1 = residentX0 + resident.ChunksPerSide;
            var residentZ1 = residentZ0 + resident.ChunksPerSide;
            if (incomingX0 < residentX1 && incomingX1 > residentX0 &&
                incomingZ0 < residentZ1 && incomingZ1 > residentZ0)
            {
                return true;
            }
        }

        return false;
    }

    private void PublishTerrainArenaReservation(
        TerrainResidencyKey key,
        GlTerrainMeshArena.Reservation reservation)
    {
        var arena = _terrainMeshArena;
        if (arena is null || reservation.Id == 0)
        {
            return;
        }

        if (!arena.TryPublish(reservation, out var allocation))
        {
            arena.Cancel(reservation);
            return;
        }

        _terrainArenaAllocations[key] = allocation;
    }

    private void ReleaseTerrainArenaAllocation(TerrainResidencyKey key)
    {
        if (_terrainMeshArena is null ||
            !_terrainArenaAllocations.Remove(key, out var allocation))
        {
            return;
        }

        _terrainTransferFrameToken++;
        _terrainMeshArena.Retire(allocation, _terrainTransferFrameToken);
        _terrainMeshArena.ReclaimCompleted(token => token <= _terrainTransferFrameToken);
    }

    private bool TryEvictTerrainForUploadBudget()
    {
        if (_terrainStreamer is null)
        {
            return false;
        }

        var desired = _terrainStreamer.SnapshotDesired();
        var cam = _terrainStreamer.CameraChunk;
        var before = _terrainGpuChunks.Count;
        DisposeTerrainGpuResidents(
            desired,
            cam,
            PreviewStageConstants.TerrainMaxChunkDisposalsPerFramePressure,
            forceNonDesired: true);
        if (_terrainGpuChunks.Count < before)
        {
            return true;
        }

        // Never evict a key that belongs to the target cut to admit another target key. That
        // creates a permanent rebake cycle and can regress a completed handoff. The fixed arena
        // now materializes the profile capacity; if no obsolete resident is safely retireable,
        // defer this upload until normal handoff disposal makes room.
        return false;
    }

    private bool EvictFarthestDesiredLodSections(
        IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> desired,
        TerrainChunkKey cameraChunk,
        int maxCount)
    {
        if (_terrainStreamer is null || maxCount <= 0)
        {
            return false;
        }

        var scheduleMax = _terrainStreamer.ScheduleMaxRing;
        List<(int Dist, byte LodLevel, TerrainResidencyKey Key)>? ranked = null;
        foreach (var (key, _) in _terrainGpuChunks)
        {
            if (!key.IsLod || !desired.ContainsKey(key))
            {
                continue;
            }

            // Never steal unlocked soft-start rings or near Full/LOD1 under the eye.
            var ring = TerrainStreamSchedule.RingIndex(key, cameraChunk);
            if (ring <= scheduleMax)
            {
                continue;
            }

            if (key.LodLevel <= 1 &&
                key.ChebyshevDistanceToChunk(cameraChunk) <= _terrainStreamer.HardRadiusChunks + 8)
            {
                continue;
            }

            ranked ??= [];
            ranked.Add((key.ChebyshevDistanceToChunk(cameraChunk), key.LodLevel, key));
        }

        if (ranked is null || ranked.Count == 0)
        {
            return false;
        }

        ranked.Sort(static (a, b) =>
        {
            var cmp = b.Dist.CompareTo(a.Dist);
            return cmp != 0 ? cmp : b.LodLevel.CompareTo(a.LodLevel);
        });

        var hard = _terrainStreamer.HardRadiusChunks;
        var disposed = 0;
        foreach (var (_, _, key) in ranked)
        {
            // Prefer leaving fade underlay resident; steal farther/coarser rings first.
            if (TerrainChunkStreamer.IsTransitionCoverageKey(key, cameraChunk, hard))
            {
                continue;
            }

            if (!_terrainGpuChunks.Remove(key, out var gpu))
            {
                continue;
            }

            ReleaseTerrainArenaAllocation(key);
            _terrainMeshPool?.Free(gpu.Allocation);
            _terrainStreamer.NotifyUnloaded(key);
            _terrainDeferredChunks.Remove(key);
            _terrainRetireAfterSeconds.Remove(key);
            disposed++;
            if (disposed >= maxCount)
            {
                break;
            }
        }

        if (disposed > 0)
        {
            _terrainCandidatesChunkVersion++;
            InvalidateTerrainShadowWorldAabbCache();
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when any Full / LOD1 (and adjacent LOD2 seam) desired key still lacks a GPU mesh.
    /// Soft-start must keep unlocking / retrying while this is true under pool pressure.
    /// </summary>
    private bool HasIncompleteTerrainTransitionCoverage(
        IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> desired,
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks)
    {
        foreach (var key in desired.Keys)
        {
            if (!TerrainChunkStreamer.IsTransitionCoverageKey(key, cameraChunk, hardRadiusChunks))
            {
                continue;
            }

            if (!_terrainGpuChunks.ContainsKey(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when an out-of-desired LOD section is still GPU-resident because its transition-skirt
    /// footprint has no replacement coverage yet. Soft-start must keep unlocking so replacements arrive.
    /// </summary>
    private bool HasPinnedObsoleteLodWithoutReplacement(
        IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> desired,
        TerrainChunkKey cameraChunk,
        int pinKeepRadiusChunks)
    {
        if (_terrainStreamer is null)
        {
            return false;
        }

        IReadOnlySet<TerrainResidencyKey>? gpuResidents = null;
        foreach (var key in _terrainGpuChunks.Keys)
        {
            if (!key.IsLod || desired.ContainsKey(key))
            {
                continue;
            }

            if (_terrainStreamer.ShouldUnload(key))
            {
                continue;
            }

            // Only transition-skirt pins block unlock; far trail uses soft hysteresis.
            if (!key.OverlapsFullDisk(cameraChunk, pinKeepRadiusChunks))
            {
                continue;
            }

            gpuResidents ??= _terrainGpuChunks.Keys.ToHashSet();
            if (!HasGpuFootprintReplacementCoverage(
                    key,
                    cameraChunk,
                    pinKeepRadiusChunks,
                    gpuResidents))
            {
                return true;
            }
        }

        return false;
    }

    private void DeferRemainingTerrainChunksAtPoolLimit()
    {
        if (_terrainStreamer is null)
        {
            return;
        }

        var cam = _terrainStreamer.CameraChunk;
        var hard = _terrainStreamer.HardRadiusChunks;
        var scheduleMax = _terrainStreamer.ScheduleMaxRing;
        foreach (var (key, _) in _terrainStreamer.SnapshotDesired())
        {
            if (_terrainGpuChunks.ContainsKey(key) || !_terrainDeferredChunks.Add(key))
            {
                continue;
            }

            // Only mass-park beyond the soft-start unlock window. Keys inside the annular
            // window stay retryable so budget pressure cannot freeze the horizon forever.
            var ring = TerrainStreamSchedule.RingIndex(key, cam);
            if (ring <= scheduleMax ||
                TerrainChunkStreamer.IsTransitionCoverageKey(key, cam, hard) ||
                ((key.IsFull || key.LodLevel <= 1) &&
                 key.ChebyshevDistanceToChunk(cam) <= hard + 8))
            {
                _terrainDeferredChunks.Remove(key);
                continue;
            }

            // Deferred is a scheduling state, never residency. The bounded scheduler retries
            // after capacity becomes available; only real GPU allocations call NotifyUploaded.
        }
    }

    /// <summary>
    /// After VRAM is freed, release deferred keys in clockwise annular schedule order so
    /// workers continue the same surround fill (not pure nearest Chebyshev).
    /// </summary>
    private void UnparkNearestDeferredTerrainKeys(int maxCount)
    {
        if (_terrainStreamer is null || maxCount <= 0 || _terrainDeferredChunks.Count == 0)
        {
            return;
        }

        var cam = _terrainStreamer.CameraChunk;
        List<(TerrainStreamSchedule.Rank Rank, TerrainResidencyKey Key)>? ranked = null;
        foreach (var key in _terrainDeferredChunks)
        {
            ranked ??= [];
            ranked.Add((TerrainStreamSchedule.RankKey(key, cam), key));
        }

        if (ranked is null)
        {
            return;
        }

        ranked.Sort(static (a, b) => TerrainStreamSchedule.Compare(a.Rank, b.Rank));
        var released = 0;
        foreach (var (_, key) in ranked)
        {
            if (!_terrainDeferredChunks.Remove(key))
            {
                continue;
            }

            _terrainStreamer.NotifyUnloaded(key);
            released++;
            if (released >= maxCount)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Soft-start unlock grew past previously mass-parked keys — clear fake residency so
    /// bakers can claim them inside the new annular window.
    /// </summary>
    private void UnparkDeferredInsideScheduleWindow()
    {
        if (_terrainStreamer is null || _terrainDeferredChunks.Count == 0)
        {
            return;
        }

        var cam = _terrainStreamer.CameraChunk;
        var maxRing = _terrainStreamer.ScheduleMaxRing;
        List<TerrainResidencyKey>? release = null;
        foreach (var key in _terrainDeferredChunks)
        {
            if (TerrainStreamSchedule.RingIndex(key, cam) <= maxRing)
            {
                release ??= [];
                release.Add(key);
            }
        }

        if (release is null)
        {
            return;
        }

        foreach (var key in release)
        {
            _terrainDeferredChunks.Remove(key);
            _terrainStreamer.NotifyUnloaded(key);
        }
    }

    private void EmitTerrainPoolLimitDiagnostic(
        GlTerrainMeshPool pool,
        TerrainResidencyKey key,
        bool replacingVisibleChunk)
    {
        if (_loggedTerrainPoolLimit)
        {
            return;
        }

        _loggedTerrainPoolLimit = true;
        long fullVertexBytes = 0;
        long fullIndexBytes = 0;
        long lodVertexBytes = 0;
        long lodIndexBytes = 0;
        foreach (var chunk in _terrainGpuChunks.Values)
        {
            var vertexBytes = (long)chunk.Allocation.VertexFloatCount * sizeof(float);
            var indexBytes = (long)chunk.Allocation.IndexCount * sizeof(uint);
            if (chunk.Lod == TerrainChunkLodKind.Full)
            {
                fullVertexBytes += vertexBytes;
                fullIndexBytes += indexBytes;
            }
            else
            {
                lodVertexBytes += vertexBytes;
                lodIndexBytes += indexBytes;
            }
        }

        EmitDiagnostic(
            $"[3D preview] Terrain mesh pool reached its safe GPU budget; preserving existing terrain " +
            $"and deferring additional chunks (capacity={pool.TotalCapacityBytes / (1024 * 1024)} MiB, " +
            $"budget={pool.MaxTotalBufferBytes / (1024 * 1024)} MiB, " +
            $"highWater={pool.VertexHighWaterBytes / (1024 * 1024)} MiB-vbo/" +
            $"{pool.IndexHighWaterBytes / (1024 * 1024)} MiB-ebo, " +
            $"fullLive={fullVertexBytes / (1024 * 1024)} MiB-vbo/{fullIndexBytes / (1024 * 1024)} MiB-ebo, " +
            $"lodLive={lodVertexBytes / (1024 * 1024)} MiB-vbo/{lodIndexBytes / (1024 * 1024)} MiB-ebo, " +
            $"failure={pool.LastFailureReason}, glError={pool.LastFailure}, " +
            $"residentChunks={_terrainGpuChunks.Count}, deferredKey={key}, " +
            $"replacement={replacingVisibleChunk}, {FormatTerrainResidencyDiagBreakdown()}).");
    }

    private static PreviewDrawBatch[] RemapBatchesToPool(
        PreviewDrawBatch[] source,
        in GlTerrainMeshPool.Allocation allocation)
    {
        if (source.Length == 0)
        {
            if (allocation.IndexCount <= 0)
            {
                return [];
            }

            return
            [
                new PreviewDrawBatch(allocation.IndexOffset, allocation.IndexCount, 0)
                {
                    BoundsCenter = Vector3.Zero,
                    BoundsRadius = -1f,
                }
            ];
        }

        var remapped = new PreviewDrawBatch[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            var batch = source[i];
            remapped[i] = batch with
            {
                FirstIndex = allocation.IndexOffset + batch.FirstIndex,
            };
        }

        return remapped;
    }

    private void RefreshTerrainEnvBounds()
    {
        if (_terrainGpuChunks.Count == 0)
        {
            var pad = PreviewStageConstants.TerrainDefaultChunkViewDistance *
                      PreviewStageConstants.TerrainChunkSize;
            _terrainEnvFloorY = PreviewStageConstants.GroundPlaneWorldY +
                                PreviewStageConstants.TerrainSolidFloorRelativeY;
            _terrainEnvCeilingY = PreviewStageConstants.GroundPlaneWorldY +
                                  PreviewStageConstants.TerrainMountainMaxReliefBlocks;
            _ = pad;
            return;
        }

        var minH = int.MaxValue;
        var maxH = int.MinValue;
        foreach (var c in _terrainGpuChunks.Values)
        {
            minH = Math.Min(minH, c.MinRelativeHeight);
            maxH = Math.Max(maxH, c.MaxRelativeHeight);
        }

        _terrainEnvFloorY = PreviewStageConstants.GroundPlaneWorldY + minH - 1f;
        _terrainEnvCeilingY = PreviewStageConstants.GroundPlaneWorldY + maxH;
    }

    private bool HasTerrainChunksToDraw => _terrainGpuChunks.Count > 0;

    private bool HasTerrainStreamerCameraChunk() =>
        _terrainStreamer is not null &&
        HasTerrainChunk(_terrainStreamer.CameraChunk);

    private bool HasTerrainChunk(TerrainChunkKey key) =>
        _terrainGpuChunks.TryGetValue(TerrainResidencyKey.Full(key), out var chunk) &&
        chunk.IndexCount > 0;

    private float TerrainEnvironmentHalfExtent
    {
        get
        {
            if (_terrainStreamer is null)
            {
                return PreviewStageConstants.TerrainDefaultChunkViewDistance *
                       PreviewStageConstants.TerrainChunkSize;
            }

            return Math.Max(
                _terrainStreamer.LodRingWorldRadius,
                PreviewStageConstants.TerrainChunkSize * 2f);
        }
    }

    private void DrawGroundTerrainChunks(
        GL gl,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        bool patches,
        bool enableParallaxSetting,
        Action<bool> setParallaxEnabled,
        bool shadowFullOnly = false,
        float maxCasterDistanceXz = 0f)
    {
        _ = gl;
        if (_terrainGpuChunks.Count == 0)
        {
            return;
        }

        EnsureTerrainDrawCandidates();
        if (_terrainDrawCandidates.Count == 0)
        {
            return;
        }

        TerrainChunkDrawCull.ApplyNearPomFlags(
            _terrainDrawCandidates,
            cameraPosition,
            enableParallaxSetting);

        TerrainChunkDrawCull.Select(
            _terrainDrawCandidates,
            viewProjection,
            cameraPosition,
            PreviewStageConstants.TerrainFrustumDrawFallbackCount,
            fullOnly: shadowFullOnly,
            _terrainDrawSelected,
            maxCasterDistanceXz: maxCasterDistanceXz);

        // Terrain always draws when frustum-visible. Hi-Z is built FROM terrain+subject so hills
        // occlude shaded subject batches; filtering terrain against that same pyramid false-culled
        // the ground (and vanished entirely when the pyramid had no subject depth).

        DrawTerrainCandidates(
            _terrainDrawCandidates,
            _terrainDrawChunkScratch,
            _terrainDrawSelected,
            patches,
            enableParallaxSetting,
            setParallaxEnabled,
            shadowPass: false,
            cameraPosition);
    }

    /// <summary>
    /// Builds the shared residency candidate list once per GPU-chunk set.
    /// NearPom stays false here; callers apply it for shaded select/draw.
    /// </summary>
    private void EnsureTerrainDrawCandidates()
    {
        if (_terrainCandidatesBuiltVersion == _terrainCandidatesChunkVersion)
        {
            return;
        }

        _terrainDrawChunkScratch.Clear();
        _terrainDrawCandidates.Clear();
        foreach (var (key, chunk) in _terrainGpuChunks)
        {
            if (chunk.IndexCount <= 0)
            {
                continue;
            }

            var sourceIndex = _terrainDrawChunkScratch.Count;
            _terrainDrawChunkScratch.Add(chunk);
            _terrainDrawCandidates.Add(new TerrainChunkDrawCull.Candidate
            {
                Key = key,
                BoundsCenter = chunk.BoundsCenter,
                BoundsRadius = chunk.BoundsRadius,
                Lod = chunk.Lod,
                NearPom = false,
                SourceIndex = sourceIndex
            });
        }

        _terrainCandidatesBuiltVersion = _terrainCandidatesChunkVersion;
    }

    private void PrepareTerrainShadowCasterSelections(
        Vector3 cameraPosition,
        Matrix4x4 nearVp,
        Matrix4x4 midVp,
        Matrix4x4 farVp,
        float nearCasterDistanceXz,
        float midCasterDistanceXz,
        float farCasterDistanceXz,
        bool cascadesActive,
        float inclusionPad)
    {
        _terrainShadowSelectedNear.Clear();
        _terrainShadowSelectedMid.Clear();
        _terrainShadowSelectedFar.Clear();
        _terrainShadowGpuIndirectReady = false;
        EnsureTerrainDrawCandidates();
        if (_terrainDrawCandidates.Count == 0)
        {
            return;
        }

        if (TryPrepareTerrainShadowCasterSelectionsGpu(
                cameraPosition,
                nearVp,
                midVp,
                farVp,
                nearCasterDistanceXz,
                midCasterDistanceXz,
                farCasterDistanceXz,
                cascadesActive,
                inclusionPad))
        {
            _terrainShadowGpuIndirectReady = true;
            return;
        }

        PrepareTerrainShadowCasterSelectionsCpu(
            cameraPosition,
            nearVp,
            midVp,
            farVp,
            nearCasterDistanceXz,
            midCasterDistanceXz,
            farCasterDistanceXz,
            cascadesActive);
    }

    private void PrepareTerrainShadowCasterSelectionsCpu(
        Vector3 cameraPosition,
        Matrix4x4 nearVp,
        Matrix4x4 midVp,
        Matrix4x4 farVp,
        float nearCasterDistanceXz,
        float midCasterDistanceXz,
        float farCasterDistanceXz,
        bool cascadesActive)
    {
        var fallback = PreviewStageConstants.TerrainFrustumDrawFallbackCount;
        if (!cascadesActive)
        {
            TerrainChunkDrawCull.Select(
                _terrainDrawCandidates,
                farVp,
                cameraPosition,
                fallback,
                fullOnly: false,
                _terrainShadowSelectedFar,
                maxCasterDistanceXz: farCasterDistanceXz);
            return;
        }

        if (_terrainDrawCandidates.Count >= TerrainShadowParallelCullMinCandidates)
        {
            Parallel.Invoke(
                TerrainShadowParallelOptions,
                () => TerrainChunkDrawCull.Select(
                    _terrainDrawCandidates,
                    nearVp,
                    cameraPosition,
                    fallback,
                    fullOnly: true,
                    _terrainShadowSelectedNear,
                    maxCasterDistanceXz: nearCasterDistanceXz,
                    allowParallel: false),
                () => TerrainChunkDrawCull.Select(
                    _terrainDrawCandidates,
                    midVp,
                    cameraPosition,
                    fallback,
                    fullOnly: false,
                    _terrainShadowSelectedMid,
                    maxCasterDistanceXz: midCasterDistanceXz,
                    allowParallel: false),
                () => TerrainChunkDrawCull.Select(
                    _terrainDrawCandidates,
                    farVp,
                    cameraPosition,
                    fallback,
                    fullOnly: false,
                    _terrainShadowSelectedFar,
                    maxCasterDistanceXz: farCasterDistanceXz,
                    allowParallel: false));
            return;
        }

        TerrainChunkDrawCull.Select(
            _terrainDrawCandidates,
            nearVp,
            cameraPosition,
            fallback,
            fullOnly: true,
            _terrainShadowSelectedNear,
            maxCasterDistanceXz: nearCasterDistanceXz);
        TerrainChunkDrawCull.Select(
            _terrainDrawCandidates,
            midVp,
            cameraPosition,
            fallback,
            fullOnly: false,
            _terrainShadowSelectedMid,
            maxCasterDistanceXz: midCasterDistanceXz);
        TerrainChunkDrawCull.Select(
            _terrainDrawCandidates,
            farVp,
            cameraPosition,
            fallback,
            fullOnly: false,
            _terrainShadowSelectedFar,
            maxCasterDistanceXz: farCasterDistanceXz);
    }

    private bool TryPrepareTerrainShadowCasterSelectionsGpu(
        Vector3 cameraPosition,
        Matrix4x4 nearVp,
        Matrix4x4 midVp,
        Matrix4x4 farVp,
        float nearCasterDistanceXz,
        float midCasterDistanceXz,
        float farCasterDistanceXz,
        bool cascadesActive,
        float inclusionPad)
    {
        if (_terrainShadowCullCompileDisabled ||
            _glCapabilities?.CanUseGpuTerrainShadowCull != true ||
            _gl is null ||
            !TryEnsureTerrainShadowCuller())
        {
            return false;
        }

        // Cutout foliage (leaves / cactus side) must use CPU Select so
        // BindGroundSlotForDraw can set uEntityAlphaMode and bind the per-slot albedo.
        // The GPU MultiDrawIndirectCount + draw-record cutout path still wrote opaque depth
        // for leaf cubes (solid block shadows), so keep CPU Select while vegetation cutouts exist.
        if (TerrainShadowRequiresCutoutSupport())
        {
            if (!_loggedTerrainShadowCutoutCpuFallback)
            {
                _loggedTerrainShadowCutoutCpuFallback = true;
                EmitDiagnostic(
                    "[3D preview] Terrain shadow cutout: CPU Select (per-material albedo discard) " +
                    "for vegetation leaf/cactus slots.");
            }

            return false;
        }

        EnsureTerrainMeshPool(_gl);
        if (_terrainMultiDrawIndirectCount is null)
        {
            return false;
        }

        var sourceCount = CountTerrainShadowGpuSourceCommands();
        if (sourceCount <= 0)
        {
            return false;
        }

        if (_terrainShadowCullRecordScratch.Length < sourceCount)
        {
            _terrainShadowCullRecordScratch = new TerrainShadowCullRecord[sourceCount];
        }

        var dwordCount = sourceCount * GlIndirectDrawCommandBuffer.CommandDwords;
        if (_terrainShadowSourceCommandScratch.Length < dwordCount)
        {
            _terrainShadowSourceCommandScratch = new uint[Math.Max(dwordCount, 256)];
        }

        var records = _terrainShadowCullRecordScratch.AsSpan(0, sourceCount);
        var written = FillTerrainShadowGpuSourceCommands(records, _terrainShadowSourceCommandScratch);
        if (written != sourceCount)
        {
            return false;
        }

        _terrainShadowSourceCommands ??= new GlIndirectDrawCommandBuffer(_gl);
        if (!_terrainShadowSourceCommands.UploadCommands(_terrainShadowSourceCommandScratch, sourceCount))
        {
            return false;
        }

        Span<Vector4> nearPlanes = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];
        Span<Vector4> midPlanes = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];
        Span<Vector4> farPlanes = stackalloc Vector4[PreviewFrustumPlanes.PlaneCount];
        PreviewFrustumPlanes.Extract(nearVp, nearPlanes);
        PreviewFrustumPlanes.Extract(midVp, midPlanes);
        PreviewFrustumPlanes.Extract(farVp, farPlanes);

        if (!_terrainShadowCuller!.Dispatch(
                _terrainShadowCullProgram!,
                _terrainShadowSourceCommands,
                records,
                nearPlanes,
                midPlanes,
                farPlanes,
                cameraPosition,
                nearCasterDistanceXz,
                midCasterDistanceXz,
                farCasterDistanceXz,
                inclusionPad,
                cascadesActive))
        {
            return false;
        }

        if (!_loggedTerrainShadowGpuCull)
        {
            _loggedTerrainShadowGpuCull = true;
            EmitDiagnostic(
                $"[3D preview] GPU terrain shadow cull enabled (no readback): sources={sourceCount} " +
                $"(candidates={_terrainDrawCandidates.Count}), MultiDrawIndirectCount per cascade.");
        }

        return true;
    }

    private bool TerrainShadowRequiresCutoutSupport()
    {
        // Only vegetation leaf/cactus cutouts force the CPU shadow path. Grass overlay is also
        // marked cutout, but keeping GPU cull for overlay-only scenes matters more than punching
        // thin side-overlay holes; leaf cube shadows are the user-visible failure.
        for (var i = PreviewTerrainGrassSlots.VegetationBase; i < _grassGroundSlots.Length; i++)
        {
            if (_grassGroundSlots[i].Cutout)
            {
                return true;
            }
        }

        return false;
    }

    private int CountTerrainShadowGpuSourceCommands()
    {
        var count = 0;
        for (var i = 0; i < _terrainDrawCandidates.Count; i++)
        {
            var chunk = _terrainDrawChunkScratch[_terrainDrawCandidates[i].SourceIndex];
            var batches = chunk.DrawBatches;
            if (batches.Length == 0)
            {
                if (chunk.IndexCount > 0)
                {
                    count++;
                }

                continue;
            }

            for (var b = 0; b < batches.Length; b++)
            {
                if (batches[b].IndexCount > 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private int FillTerrainShadowGpuSourceCommands(
        Span<TerrainShadowCullRecord> records,
        uint[] commandScratch)
    {
        var sourceIndex = 0;
        for (var i = 0; i < _terrainDrawCandidates.Count; i++)
        {
            var c = _terrainDrawCandidates[i];
            var chunk = _terrainDrawChunkScratch[c.SourceIndex];
            var batches = chunk.DrawBatches;
            var isFullLod = c.Lod == TerrainChunkLodKind.Full;
            if (batches.Length == 0)
            {
                if (chunk.IndexCount <= 0)
                {
                    continue;
                }

                if ((uint)sourceIndex >= (uint)records.Length)
                {
                    return -1;
                }

                records[sourceIndex] = new TerrainShadowCullRecord(
                    c.BoundsCenter,
                    c.BoundsRadius,
                    isFullLod,
                    candidateIndex: sourceIndex);
                GlIndirectDrawCommandBuffer.WriteCommandDwords(
                    commandScratch.AsSpan(
                        sourceIndex * GlIndirectDrawCommandBuffer.CommandDwords,
                        GlIndirectDrawCommandBuffer.CommandDwords),
                    (uint)Math.Max(0, chunk.IndexCount),
                    (uint)Math.Max(0, chunk.Allocation.IndexOffset),
                    checked((int)chunk.Allocation.BaseVertex),
                    baseInstance: 0u);
                sourceIndex++;
                continue;
            }

            for (var b = 0; b < batches.Length; b++)
            {
                var batch = batches[b];
                if (batch.IndexCount <= 0)
                {
                    continue;
                }

                if ((uint)sourceIndex >= (uint)records.Length)
                {
                    return -1;
                }

                var materialIndex = batch.MaterialIndex;
                if ((uint)materialIndex >= (uint)_grassGroundSlots.Length)
                {
                    materialIndex = 0;
                }

                records[sourceIndex] = new TerrainShadowCullRecord(
                    c.BoundsCenter,
                    c.BoundsRadius,
                    isFullLod,
                    candidateIndex: sourceIndex);
                GlIndirectDrawCommandBuffer.WriteCommandDwords(
                    commandScratch.AsSpan(
                        sourceIndex * GlIndirectDrawCommandBuffer.CommandDwords,
                        GlIndirectDrawCommandBuffer.CommandDwords),
                    (uint)batch.IndexCount,
                    (uint)Math.Max(0, batch.FirstIndex),
                    checked((int)chunk.Allocation.BaseVertex),
                    baseInstance: (uint)materialIndex);
                sourceIndex++;
            }
        }

        return sourceIndex;
    }

    private bool TryEnsureTerrainShadowCuller()
    {
        if (_terrainShadowCullProgram is { IsValid: true } && _terrainShadowCuller is not null)
        {
            return true;
        }

        if (_gl is null || _shaderCtx is null || _terrainShadowCullCompileDisabled)
        {
            return false;
        }

        _terrainShadowCullProgram = CreatePreviewComputeProgram(
            "genesis_terrain_shadow_cull.comp",
            out var error,
            "genesis-terrain-shadow-cull");
        if (!_terrainShadowCullProgram.IsValid)
        {
            _terrainShadowCullProgram.Dispose();
            _terrainShadowCullProgram = null;
            _terrainShadowCullCompileDisabled = true;
            EmitDiagnostic(
                $"[3D preview] GPU terrain shadow cull unavailable; retaining CPU Select. {error}");
            return false;
        }

        _terrainShadowCuller = new GlTerrainShadowCuller(_gl);
        return true;
    }

    private void DisposeTerrainShadowCuller()
    {
        _terrainShadowGpuIndirectReady = false;
        _terrainShadowCuller?.Dispose();
        _terrainShadowCuller = null;
        _terrainShadowCullProgram?.Dispose();
        _terrainShadowCullProgram = null;
        _terrainShadowSourceCommands?.Dispose();
        _terrainShadowSourceCommands = null;
    }

    private void DrawPreparedTerrainShadowCasters(List<int> selected)
    {
        if (_terrainShadowGpuIndirectReady && TryDrawTerrainShadowCastersGpu(selected))
        {
            return;
        }

        DrawTerrainCandidates(
            _terrainDrawCandidates,
            _terrainDrawChunkScratch,
            selected,
            patches: false,
            enableParallaxSetting: false,
            setParallaxEnabled: static _ => { },
            shadowPass: true,
            cameraPosition: default);
    }

    private bool TryDrawTerrainShadowCastersGpu(List<int> selected)
    {
        var pool = _terrainMeshPool;
        var culler = _terrainShadowCuller;
        if (pool is not { IsValid: true } ||
            culler is null ||
            _terrainMultiDrawIndirectCount is null ||
            culler.MaxDrawCount <= 0 ||
            culler.CounterBufferHandle == 0)
        {
            return false;
        }

        // Opaque-only GPU path: cutout slots force CPU Select in Prepare.
        if (TerrainShadowRequiresCutoutSupport())
        {
            return false;
        }

        GlIndirectDrawCommandBuffer commands;
        nint countOffset;
        if (ReferenceEquals(selected, _terrainShadowSelectedNear))
        {
            commands = culler.NearCommands;
            countOffset = 0;
        }
        else if (ReferenceEquals(selected, _terrainShadowSelectedMid))
        {
            commands = culler.MidCommands;
            countOffset = sizeof(uint);
        }
        else
        {
            commands = culler.FarCommands;
            countOffset = 2 * sizeof(uint);
        }

        if (!commands.IsValid)
        {
            return false;
        }

        pool.BindVertexArray();
        var drawn = pool.MultiDrawIndirectCount(
            commands,
            culler.CounterBufferHandle,
            culler.MaxDrawCount,
            _terrainMultiDrawIndirectCount,
            patches: false,
            keepBound: true,
            drawCountOffset: countOffset);
        pool.UnbindVertexArray();

        return drawn;
    }

    private void DrawTerrainCandidates(
        List<TerrainChunkDrawCull.Candidate> candidates,
        List<TerrainGpuChunk> scratch,
        List<int> selected,
        bool patches,
        bool enableParallaxSetting,
        Action<bool> setParallaxEnabled,
        bool shadowPass,
        Vector3 cameraPosition,
        bool opaqueOnly = false)
    {
        _ = cameraPosition;
        if (_terrainMeshPool is not { IsValid: true } || selected.Count == 0)
        {
            return;
        }

        BuildTerrainDrawItems(
            candidates,
            scratch,
            selected,
            enableParallaxSetting && !shadowPass,
            opaqueOnly);
        if (_terrainDrawItems.Count == 0)
        {
            return;
        }

        _terrainDrawItems.Sort(static (a, b) =>
        {
            var ca = new TerrainChunkDrawCull.Candidate
            {
                BoundsCenter = default,
                BoundsRadius = 0f,
                Lod = a.Lod,
                NearPom = a.NearPom,
                SourceIndex = 0
            };
            var cb = new TerrainChunkDrawCull.Candidate
            {
                BoundsCenter = default,
                BoundsRadius = 0f,
                Lod = b.Lod,
                NearPom = b.NearPom,
                SourceIndex = 0
            };
            return TerrainChunkDrawCull.CompareDrawItems(
                ca, a.MaterialIndex, a.Cutout,
                cb, b.MaterialIndex, b.Cutout,
                a.SourceOrder, b.SourceOrder);
        });

        var useArrays = !shadowPass &&
                        _groundTextureArraysReady &&
                        _activeGenesisProgramKey.MaterialTextureArrays;
        if (useArrays)
        {
            BindGroundMaterialTextureArrays();
        }

        // Shared VAO pool + MultiDrawIndirect for shaded and shadow/depth (CPU Select already culled).
        if (_glCapabilities?.CanUseIndirectDrawCommands == true &&
            TryDrawTerrainMultiDrawIndirect(
                patches,
                enableParallaxSetting,
                setParallaxEnabled,
                useArrays,
                shadowPass,
                opaqueOnly))
        {
            if (!shadowPass && enableParallaxSetting)
            {
                setParallaxEnabled(enableParallaxSetting);
            }

            if (_grassGroundSlots.Length > 1)
            {
                SetGroundAlphaMode(cutout: false, shadowPass);
            }

            return;
        }

        DrawTerrainItemsSequential(
            patches,
            enableParallaxSetting,
            setParallaxEnabled,
            shadowPass,
            useArrays,
            opaqueOnly);
    }

    private void BuildTerrainDrawItems(
        List<TerrainChunkDrawCull.Candidate> candidates,
        List<TerrainGpuChunk> scratch,
        List<int> selected,
        bool applyNearPom,
        bool opaqueOnly = false)
    {
        _terrainDrawItems.Clear();
        var multiSlot = _grassGroundSlots.Length > 1;
        for (var order = 0; order < selected.Count; order++)
        {
            var candidate = candidates[selected[order]];
            var chunk = scratch[candidate.SourceIndex];
            var nearPom = applyNearPom && candidate.NearPom;
            var hasUnderlay = HasCoarserGpuUnderlayForFade(candidate.Key);
            var transitionKeep = ResolveTerrainTransitionKeep(
                candidate.Key,
                chunk,
                hasUnderlay);
            var batches = chunk.DrawBatches;
            if (batches.Length == 0)
            {
                if (chunk.IndexCount <= 0)
                {
                    continue;
                }

                _terrainDrawItems.Add(new TerrainDrawItem
                {
                    FirstIndex = chunk.Allocation.IndexOffset,
                    IndexCount = chunk.IndexCount,
                    BaseVertex = checked((int)chunk.Allocation.BaseVertex),
                    MaterialIndex = 0,
                    Cutout = false,
                    NearPom = nearPom,
                    HasCoarserUnderlay = hasUnderlay,
                    TransitionKeep = transitionKeep,
                    Lod = chunk.Lod,
                    SourceOrder = order,
                });
                continue;
            }

            foreach (var batch in batches)
            {
                if (batch.IndexCount <= 0)
                {
                    continue;
                }

                var materialIndex = batch.MaterialIndex;
                if ((uint)materialIndex >= (uint)_grassGroundSlots.Length)
                {
                    materialIndex = 0;
                }

                var cutout = multiSlot && _grassGroundSlots[materialIndex].Cutout;
                if (opaqueOnly && cutout)
                {
                    continue;
                }

                _terrainDrawItems.Add(new TerrainDrawItem
                {
                    FirstIndex = batch.FirstIndex,
                    IndexCount = batch.IndexCount,
                    BaseVertex = checked((int)chunk.Allocation.BaseVertex),
                    MaterialIndex = materialIndex,
                    Cutout = cutout,
                    NearPom = nearPom && !cutout,
                    HasCoarserUnderlay = hasUnderlay,
                    TransitionKeep = transitionKeep,
                    Lod = chunk.Lod,
                    SourceOrder = order,
                });
            }
        }
    }

    private void DrawTerrainItemsSequential(
        bool patches,
        bool enableParallaxSetting,
        Action<bool> setParallaxEnabled,
        bool shadowPass,
        bool useArrays,
        bool opaqueOnly)
    {
        var pool = _terrainMeshPool;
        if (pool is null)
        {
            return;
        }

        pool.BindVertexArray();
        if (patches && _gl is not null)
        {
            _gl.PatchParameter(PatchParameterName.Vertices, 3);
        }

        var lastPom = !enableParallaxSetting;
        var lastMaterial = int.MinValue;
        var lastCutout = false;
        var lastLod = (TerrainChunkLodKind)byte.MaxValue;
        var lastUnderlay = false;
        var lastTransitionKeep = -1f;
        // Opaque depth/shadow prepass: no alpha discard → skip albedo binds / material splits.
        var skipMaterialBinds = opaqueOnly && shadowPass;
        foreach (var item in _terrainDrawItems)
        {
            var pom = item.NearPom && enableParallaxSetting && !item.Cutout;
            if (!shadowPass && pom != lastPom)
            {
                setParallaxEnabled(pom);
                lastPom = pom;
            }

            if (!shadowPass &&
                (item.Lod != lastLod || item.HasCoarserUnderlay != lastUnderlay))
            {
                SetTerrainLodDetailFade(item.Lod, item.HasCoarserUnderlay);
                lastLod = item.Lod;
                lastUnderlay = item.HasCoarserUnderlay;
            }

            if (!shadowPass && item.TransitionKeep != lastTransitionKeep)
            {
                SetTerrainTransitionKeep(item.TransitionKeep);
                lastTransitionKeep = item.TransitionKeep;
            }

            if (!skipMaterialBinds && !useArrays && item.MaterialIndex != lastMaterial)
            {
                BindGroundSlotForDraw(item.MaterialIndex, shadowPass);
                lastMaterial = item.MaterialIndex;
                lastCutout = item.Cutout;
                if (!shadowPass && !item.Cutout && enableParallaxSetting)
                {
                    setParallaxEnabled(lastPom);
                }
            }
            else if (!skipMaterialBinds && useArrays && item.Cutout != lastCutout)
            {
                SetGroundAlphaMode(item.Cutout, shadowPass);
                if (!shadowPass && item.Cutout)
                {
                    setParallaxEnabled(false);
                    lastPom = false;
                }
                else if (!shadowPass && enableParallaxSetting)
                {
                    setParallaxEnabled(lastPom);
                }

                lastCutout = item.Cutout;
            }

            if (useArrays && !skipMaterialBinds)
            {
                SetGroundDrawRecordIndex(item.MaterialIndex);
            }

            pool.DrawRange(
                item.FirstIndex,
                item.IndexCount,
                patches,
                keepBound: true,
                updatePatchParameter: false,
                baseInstance: useArrays ? (uint)Math.Max(0, item.MaterialIndex) : 0u,
                baseVertex: item.BaseVertex);
        }

        pool.UnbindVertexArray();
        if (!shadowPass)
        {
            SetTerrainLodDetailFade(TerrainChunkLodKind.Full, hasCoarserUnderlay: false);
            SetTerrainTransitionKeep(1f);
        }

        if (!shadowPass && lastPom != enableParallaxSetting)
        {
            setParallaxEnabled(enableParallaxSetting);
        }

        if (_grassGroundSlots.Length > 1)
        {
            SetGroundAlphaMode(cutout: false, shadowPass);
        }
    }

    private void SetTerrainLodDetailFade(
        TerrainChunkLodKind lod,
        bool hasCoarserUnderlay)
    {
        var u = _mainUniformLocs;
        if (_terrainStreamer is null || u.TerrainLodFadeEnable < 0)
        {
            SetIntLoc(u.TerrainLodFadeEnable, 0);
            return;
        }

        var hard = _terrainStreamer.HardRadiusChunks;
        var ring = _terrainStreamer.LodRingChunks;
        var level = (byte)lod;
        // Outermost / sole coverage stays fully opaque — dither only where coarser underlay exists.
        if (ring <= 0 ||
            TerrainChunkStreamer.IsOutermostLodLevel(hard, ring, level) ||
            (level == 0 && TerrainChunkStreamer.ResolveActiveLodLevelCount(ring) == 0) ||
            !hasCoarserUnderlay)
        {
            SetIntLoc(u.TerrainLodFadeEnable, 0);
            return;
        }

        TerrainChunkStreamer.ResolveLodDetailFadeMeters(
            hard,
            ring,
            level,
            out var fadeStart,
            out var fadeEnd);
        SetIntLoc(u.TerrainLodFadeEnable, 1);
        SetFloatLoc(u.TerrainLodFadeStart, fadeStart);
        SetFloatLoc(u.TerrainLodFadeEnd, fadeEnd);
    }

    /// <summary>
    /// Transaction-local underlay proof: a coarser ancestor is still drawable only while it is
    /// outgoing/retiring (or superseded by the current target cut). Stable dual residency is not
    /// enough — that was the old global HasCoarserGpuUnderlayForFade path.
    /// </summary>
    private bool HasCoarserGpuUnderlayForFade(TerrainResidencyKey finer)
    {
        var desired = _terrainStreamer?.SnapshotDesired();
        var chunk = new TerrainChunkKey(finer.OriginChunkX, finer.OriginChunkZ);
        for (var level = finer.LodLevel + 1; level <= TerrainResidencyKey.MaxLodLevel; level++)
        {
            var ancestor = TerrainResidencyKey.FromChunk(chunk, (byte)level);
            if (!_terrainGpuChunks.ContainsKey(ancestor))
            {
                continue;
            }

            if (_terrainRetireAfterSeconds.ContainsKey(ancestor))
            {
                return true;
            }

            if (desired is not null && !desired.ContainsKey(ancestor))
            {
                return true;
            }

            var node = _terrainCoverageGraph?.GetNode(ancestor);
            if (node is
                {
                    IsDrawable: true,
                    State: TerrainCoverageNodeState.TransitionOutgoing
                })
            {
                return true;
            }
        }

        return false;
    }

    private float ResolveTerrainTransitionKeep(
        TerrainResidencyKey key,
        TerrainGpuChunk chunk,
        bool hasCoarserUnderlay)
    {
        double keep;
        if (_terrainRetireAfterSeconds.TryGetValue(key, out var retireAfter))
        {
            keep = (retireAfter - _renderTimeAccum) / TerrainTransitionSeconds;
        }
        else if (hasCoarserUnderlay)
        {
            keep = (_renderTimeAccum - chunk.ActivatedAtSeconds) / TerrainTransitionSeconds;
        }
        else
        {
            keep = 1.0;
        }

        // Stable buckets preserve MDI grouping while retaining a smooth temporal handoff.
        var clamped = Math.Clamp((float)keep, 0f, 1f);
        return MathF.Round(clamped * 15f) / 15f;
    }

    private void SetTerrainTransitionKeep(float keep) =>
        SetFloatLoc(_mainUniformLocs.TerrainTransitionKeep, Math.Clamp(keep, 0f, 1f));

    private bool TryDrawTerrainMultiDrawIndirect(
        bool patches,
        bool enableParallaxSetting,
        Action<bool> setParallaxEnabled,
        bool useArrays,
        bool shadowPass,
        bool opaqueOnly)
    {
        var pool = _terrainMeshPool;
        var commands = _terrainIndirectCommands;
        if (pool is null || commands is null || _terrainDrawItems.Count == 0)
        {
            return false;
        }

        var itemCount = _terrainDrawItems.Count;
        var dwordCount = itemCount * GlIndirectDrawCommandBuffer.CommandDwords;
        if (_terrainIndirectScratch.Length < dwordCount)
        {
            _terrainIndirectScratch = new uint[Math.Max(dwordCount, 256)];
        }

        for (var i = 0; i < itemCount; i++)
        {
            var item = _terrainDrawItems[i];
            GlIndirectDrawCommandBuffer.WriteCommandDwords(
                _terrainIndirectScratch.AsSpan(i * GlIndirectDrawCommandBuffer.CommandDwords, GlIndirectDrawCommandBuffer.CommandDwords),
                (uint)item.IndexCount,
                (uint)item.FirstIndex,
                item.BaseVertex,
                (uint)item.MaterialIndex);
        }

        if (!commands.UploadCommands(_terrainIndirectScratch, itemCount))
        {
            return false;
        }

        pool.BindVertexArray();
        if (patches && _gl is not null)
        {
            _gl.PatchParameter(PatchParameterName.Vertices, 3);
        }

        // Opaque depth prepass: one MultiDraw for the whole list (no cutout/material state breaks).
        var skipMaterialBinds = opaqueOnly && shadowPass;
        if (skipMaterialBinds)
        {
            if (itemCount >= 2)
            {
                pool.MultiDrawIndirect(
                    commands,
                    0,
                    itemCount,
                    patches,
                    keepBound: true,
                    updatePatchParameter: false);
            }
            else
            {
                var item = _terrainDrawItems[0];
                pool.DrawRange(
                    item.FirstIndex,
                    item.IndexCount,
                    patches,
                    keepBound: true,
                    updatePatchParameter: false,
                    baseVertex: item.BaseVertex);
            }

            pool.UnbindVertexArray();
            if (!_loggedTerrainMultiDraw)
            {
                _loggedTerrainMultiDraw = true;
                EmitDiagnostic(
                    $"[3D preview] Terrain MultiDrawIndirect enabled: items={itemCount}, " +
                    $"arrays={(useArrays ? "on" : "off")}, shadow={(shadowPass ? "yes" : "no")}, " +
                    $"{FormatTerrainResidencyDiagBreakdown()}, " +
                    $"cameraChunkResident={HasTerrainStreamerCameraChunk()}, safetyUnderlay=startup-only.");
            }

            return true;
        }

        var groupStart = 0;
        while (groupStart < itemCount)
        {
            var head = _terrainDrawItems[groupStart];
            var groupEnd = groupStart + 1;
            while (groupEnd < itemCount)
            {
                var next = _terrainDrawItems[groupEnd];
                var samePom = next.NearPom == head.NearPom;
                var sameCutout = next.Cutout == head.Cutout;
                var sameLod = next.Lod == head.Lod;
                var sameUnderlay = next.HasCoarserUnderlay == head.HasCoarserUnderlay;
                var sameTransition = next.TransitionKeep == head.TransitionKeep;
                // With texture arrays + draw-parameter baseInstance, materials share one bind group.
                var sameMaterial = (!shadowPass && useArrays && _activeGenesisProgramKey.DrawRecordBaseInstance) ||
                                   next.MaterialIndex == head.MaterialIndex;
                if (!samePom ||
                    !sameCutout ||
                    !sameMaterial ||
                    !sameLod ||
                    !sameUnderlay ||
                    !sameTransition)
                {
                    break;
                }

                groupEnd++;
            }

            var pom = !shadowPass && head.NearPom && enableParallaxSetting && !head.Cutout;
            if (!shadowPass)
            {
                setParallaxEnabled(pom);
                SetTerrainLodDetailFade(head.Lod, head.HasCoarserUnderlay);
                SetTerrainTransitionKeep(head.TransitionKeep);
            }

            if (useArrays && !shadowPass)
            {
                SetGroundAlphaMode(head.Cutout, shadowPass: false);
            }
            else
            {
                BindGroundSlotForDraw(head.MaterialIndex, shadowPass);
                if (!shadowPass && !head.Cutout && enableParallaxSetting)
                {
                    setParallaxEnabled(pom);
                }
            }

            var count = groupEnd - groupStart;
            if (count >= 2)
            {
                pool.MultiDrawIndirect(
                    commands,
                    groupStart,
                    count,
                    patches,
                    keepBound: true,
                    updatePatchParameter: false);
            }
            else
            {
                for (var i = groupStart; i < groupEnd; i++)
                {
                    var item = _terrainDrawItems[i];
                    if (useArrays && !shadowPass)
                    {
                        SetGroundDrawRecordIndex(item.MaterialIndex);
                    }

                    pool.DrawRange(
                        item.FirstIndex,
                        item.IndexCount,
                        patches,
                        keepBound: true,
                        updatePatchParameter: false,
                        baseInstance: useArrays && !shadowPass
                            ? (uint)Math.Max(0, item.MaterialIndex)
                            : 0u,
                        baseVertex: item.BaseVertex);
                }
            }

            groupStart = groupEnd;
        }

        pool.UnbindVertexArray();
        if (!shadowPass)
        {
            SetTerrainLodDetailFade(TerrainChunkLodKind.Full, hasCoarserUnderlay: false);
            SetTerrainTransitionKeep(1f);
        }

        if (!_loggedTerrainMultiDraw)
        {
            _loggedTerrainMultiDraw = true;
            EmitDiagnostic(
                $"[3D preview] Terrain MultiDrawIndirect enabled: items={itemCount}, " +
                $"arrays={(useArrays ? "on" : "off")}, shadow={(shadowPass ? "yes" : "no")}, " +
                $"{FormatTerrainResidencyDiagBreakdown()}, " +
                $"cameraChunkResident={HasTerrainStreamerCameraChunk()}, safetyUnderlay=startup-only.");
        }

        return true;
    }

    private void SetGroundDrawRecordIndex(int materialIndex)
    {
        if (_program is null)
        {
            return;
        }

        SetIntLoc(_mainUniformLocs.GenesisDrawRecordIndex, materialIndex);
    }

    private void BindGroundSlotForDraw(int materialIndex, bool shadowPass)
    {
        if ((uint)materialIndex >= (uint)_grassGroundSlots.Length)
        {
            materialIndex = 0;
        }

        var slot = _grassGroundSlots[materialIndex];
        if (shadowPass)
        {
            if (_shadowProgram is not null && slot.Albedo is not null)
            {
                var su = _shadowUniformLocs;
                slot.Albedo.Bind(0);
                SetIntOnProgramLoc(_shadowProgram, su.Albedo, 0);
                SetGroundAlphaMode(slot.Cutout, shadowPass: true);
            }

            return;
        }

        if (_program is null)
        {
            return;
        }

        var u = _mainUniformLocs;
        BindGroundGpuSlot(slot);
        SetIntLoc(u.Albedo, 0);
        SetIntLoc(u.Normal, 1);
        SetIntLoc(u.Specular, 2);
        SetIntLoc(u.Height, 3);
        SetIntLoc(u.HasNormal, slot.HasNormal ? 1 : 0);
        SetIntLoc(u.HasSpecular, slot.HasSpecular ? 1 : 0);
        // Cutout leaves/cactus: never run POM — height noise destroys foliage silhouettes.
        var hasHeight = slot is { HasHeight: true, Cutout: false };
        SetIntLoc(u.HasHeight, hasHeight ? 1 : 0);
        if (slot.Cutout)
        {
            SetIntLoc(u.EnableParallax, 0);
            SetIntLoc(u.EnableParallaxAo, 0);
            SetIntLoc(u.EnableParallaxShadow, 0);
        }

        SetVec2Loc(u.ParallaxHeightTexSize, new Vector2(slot.Width, slot.TexHeight));
        SetGroundAlphaMode(slot.Cutout, shadowPass: false);
    }

    private void SetGroundAlphaMode(bool cutout, bool shadowPass)
    {
        if (shadowPass)
        {
            if (_shadowProgram is null)
            {
                return;
            }

            var su = _shadowUniformLocs;
            SetIntOnProgramLoc(_shadowProgram, su.EntityAlphaMode, cutout ? (int)PreviewEntityAlphaMode.Cutout : 0);
            return;
        }

        if (_program is null)
        {
            return;
        }

        SetIntLoc(_mainUniformLocs.EntityAlphaMode, cutout ? (int)PreviewEntityAlphaMode.Cutout : 0);
    }

    private void TryEnsureGroundTextureArrays(GL gl)
    {
        _groundTextureArraysReady = false;
        if (_grassGroundSlotMaterials is not { Length: > 0 } slots ||
            _glCapabilities?.CanUseMaterialTextureArrays != true ||
            !_genesisMaterialDrawRecordsUseSsbo ||
            _materialTextureArraysCompileDisabled)
        {
            return;
        }

        var maxLayers = Math.Max(1, gl.GetInteger(GetPName.MaxArrayTextureLayers));
        if (!GenesisMaterialTextureArrayPlan.TryCreate(slots, maxLayers, out var plan, out _))
        {
            return;
        }

        var resolved = plan!;
        if (_groundTextureArrayPlan is not null &&
            resolved.ContentEquals(_groundTextureArrayPlan) &&
            _groundAlbedoArray is not null &&
            ReferenceEquals(_groundArraySlotMaterialsFingerprint, slots))
        {
            _groundTextureArraysReady = UploadGroundMaterialDrawRecords(slots);
            return;
        }

        _groundAlbedoArray ??= new GlTexture2DArray(gl);
        _groundNormalArray ??= new GlTexture2DArray(gl);
        _groundSpecArray ??= new GlTexture2DArray(gl);
        _groundHeightArray ??= new GlTexture2DArray(gl);
        var layerBytes = resolved.Width * resolved.Height * 4;
        var totalBytes = layerBytes * resolved.Layers;
        EnsureMaterialTextureArrayScratch(totalBytes);
        var scratch = _materialTextureArrayScratch;
        if (scratch is null)
        {
            return;
        }

        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        try
        {
            FillMaterialArrayScratch(slots, resolved, MaterialArrayMapKind.Albedo, scratch);
            _groundAlbedoArray.UploadRgbaIfChanged(resolved.Width, resolved.Height, resolved.Layers, scratch.AsSpan(0, totalBytes), nearest: true);
            FillMaterialArrayScratch(slots, resolved, MaterialArrayMapKind.Normal, scratch);
            _groundNormalArray.UploadRgbaIfChanged(resolved.Width, resolved.Height, resolved.Layers, scratch.AsSpan(0, totalBytes), nearest: true);
            FillMaterialArrayScratch(slots, resolved, MaterialArrayMapKind.Specular, scratch);
            _groundSpecArray.UploadRgbaIfChanged(resolved.Width, resolved.Height, resolved.Layers, scratch.AsSpan(0, totalBytes), nearest: true);
            FillMaterialArrayScratch(slots, resolved, MaterialArrayMapKind.Height, scratch);
            _groundHeightArray.UploadRgbaIfChanged(resolved.Width, resolved.Height, resolved.Layers, scratch.AsSpan(0, totalBytes), nearest: true);
        }
        finally
        {
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        }

        _groundTextureArrayPlan = resolved;
        _groundArraySlotMaterialsFingerprint = slots;
        _groundTextureArraysReady = UploadGroundMaterialDrawRecords(slots);
    }

    private bool UploadGroundMaterialDrawRecords(PreviewMaterial[] slots)
    {
        if (!_genesisMaterialDrawRecordsUseSsbo ||
            _genesisMaterialDrawRecordUpload is null ||
            slots.Length <= 0 ||
            slots.Length > GenesisMaterialDrawRecordMaxRecords)
        {
            return false;
        }

        var byteCount = slots.Length * GenesisMaterialDrawRecordBytes;
        var records = MemoryMarshal.Cast<byte, float>(_genesisMaterialDrawRecordScratch.AsSpan(0, byteCount));
        records.Clear();
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            var cutout = (uint)i < (uint)_grassGroundSlots.Length && _grassGroundSlots[i].Cutout;
            var hasNormal = slot.NormalRgba is { Length: > 0 };
            var hasSpecular = slot.SpecularRgba is { Length: > 0 };
            var hasHeight = slot.HeightRgba is { Length: > 0 } && !cutout;
            var record = records.Slice(i * GenesisMaterialDrawRecordFloats, GenesisMaterialDrawRecordFloats);
            record[0] = 1f;
            record[1] = 1f;
            record[2] = 1f;
            record[3] = Math.Max(1, slot.Width);
            record[4] = Math.Max(1, slot.Height);
            record[5] = i; // texture-array layer
            record[8] = hasHeight ? 1f : 0f;
            record[9] = hasHeight ? 1f : 0f;
            record[10] = hasHeight ? 1f : 0f;
            record[11] = 0f;
            record[12] = hasNormal ? 1f : 0f;
            record[13] = hasSpecular ? 1f : 0f;
            record[14] = hasHeight ? 1f : 0f;
            record[15] = cutout ? (int)PreviewEntityAlphaMode.Cutout : 0;
        }

        _genesisMaterialDrawRecordUpload.Upload(_genesisMaterialDrawRecordScratch.AsSpan(0, byteCount));
        BindGenesisMaterialDrawRecordBuffer();
        return true;
    }

    private void BindGroundMaterialTextureArrays()
    {
        if (_program is null ||
            _groundAlbedoArray is null ||
            _groundNormalArray is null ||
            _groundSpecArray is null ||
            _groundHeightArray is null)
        {
            return;
        }

        var u = _mainUniformLocs;
        _groundAlbedoArray.Bind(MainPassAlbedoArrayUnit);
        _groundNormalArray.Bind(MainPassNormalArrayUnit);
        _groundSpecArray.Bind(MainPassSpecularArrayUnit);
        _groundHeightArray.Bind(MainPassHeightArrayUnit);
        SetIntLoc(u.AlbedoArray, MainPassAlbedoArrayUnit);
        SetIntLoc(u.NormalArray, MainPassNormalArrayUnit);
        SetIntLoc(u.SpecularArray, MainPassSpecularArrayUnit);
        SetIntLoc(u.HeightArray, MainPassHeightArrayUnit);
        BindGenesisMaterialDrawRecordBuffer();
    }

    private void DisposeGroundTextureArrays()
    {
        _groundAlbedoArray?.Dispose();
        _groundAlbedoArray = null;
        _groundNormalArray?.Dispose();
        _groundNormalArray = null;
        _groundSpecArray?.Dispose();
        _groundSpecArray = null;
        _groundHeightArray?.Dispose();
        _groundHeightArray = null;
        _groundTextureArrayPlan = null;
        _groundArraySlotMaterialsFingerprint = null;
        _groundTextureArraysReady = false;
    }
}
