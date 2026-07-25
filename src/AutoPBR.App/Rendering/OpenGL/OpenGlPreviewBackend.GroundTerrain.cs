using System.Numerics;
using System.Runtime.InteropServices;

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
        public int IndexCount => Allocation.IndexCount;
    }

    private readonly struct TerrainDrawItem
    {
        public required int FirstIndex { get; init; }
        public required int IndexCount { get; init; }
        public required int MaterialIndex { get; init; }
        public required bool Cutout { get; init; }
        public required bool NearPom { get; init; }
        public required TerrainChunkLodKind Lod { get; init; }
        public required int SourceOrder { get; init; }
    }

    private GlTerrainMeshPool? _terrainMeshPool;
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
    private bool _loggedTerrainMultiDraw;

    private void ApplyTerrainGrassBakeSettings(PreviewTerrainGrassBakeSettings settings)
    {
        EnsureTerrainStreamer();
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
        _terrainStreamer!.VegetationBakePlan = plan is { HasAny: true } ? plan : null;
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
        _terrainStreamer.WorldGenSettings = settings;
        // Clear streamed chunks and rebake; flat pad regenerates as height-0 Plains.
        DisposeTerrainGpuChunks();
        _terrainStreamer.InvalidateAll();
        // Only invalidate the DDA atlas when resolved world-gen actually changed; otherwise a
        // startup dirty apply would discard the bootstrap prefetch and leave DDA racing again.
        if (!previous.Equals(_terrainStreamer.WorldGenSettings))
        {
            _terrainOccluderWorldGenRevision++;
        }

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

    private void EnsureTerrainStreamer()
    {
        if (_terrainStreamer is not null)
        {
            return;
        }

        _terrainStreamer = new TerrainChunkStreamer();
        _terrainStreamer.Start();
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

        _terrainGpuChunks.Clear();
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
        _terrainMultiDrawIndirectCount = null;
        _terrainShadowGpuIndirectReady = false;
    }

    private void InitTerrainStreaming(GL gl)
    {
        EnsureTerrainStreamer();
        _groundMesh ??= new GlMeshBuffer(gl);
        EnsureTerrainMeshPool(gl);
        _terrainEnvFloorY = PreviewStageConstants.GroundPlaneWorldY +
                            PreviewStageConstants.TerrainSolidFloorRelativeY;
        _terrainEnvCeilingY = PreviewStageConstants.GroundPlaneWorldY +
                              PreviewStageConstants.TerrainMountainMaxReliefBlocks;
        // Start the DDA height atlas bake now so it can finish during later bootstrap steps.
        PrefetchTerrainOccluderAtlas(gl);
    }

    private void EnsureTerrainMeshPool(GL gl)
    {
        if (_terrainMeshPool is { IsValid: true })
        {
            return;
        }

        _terrainMeshPool?.Dispose();
        _terrainMeshPool = new GlTerrainMeshPool(gl);
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
                "[3D preview] Terrain mesh pool ready: shared VAO/VBO/EBO for streamed chunks (single-bind draws).");
        }
    }

    private void TickTerrainStreaming(ref GlRenderFrame frame)
    {
        if (!frame.Settings.ShowGroundMesh)
        {
            return;
        }

        EnsureTerrainStreamer();
        var viewDist = frame.Settings.ChunkViewDistance;
        _terrainStreamer!.Tick(frame.Eye, viewDist);

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

        var uploadCap = _terrainStreamingNeedsFrames
            ? PreviewStageConstants.TerrainMaxChunkUploadsPerFrameCatchUp
            : PreviewStageConstants.TerrainMaxChunkUploadsPerFrame;
        var uploads = new List<PreviewTerrainChunkMesh>(uploadCap);
        _terrainStreamer.DrainReady(uploads, uploadCap);
        foreach (var cpu in uploads)
        {
            UploadTerrainChunk(frame.Gl, cpu);
        }

        var disposed = 0;
        List<TerrainChunkKey>? toRemove = null;
        foreach (var (key, _) in _terrainGpuChunks)
        {
            if (!_terrainStreamer.ShouldUnload(key) && desired.ContainsKey(key))
            {
                continue;
            }

            // Keep hysteresis: only unload when ShouldUnload (past LOD+1).
            if (!_terrainStreamer.ShouldUnload(key))
            {
                continue;
            }

            toRemove ??= [];
            toRemove.Add(key);
            if (++disposed >= PreviewStageConstants.TerrainMaxChunkDisposalsPerFrame)
            {
                break;
            }
        }

        if (toRemove is not null)
        {
            foreach (var key in toRemove)
            {
                if (_terrainGpuChunks.Remove(key, out var gpu))
                {
                    _terrainMeshPool?.Free(gpu.Allocation);
                    _terrainStreamer.NotifyUnloaded(key);
                    _terrainCandidatesChunkVersion++;
                    InvalidateTerrainShadowWorldAabbCache();
                }
            }
        }

        RefreshTerrainEnvBounds();

        var desiredCount = desired.Count;
        var needsFrames = _terrainGpuChunks.Count < desiredCount || uploads.Count > 0;
        lock (_sync)
        {
            _terrainStreamingNeedsFrames = needsFrames;
        }
    }

    private void UploadTerrainChunk(GL gl, PreviewTerrainChunkMesh cpu)
    {
        EnsureTerrainMeshPool(gl);
        var pool = _terrainMeshPool!;
        if (_terrainGpuChunks.TryGetValue(cpu.Key, out var existing))
        {
            pool.Free(existing.Allocation);
            existing.Allocation = pool.Upload(cpu.InterleavedVertices, cpu.Indices);
            existing.Lod = cpu.Lod;
            existing.DrawBatches = RemapBatchesToPool(cpu.DrawBatches, existing.Allocation);
            existing.BoundsCenter = cpu.BoundsCenter;
            existing.BoundsRadius = cpu.BoundsRadius;
            existing.MinRelativeHeight = cpu.MinRelativeHeight;
            existing.MaxRelativeHeight = cpu.MaxRelativeHeight;
            _terrainStreamer?.NotifyUploaded(cpu.Key, cpu.Lod);
            _terrainCandidatesChunkVersion++;
            InvalidateTerrainShadowWorldAabbCache();
            return;
        }

        var allocation = pool.Upload(cpu.InterleavedVertices, cpu.Indices);
        _terrainGpuChunks[cpu.Key] = new TerrainGpuChunk
        {
            Lod = cpu.Lod,
            Allocation = allocation,
            DrawBatches = RemapBatchesToPool(cpu.DrawBatches, allocation),
            BoundsCenter = cpu.BoundsCenter,
            BoundsRadius = cpu.BoundsRadius,
            MinRelativeHeight = cpu.MinRelativeHeight,
            MaxRelativeHeight = cpu.MaxRelativeHeight
        };
        _terrainStreamer?.NotifyUploaded(cpu.Key, cpu.Lod);
        _terrainCandidatesChunkVersion++;
        InvalidateTerrainShadowWorldAabbCache();
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
        foreach (var chunk in _terrainGpuChunks.Values)
        {
            if (chunk.IndexCount <= 0)
            {
                continue;
            }

            var sourceIndex = _terrainDrawChunkScratch.Count;
            _terrainDrawChunkScratch.Add(chunk);
            _terrainDrawCandidates.Add(new TerrainChunkDrawCull.Candidate
            {
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
                () => TerrainChunkDrawCull.Select(
                    _terrainDrawCandidates,
                    nearVp,
                    cameraPosition,
                    fallback,
                    fullOnly: true,
                    _terrainShadowSelectedNear,
                    maxCasterDistanceXz: nearCasterDistanceXz),
                () => TerrainChunkDrawCull.Select(
                    _terrainDrawCandidates,
                    midVp,
                    cameraPosition,
                    fallback,
                    fullOnly: false,
                    _terrainShadowSelectedMid,
                    maxCasterDistanceXz: midCasterDistanceXz),
                () => TerrainChunkDrawCull.Select(
                    _terrainDrawCandidates,
                    farVp,
                    cameraPosition,
                    fallback,
                    fullOnly: false,
                    _terrainShadowSelectedFar,
                    maxCasterDistanceXz: farCasterDistanceXz));
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

        EnsureTerrainMeshPool(_gl);
        if (_terrainMultiDrawIndirectCount is null)
        {
            return false;
        }

        var candidateCount = _terrainDrawCandidates.Count;
        if (_terrainShadowCullRecordScratch.Length < candidateCount)
        {
            _terrainShadowCullRecordScratch = new TerrainShadowCullRecord[candidateCount];
        }

        var dwordCount = candidateCount * GlIndirectDrawCommandBuffer.CommandDwords;
        if (_terrainShadowSourceCommandScratch.Length < dwordCount)
        {
            _terrainShadowSourceCommandScratch = new uint[Math.Max(dwordCount, 256)];
        }

        var records = _terrainShadowCullRecordScratch.AsSpan(0, candidateCount);
        for (var i = 0; i < candidateCount; i++)
        {
            var c = _terrainDrawCandidates[i];
            var chunk = _terrainDrawChunkScratch[c.SourceIndex];
            records[i] = new TerrainShadowCullRecord(
                c.BoundsCenter,
                c.BoundsRadius,
                isFullLod: c.Lod == TerrainChunkLodKind.Full,
                candidateIndex: i);
            var shadowIndexCount = Math.Max(0, chunk.IndexCount);
            var shadowFirstIndex = Math.Max(0, chunk.Allocation.IndexOffset);
            GlIndirectDrawCommandBuffer.WriteCommandDwords(
                _terrainShadowSourceCommandScratch.AsSpan(
                    i * GlIndirectDrawCommandBuffer.CommandDwords,
                    GlIndirectDrawCommandBuffer.CommandDwords),
                (uint)shadowIndexCount,
                (uint)shadowFirstIndex,
                baseInstance: 0u);
        }

        _terrainShadowSourceCommands ??= new GlIndirectDrawCommandBuffer(_gl);
        if (!_terrainShadowSourceCommands.UploadCommands(_terrainShadowSourceCommandScratch, candidateCount))
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
                $"[3D preview] GPU terrain shadow cull enabled (no readback): candidates={candidateCount}, " +
                "MultiDrawIndirectCount per cascade.");
        }

        return true;
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
                    MaterialIndex = 0,
                    Cutout = false,
                    NearPom = nearPom,
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
                    MaterialIndex = materialIndex,
                    Cutout = cutout,
                    NearPom = nearPom && !cutout,
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
                updatePatchParameter: false);
        }

        pool.UnbindVertexArray();
        if (!shadowPass && lastPom != enableParallaxSetting)
        {
            setParallaxEnabled(enableParallaxSetting);
        }

        if (_grassGroundSlots.Length > 1)
        {
            SetGroundAlphaMode(cutout: false, shadowPass);
        }
    }

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
                    updatePatchParameter: false);
            }

            pool.UnbindVertexArray();
            if (!_loggedTerrainMultiDraw)
            {
                _loggedTerrainMultiDraw = true;
                EmitDiagnostic(
                    $"[3D preview] Terrain MultiDrawIndirect enabled: items={itemCount}, " +
                    $"arrays={(useArrays ? "on" : "off")}, shadow={(shadowPass ? "yes" : "no")}.");
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
                // With texture arrays + draw-parameter baseInstance, materials share one bind group.
                var sameMaterial = (!shadowPass && useArrays && _activeGenesisProgramKey.DrawRecordBaseInstance) ||
                                   next.MaterialIndex == head.MaterialIndex;
                if (!samePom || !sameCutout || !sameMaterial)
                {
                    break;
                }

                groupEnd++;
            }

            var pom = !shadowPass && head.NearPom && enableParallaxSetting && !head.Cutout;
            if (!shadowPass)
            {
                setParallaxEnabled(pom);
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
                        updatePatchParameter: false);
                }
            }

            groupStart = groupEnd;
        }

        pool.UnbindVertexArray();
        if (!_loggedTerrainMultiDraw)
        {
            _loggedTerrainMultiDraw = true;
            EmitDiagnostic(
                $"[3D preview] Terrain MultiDrawIndirect enabled: items={itemCount}, " +
                $"arrays={(useArrays ? "on" : "off")}, shadow={(shadowPass ? "yes" : "no")}.");
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
