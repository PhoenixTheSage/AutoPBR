using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private const int HiZSamplerUnit = 10;
    private const int VoxelOccluderSamplerUnit = 11;

    private GlDepthPrepassTarget? _depthPrepassTarget;
    private GlHierarchicalZPyramid? _hierarchicalZ;
    private GlTerrainOccluderAtlas? _terrainOccluderAtlas;
    private GlShaderProgram? _hizBuildProgram;
    private bool _hizBuildCompileDisabled;
    private bool _loggedHiZOcclusionEnabled;
    private bool _loggedVoxelDdaOcclusionEnabled;
    private bool _loggedVoxelDdaOcclusionPending;
    private bool _loggedVoxelDdaSlowBake;
    private string _loggedVoxelDdaFailure = "none";
    private bool _hiZReadyThisFrame;
    private bool _voxelDdaReadyThisFrame;
    private int _terrainOccluderWorldGenRevision;
    private GlGpuDrawReductionSnapshot _occlusionDebugFrameTotals;
    private int _occlusionDebugGroupsThisFrame;
    private string? _latestOcclusionDebugHudText;
    private long _lastOcclusionDebugSampleUnixMs;
    private bool _occlusionDebugSampleThisFrame;
    private bool _occlusionDebugReadThisFrame;

    private bool CanUseHiZOcclusionThisFrame =>
        _glCapabilities?.CanUseHierarchicalZOcclusion == true &&
        !_hizBuildCompileDisabled &&
        _shadowProgram is { IsValid: true };

    private bool CanUseVoxelDdaOcclusionThisFrame =>
        _glCapabilities?.CanUseGpuCompactedDrawSubmission == true &&
        _terrainOccluderAtlas is { IsValid: true };

    private static PreviewOcclusionDebugMode ResolveOcclusionDebugMode(in PreviewRenderSettingsSnapshot settings) =>
        (PreviewOcclusionDebugMode)Math.Clamp(settings.OcclusionDebugMode, 0, 2);

    private bool OcclusionDebugEnabled(in PreviewRenderSettingsSnapshot settings) =>
        ResolveOcclusionDebugMode(settings) != PreviewOcclusionDebugMode.Off;

    /// <summary>
    /// Hi-Z prepass only when the voxel DDA atlas is unavailable. Never run both: TintCulled used to
    /// force Hi-Z alongside DDA and combined with sync counter readbacks collapsed FPS.
    /// </summary>
    private bool ShouldRunHiZPrepassThisFrame(in PreviewRenderSettingsSnapshot _) =>
        CanUseHiZOcclusionThisFrame && !CanUseVoxelDdaOcclusionThisFrame;

    private bool TryEnsureHierarchicalZResources(int width, int height)
    {
        if (_gl is null || !CanUseHiZOcclusionThisFrame)
        {
            return false;
        }

        _depthPrepassTarget ??= new GlDepthPrepassTarget(_gl);
        if (!_depthPrepassTarget.EnsureSize(width, height))
        {
            return false;
        }

        _hierarchicalZ ??= new GlHierarchicalZPyramid(_gl);
        if (!_hierarchicalZ.EnsureSize(width, height))
        {
            return false;
        }

        if (_hizBuildProgram is not { IsValid: true })
        {
            _hizBuildProgram = CreatePreviewComputeProgram(
                "genesis_hiz_build.comp",
                out var error,
                "genesis-hiz-build");
            if (!_hizBuildProgram.IsValid)
            {
                EmitDiagnostic("[3D preview] Hi-Z build compute failed: " + (error ?? "link failed"));
                _hizBuildProgram.Dispose();
                _hizBuildProgram = null;
                _hizBuildCompileDisabled = true;
                return false;
            }
        }

        return true;
    }

    private bool TryBuildHierarchicalZFromPrepass()
    {
        if (_hierarchicalZ is null ||
            _depthPrepassTarget is not { IsValid: true } ||
            _hizBuildProgram is not { IsValid: true })
        {
            return false;
        }

        return _hierarchicalZ.Build(_hizBuildProgram, _depthPrepassTarget.DepthTextureHandle);
    }

    private void DisposeHierarchicalZResources()
    {
        _hizBuildProgram?.Dispose();
        _hizBuildProgram = null;
        _hierarchicalZ?.Dispose();
        _hierarchicalZ = null;
        _depthPrepassTarget?.Dispose();
        _depthPrepassTarget = null;
        _terrainOccluderAtlas?.Dispose();
        _terrainOccluderAtlas = null;
        _hizBuildCompileDisabled = false;
        _loggedHiZOcclusionEnabled = false;
        _loggedVoxelDdaOcclusionEnabled = false;
        _loggedVoxelDdaOcclusionPending = false;
        _loggedVoxelDdaSlowBake = false;
        _loggedVoxelDdaFailure = "none";
        _voxelDdaReadyThisFrame = false;
        _latestOcclusionDebugHudText = null;
    }

    private void AbandonHierarchicalZResources()
    {
        _hizBuildProgram = null;
        _hierarchicalZ = null;
        _depthPrepassTarget = null;
        _terrainOccluderAtlas = null;
        _hizBuildCompileDisabled = false;
        _loggedHiZOcclusionEnabled = false;
        _loggedVoxelDdaOcclusionEnabled = false;
        _loggedVoxelDdaOcclusionPending = false;
        _loggedVoxelDdaSlowBake = false;
        _loggedVoxelDdaFailure = "none";
        _voxelDdaReadyThisFrame = false;
        _latestOcclusionDebugHudText = null;
    }

    private void BeginOcclusionDebugFrame()
    {
        _occlusionDebugFrameTotals = default;
        _occlusionDebugGroupsThisFrame = 0;
        _occlusionDebugReadThisFrame = false;
        // At most one sync counter readback every 2s — per-group GetBufferSubData stalls the GL pipe.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _occlusionDebugSampleThisFrame =
            OcclusionDebugEnabled(_settings) &&
            now - _lastOcclusionDebugSampleUnixMs >= 2000;
    }

    private void AccumulateOcclusionDebug(in GlGpuDrawReductionSnapshot snap)
    {
        _occlusionDebugGroupsThisFrame++;
        _occlusionDebugFrameTotals = new GlGpuDrawReductionSnapshot(
            _occlusionDebugFrameTotals.ExaminedCommands + snap.ExaminedCommands,
            _occlusionDebugFrameTotals.WrittenCommands + snap.WrittenCommands,
            _occlusionDebugFrameTotals.FrustumCulledCommands + snap.FrustumCulledCommands,
            _occlusionDebugFrameTotals.DistanceCulledCommands + snap.DistanceCulledCommands,
            _occlusionDebugFrameTotals.EmptyCommands + snap.EmptyCommands,
            _occlusionDebugFrameTotals.VisibilityFlagCulledCommands + snap.VisibilityFlagCulledCommands,
            _occlusionDebugFrameTotals.OverflowCommands + snap.OverflowCommands,
            Math.Max(_occlusionDebugFrameTotals.MaximumIndexCount, snap.MaximumIndexCount),
            _occlusionDebugFrameTotals.OcclusionCulledCommands + snap.OcclusionCulledCommands);
    }

    private void FinishOcclusionDebugFrame(in PreviewRenderSettingsSnapshot settings)
    {
        if (!OcclusionDebugEnabled(settings))
        {
            return;
        }

        if (!_occlusionDebugSampleThisFrame || _occlusionDebugGroupsThisFrame <= 0)
        {
            return;
        }

        _lastOcclusionDebugSampleUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var mode = ResolveOcclusionDebugMode(settings);
        var path = _voxelDdaReadyThisFrame ? "dda" : (_hiZReadyThisFrame ? "hiz" : "none");
        _latestOcclusionDebugHudText =
            $"Occ[{path}/{mode}]: occ={_occlusionDebugFrameTotals.OcclusionCulledCommands} " +
            $"frustum={_occlusionDebugFrameTotals.FrustumCulledCommands} " +
            $"dist={_occlusionDebugFrameTotals.DistanceCulledCommands} " +
            $"written={_occlusionDebugFrameTotals.WrittenCommands}/{_occlusionDebugFrameTotals.ExaminedCommands}";

        EmitDiagnostic("[3D preview] Occlusion debug: " + _latestOcclusionDebugHudText + "; " +
                       _occlusionDebugFrameTotals.FormatDiagnostic());
    }

    private void TickTerrainOccluderAtlas(ref GlRenderFrame frame)
    {
        _voxelDdaReadyThisFrame = false;
        if (_gl is null ||
            _glCapabilities?.CanUseGpuCompactedDrawSubmission != true ||
            !frame.Settings.ShowGroundMesh)
        {
            // Still drain a completed bake so a prior prefetch/reload is not left latched.
            _terrainOccluderAtlas?.PumpUpload();
            return;
        }

        _terrainOccluderAtlas ??= new GlTerrainOccluderAtlas(_gl);
        // Apply any completed off-thread bake, then request a rebuild if the camera ring moved.
        _terrainOccluderAtlas.PumpUpload();
        MaybeLogTerrainOccluderFailure();
        EnsureTerrainStreamer();
        var cameraChunk = TerrainChunkKey.FromWorld(frame.Eye.X, frame.Eye.Z);
        var worldGen = _terrainStreamer!.WorldGenSettings;
        _terrainOccluderAtlas.EnsureFilled(
            cameraChunk,
            frame.Settings.ChunkViewDistance,
            worldGen,
            _terrainOccluderWorldGenRevision,
            frame.Settings.LodRingChunks);
        _terrainOccluderAtlas.PumpUpload();
        if (_terrainOccluderAtlas.IsValid)
        {
            _voxelDdaReadyThisFrame = true;
            if (!_loggedVoxelDdaOcclusionEnabled)
            {
                _loggedVoxelDdaOcclusionEnabled = true;
                EmitDiagnostic(
                    $"[3D preview] P5.4 voxel DDA occlusion enabled: atlas={_terrainOccluderAtlas.Width}x{_terrainOccluderAtlas.Height} " +
                    $"origin=({_terrainOccluderAtlas.OriginX},{_terrainOccluderAtlas.OriginZ}); " +
                    "Hi-Z prepass skipped while DDA atlas is valid; atlas bakes off the GL thread.");
            }
        }
        else if (_terrainOccluderAtlas.IsBakeInFlight && !_loggedVoxelDdaOcclusionPending)
        {
            // One-shot so the log shows why Hi-Z is still primary during the first bake.
            _loggedVoxelDdaOcclusionPending = true;
            EmitDiagnostic(
                "[3D preview] P5.4 voxel DDA occlusion pending: heightfield atlas bake in flight; " +
                "Hi-Z remains primary until the first atlas upload completes.");
        }
    }

    /// <summary>
    /// Kick the heightfield atlas bake as soon as terrain streaming exists so worker time overlaps
    /// remaining GPU bootstrap steps (atmosphere, prewarm) instead of waiting for the first scene frame.
    /// </summary>
    private void PrefetchTerrainOccluderAtlas(GL gl)
    {
        if (_glCapabilities?.CanUseGpuCompactedDrawSubmission != true)
        {
            return;
        }

        EnsureTerrainStreamer();
        _terrainOccluderAtlas ??= new GlTerrainOccluderAtlas(gl);
        PreviewTerrainWorldGenSettings worldGen;
        int viewDistance;
        int lodRingChunks;
        float eyeX;
        float eyeZ;
        lock (_sync)
        {
            worldGen = _terrainWorldGenSettings;
            viewDistance = _settings.ChunkViewDistance;
            lodRingChunks = _settings.LodRingChunks;
            if (_flyEngaged)
            {
                eyeX = _flyPosition.X;
                eyeZ = _flyPosition.Z;
            }
            else
            {
                // Orbit eye is not stored as a world position here; origin is fine for the first atlas.
                eyeX = 0f;
                eyeZ = 0f;
            }
        }

        // Keep streamer settings aligned so the first Setup dirty-apply does not bump the atlas revision.
        _terrainStreamer!.WorldGenSettings = worldGen;
        var cameraChunk = TerrainChunkKey.FromWorld(eyeX, eyeZ);
        _terrainOccluderAtlas.EnsureFilled(
            cameraChunk,
            viewDistance,
            _terrainStreamer.WorldGenSettings,
            _terrainOccluderWorldGenRevision,
            lodRingChunks);
        _terrainOccluderAtlas.PumpUpload();
        MaybeLogTerrainOccluderFailure();
    }

    /// <summary>
    /// Upload a completed prefetch bake during bootstrap frames (PassScene does not run yet).
    /// </summary>
    private void PumpTerrainOccluderAtlasBootstrap()
    {
        if (_gl is null ||
            _glCapabilities?.CanUseGpuCompactedDrawSubmission != true ||
            _terrainOccluderAtlas is null)
        {
            return;
        }

        _terrainOccluderAtlas.PumpUpload();
        MaybeLogTerrainOccluderFailure();
    }

    private void MaybeLogTerrainOccluderFailure()
    {
        if (_terrainOccluderAtlas is null)
        {
            return;
        }

        if (_terrainOccluderAtlas.IsBakeSlow && !_loggedVoxelDdaSlowBake)
        {
            _loggedVoxelDdaSlowBake = true;
            EmitDiagnostic(
                "[3D preview] P5.4 voxel DDA initialization is still baking after " +
                $"{_terrainOccluderAtlas.BakeElapsedMilliseconds} ms; retaining one " +
                "single-flight worker and Hi-Z fallback.");
        }

        var failure = _terrainOccluderAtlas.LastFailureDiagnostic;
        if (failure == "none" ||
            string.Equals(failure, _loggedVoxelDdaFailure, StringComparison.Ordinal))
        {
            return;
        }

        _loggedVoxelDdaFailure = failure;
        EmitDiagnostic($"[3D preview] P5.4 voxel DDA initialization: {failure}.");
    }
}
