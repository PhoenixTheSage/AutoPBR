using System.Numerics;

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
        bool shadowFullOnly = false)
    {
        _ = gl;
        if (_terrainGpuChunks.Count == 0)
        {
            return;
        }

        var pomRadiusSq = PreviewStageConstants.TerrainNearPomRadius *
                          PreviewStageConstants.TerrainNearPomRadius;

        _terrainDrawChunkScratch.Clear();
        _terrainDrawCandidates.Clear();
        foreach (var chunk in _terrainGpuChunks.Values)
        {
            if (chunk.Mesh.IndexCount <= 0)
            {
                continue;
            }

            var dx = chunk.BoundsCenter.X - cameraPosition.X;
            var dz = chunk.BoundsCenter.Z - cameraPosition.Z;
            var nearCam = dx * dx + dz * dz <= pomRadiusSq;
            var nearPom = enableParallaxSetting &&
                          chunk.Lod == TerrainChunkLodKind.Full &&
                          nearCam;
            var sourceIndex = _terrainDrawChunkScratch.Count;
            _terrainDrawChunkScratch.Add(chunk);
            _terrainDrawCandidates.Add(new TerrainChunkDrawCull.Candidate
            {
                BoundsCenter = chunk.BoundsCenter,
                BoundsRadius = chunk.BoundsRadius,
                Lod = chunk.Lod,
                NearPom = nearPom,
                SourceIndex = sourceIndex
            });
        }

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
            _terrainDrawSelected);

        var lastPom = !enableParallaxSetting;
        GlMeshBuffer? lastMesh = null;
        foreach (var idx in _terrainDrawSelected)
        {
            var candidate = _terrainDrawCandidates[idx];
            var chunk = _terrainDrawChunkScratch[candidate.SourceIndex];
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
