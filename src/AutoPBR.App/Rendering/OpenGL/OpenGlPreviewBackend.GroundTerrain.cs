using System.Numerics;
using System.Threading.Tasks;

using AutoPBR.App.Rendering.Scene;
using AutoPBR.Preview;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private sealed class TerrainGpuChunk
    {
        public required TerrainChunkKey Key { get; init; }
        public required TerrainChunkLodKind Lod { get; set; }
        public required GlMeshBuffer Mesh { get; init; }
        public Vector3 BoundsCenter { get; set; }
        public float BoundsRadius { get; set; }
        public int MinRelativeHeight { get; set; }
        public int MaxRelativeHeight { get; set; }
    }

    private readonly List<TerrainChunkDrawCull.Candidate> _terrainDrawCandidates = new(256);
    private readonly List<int> _terrainDrawSelected = new(256);
    private readonly List<TerrainGpuChunk> _terrainDrawChunkScratch = new(256);

    /// <summary>Shadow-pass candidate snapshot (built once per frame; culled per cascade).</summary>
    private readonly List<TerrainChunkDrawCull.Candidate> _terrainShadowCandidates = new(256);
    private readonly List<TerrainGpuChunk> _terrainShadowChunkScratch = new(256);
    private readonly List<int> _terrainShadowSelectedNear = new(256);
    private readonly List<int> _terrainShadowSelectedMid = new(256);
    private readonly List<int> _terrainShadowSelectedFar = new(256);
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

    private void ShutdownTerrainStreamer()
    {
        DisposeTerrainGpuChunks();
        _terrainStreamer?.Dispose();
        _terrainStreamer = null;
        _groundMesh = null;
        _groundChunkBatches = [];
    }

    private void DisposeTerrainGpuChunks()
    {
        foreach (var chunk in _terrainGpuChunks.Values)
        {
            chunk.Mesh.Dispose();
        }

        _terrainGpuChunks.Clear();
    }

    private void InitTerrainStreaming(GL gl)
    {
        EnsureTerrainStreamer();
        _groundMesh ??= new GlMeshBuffer(gl);
        _groundChunkBatches = [];
        _terrainEnvFloorY = PreviewStageConstants.GroundPlaneWorldY - PreviewStageConstants.TerrainFillDepth;
        _terrainEnvCeilingY = PreviewStageConstants.GroundPlaneWorldY + PreviewStageConstants.TerrainMaxReliefBlocks;
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
        foreach (var (key, gpu) in _terrainGpuChunks)
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
                    gpu.Mesh.Dispose();
                    _terrainStreamer.NotifyUnloaded(key);
                }
            }
        }

        RefreshTerrainEnvBounds();

        var desiredCount = desired.Count;
        _terrainStreamingNeedsFrames =
            _terrainGpuChunks.Count < desiredCount || uploads.Count > 0;
    }

    private void UploadTerrainChunk(GL gl, PreviewTerrainChunkMesh cpu)
    {
        if (_terrainGpuChunks.TryGetValue(cpu.Key, out var existing))
        {
            existing.Mesh.Upload(cpu.InterleavedVertices, cpu.Indices);
            existing.Lod = cpu.Lod;
            existing.BoundsCenter = cpu.BoundsCenter;
            existing.BoundsRadius = cpu.BoundsRadius;
            existing.MinRelativeHeight = cpu.MinRelativeHeight;
            existing.MaxRelativeHeight = cpu.MaxRelativeHeight;
            _terrainStreamer?.NotifyUploaded(cpu.Key, cpu.Lod);
            return;
        }

        var mesh = new GlMeshBuffer(gl);
        mesh.Upload(cpu.InterleavedVertices, cpu.Indices);
        _terrainGpuChunks[cpu.Key] = new TerrainGpuChunk
        {
            Key = cpu.Key,
            Lod = cpu.Lod,
            Mesh = mesh,
            BoundsCenter = cpu.BoundsCenter,
            BoundsRadius = cpu.BoundsRadius,
            MinRelativeHeight = cpu.MinRelativeHeight,
            MaxRelativeHeight = cpu.MaxRelativeHeight
        };
        _terrainStreamer?.NotifyUploaded(cpu.Key, cpu.Lod);
    }

    private void RefreshTerrainEnvBounds()
    {
        if (_terrainGpuChunks.Count == 0)
        {
            var pad = PreviewStageConstants.TerrainDefaultChunkViewDistance *
                      PreviewStageConstants.TerrainChunkSize;
            _terrainEnvFloorY = PreviewStageConstants.GroundPlaneWorldY -
                                PreviewStageConstants.TerrainFillDepth;
            _terrainEnvCeilingY = PreviewStageConstants.GroundPlaneWorldY +
                                  PreviewStageConstants.TerrainMaxReliefBlocks;
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

        CollectTerrainDrawCandidates(
            cameraPosition,
            enableParallaxSetting,
            _terrainDrawChunkScratch,
            _terrainDrawCandidates);

        if (_terrainDrawCandidates.Count == 0)
        {
            return;
        }

        TerrainChunkDrawCull.Select(
            _terrainDrawCandidates,
            viewProjection,
            cameraPosition,
            PreviewStageConstants.TerrainFrustumDrawFallbackCount,
            fullOnly: shadowFullOnly,
            _terrainDrawSelected,
            maxCasterDistanceXz: maxCasterDistanceXz);

        DrawTerrainCandidates(
            _terrainDrawCandidates,
            _terrainDrawChunkScratch,
            _terrainDrawSelected,
            patches,
            enableParallaxSetting,
            setParallaxEnabled);
    }

    private void CollectTerrainDrawCandidates(
        Vector3 cameraPosition,
        bool enableParallaxSetting,
        List<TerrainGpuChunk> scratch,
        List<TerrainChunkDrawCull.Candidate> candidates)
    {
        scratch.Clear();
        candidates.Clear();
        if (_terrainGpuChunks.Count == 0)
        {
            return;
        }

        var pomEnableRadius = PreviewStageConstants.TerrainNearPomRadius +
                              PreviewStageConstants.TerrainNearPomFadeWidth;
        var pomEnableRadiusSq = pomEnableRadius * pomEnableRadius;

        foreach (var chunk in _terrainGpuChunks.Values)
        {
            if (chunk.Mesh.IndexCount <= 0)
            {
                continue;
            }

            var dx = chunk.BoundsCenter.X - cameraPosition.X;
            var dz = chunk.BoundsCenter.Z - cameraPosition.Z;
            // Keep POM enabled through the fade band; fragment shader softens strength by distance.
            var nearCam = dx * dx + dz * dz <= pomEnableRadiusSq;
            var nearPom = enableParallaxSetting &&
                          chunk.Lod == TerrainChunkLodKind.Full &&
                          nearCam;
            var sourceIndex = scratch.Count;
            scratch.Add(chunk);
            candidates.Add(new TerrainChunkDrawCull.Candidate
            {
                BoundsCenter = chunk.BoundsCenter,
                BoundsRadius = chunk.BoundsRadius,
                Lod = chunk.Lod,
                NearPom = nearPom,
                SourceIndex = sourceIndex
            });
        }
    }

    private void PrepareTerrainShadowCasterSelections(
        Vector3 cameraPosition,
        Matrix4x4 nearVp,
        Matrix4x4 midVp,
        Matrix4x4 farVp,
        float nearCasterDistanceXz,
        float midCasterDistanceXz,
        float farCasterDistanceXz,
        bool cascadesActive)
    {
        _terrainShadowSelectedNear.Clear();
        _terrainShadowSelectedMid.Clear();
        _terrainShadowSelectedFar.Clear();
        CollectTerrainDrawCandidates(
            cameraPosition,
            enableParallaxSetting: false,
            _terrainShadowChunkScratch,
            _terrainShadowCandidates);
        if (_terrainShadowCandidates.Count == 0)
        {
            return;
        }

        var fallback = PreviewStageConstants.TerrainFrustumDrawFallbackCount;
        if (!cascadesActive)
        {
            TerrainChunkDrawCull.Select(
                _terrainShadowCandidates,
                farVp,
                cameraPosition,
                fallback,
                fullOnly: false,
                _terrainShadowSelectedFar,
                maxCasterDistanceXz: farCasterDistanceXz);
            return;
        }

        if (_terrainShadowCandidates.Count >= TerrainShadowParallelCullMinCandidates)
        {
            Parallel.Invoke(
                () => TerrainChunkDrawCull.Select(
                    _terrainShadowCandidates,
                    nearVp,
                    cameraPosition,
                    fallback,
                    fullOnly: true,
                    _terrainShadowSelectedNear,
                    maxCasterDistanceXz: nearCasterDistanceXz),
                () => TerrainChunkDrawCull.Select(
                    _terrainShadowCandidates,
                    midVp,
                    cameraPosition,
                    fallback,
                    fullOnly: false,
                    _terrainShadowSelectedMid,
                    maxCasterDistanceXz: midCasterDistanceXz),
                () => TerrainChunkDrawCull.Select(
                    _terrainShadowCandidates,
                    farVp,
                    cameraPosition,
                    fallback,
                    fullOnly: false,
                    _terrainShadowSelectedFar,
                    maxCasterDistanceXz: farCasterDistanceXz));
            return;
        }

        TerrainChunkDrawCull.Select(
            _terrainShadowCandidates,
            nearVp,
            cameraPosition,
            fallback,
            fullOnly: true,
            _terrainShadowSelectedNear,
            maxCasterDistanceXz: nearCasterDistanceXz);
        TerrainChunkDrawCull.Select(
            _terrainShadowCandidates,
            midVp,
            cameraPosition,
            fallback,
            fullOnly: false,
            _terrainShadowSelectedMid,
            maxCasterDistanceXz: midCasterDistanceXz);
        TerrainChunkDrawCull.Select(
            _terrainShadowCandidates,
            farVp,
            cameraPosition,
            fallback,
            fullOnly: false,
            _terrainShadowSelectedFar,
            maxCasterDistanceXz: farCasterDistanceXz);
    }

    private void DrawPreparedTerrainShadowCasters(List<int> selected)
    {
        DrawTerrainCandidates(
            _terrainShadowCandidates,
            _terrainShadowChunkScratch,
            selected,
            patches: false,
            enableParallaxSetting: false,
            setParallaxEnabled: static _ => { });
    }

    private static void DrawTerrainCandidates(
        List<TerrainChunkDrawCull.Candidate> candidates,
        List<TerrainGpuChunk> scratch,
        List<int> selected,
        bool patches,
        bool enableParallaxSetting,
        Action<bool> setParallaxEnabled)
    {
        var lastPom = !enableParallaxSetting;
        GlMeshBuffer? lastMesh = null;
        foreach (var idx in selected)
        {
            var candidate = candidates[idx];
            var chunk = scratch[candidate.SourceIndex];
            var pom = candidate.NearPom;
            if (pom != lastPom)
            {
                setParallaxEnabled(pom);
                lastPom = pom;
            }

            chunk.Mesh.Draw(patches, keepBound: true);
            if (lastMesh is not null && !ReferenceEquals(lastMesh, chunk.Mesh))
            {
                lastMesh.ClearBoundTracking();
            }

            lastMesh = chunk.Mesh;
        }

        lastMesh?.UnbindVertexArray();

        if (lastPom != enableParallaxSetting)
        {
            setParallaxEnabled(enableParallaxSetting);
        }
    }
}
