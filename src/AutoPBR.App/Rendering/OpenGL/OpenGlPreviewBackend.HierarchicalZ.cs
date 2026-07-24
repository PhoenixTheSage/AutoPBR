using System.Numerics;
using System.Runtime.InteropServices;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.Preview;

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
    private GlShaderProgram? _hizSphereTestProgram;
    private bool _hizBuildCompileDisabled;
    private bool _hizSphereTestCompileDisabled;
    private bool _loggedHiZOcclusionEnabled;
    private bool _loggedVoxelDdaOcclusionEnabled;
    private bool _hiZReadyThisFrame;
    private bool _voxelDdaReadyThisFrame;
    private int _terrainOccluderWorldGenRevision;
    private GlGpuDrawReductionSnapshot _occlusionDebugFrameTotals;
    private int _occlusionDebugGroupsThisFrame;
    private string? _latestOcclusionDebugHudText;
    private long _lastOcclusionDebugSampleUnixMs;
    private bool _occlusionDebugSampleThisFrame;
    private bool _occlusionDebugReadThisFrame;
    private readonly List<Vector4> _terrainHiZSphereScratch = [];
    private uint[] _terrainHiZVisibilityScratch = [];

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
    private bool ShouldRunHiZPrepassThisFrame(in PreviewRenderSettingsSnapshot settings) =>
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

    private bool TryEnsureHiZSphereTestProgram()
    {
        if (_hizSphereTestCompileDisabled || _gl is null || _shaderCtx is null)
        {
            return false;
        }

        if (_hizSphereTestProgram is { IsValid: true })
        {
            return true;
        }

        _hizSphereTestProgram = CreatePreviewComputeProgram(
            "genesis_hiz_sphere_test.comp",
            out var error,
            "genesis-hiz-sphere-test");
        if (_hizSphereTestProgram.IsValid)
        {
            return true;
        }

        EmitDiagnostic("[3D preview] Hi-Z sphere test compute failed: " + (error ?? "link failed"));
        _hizSphereTestProgram.Dispose();
        _hizSphereTestProgram = null;
        _hizSphereTestCompileDisabled = true;
        return false;
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
        _hizSphereTestProgram?.Dispose();
        _hizSphereTestProgram = null;
        _hierarchicalZ?.Dispose();
        _hierarchicalZ = null;
        _depthPrepassTarget?.Dispose();
        _depthPrepassTarget = null;
        _terrainOccluderAtlas?.Dispose();
        _terrainOccluderAtlas = null;
        _hizBuildCompileDisabled = false;
        _hizSphereTestCompileDisabled = false;
        _loggedHiZOcclusionEnabled = false;
        _loggedVoxelDdaOcclusionEnabled = false;
        _voxelDdaReadyThisFrame = false;
        _latestOcclusionDebugHudText = null;
    }

    private void AbandonHierarchicalZResources()
    {
        _hizBuildProgram = null;
        _hizSphereTestProgram = null;
        _hierarchicalZ = null;
        _depthPrepassTarget = null;
        _terrainOccluderAtlas = null;
        _hizBuildCompileDisabled = false;
        _hizSphereTestCompileDisabled = false;
        _loggedHiZOcclusionEnabled = false;
        _loggedVoxelDdaOcclusionEnabled = false;
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
            !frame.Settings.ShowGroundMesh ||
            _terrainStreamer is null)
        {
            return;
        }

        _terrainOccluderAtlas ??= new GlTerrainOccluderAtlas(_gl);
        // Apply any completed off-thread bake, then request a rebuild if the camera ring moved.
        _terrainOccluderAtlas.PumpUpload();
        var cameraChunk = TerrainChunkKey.FromWorld(frame.Eye.X, frame.Eye.Z);
        _terrainOccluderAtlas.EnsureFilled(
            cameraChunk,
            frame.Settings.ChunkViewDistance,
            _terrainStreamer.WorldGenSettings,
            _terrainOccluderWorldGenRevision);
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
    }

    /// <summary>
    /// After frustum select, drop terrain chunks fully occluded in the Hi-Z pyramid.
    /// </summary>
    private void FilterTerrainSelectionByHiZ(
        List<TerrainChunkDrawCull.Candidate> candidates,
        List<int> selected,
        Matrix4x4 viewProj)
    {
        if (selected.Count == 0 ||
            _hierarchicalZ is not { IsValid: true } ||
            !TryEnsureHiZSphereTestProgram())
        {
            return;
        }

        _terrainHiZSphereScratch.Clear();
        for (var i = 0; i < selected.Count; i++)
        {
            var c = candidates[selected[i]];
            _terrainHiZSphereScratch.Add(new Vector4(c.BoundsCenter, c.BoundsRadius));
        }

        if (_terrainHiZVisibilityScratch.Length < _terrainHiZSphereScratch.Count)
        {
            _terrainHiZVisibilityScratch = new uint[Math.Max(_terrainHiZSphereScratch.Count, 64)];
        }

        if (!_hierarchicalZ.TestSpheres(
                _hizSphereTestProgram!,
                CollectionsMarshal.AsSpan(_terrainHiZSphereScratch),
                viewProj,
                _terrainHiZVisibilityScratch.AsSpan(0, _terrainHiZSphereScratch.Count)))
        {
            return;
        }

        var write = 0;
        for (var i = 0; i < selected.Count; i++)
        {
            if (_terrainHiZVisibilityScratch[i] != 0u)
            {
                selected[write++] = selected[i];
            }
        }

        if (write < selected.Count)
        {
            selected.RemoveRange(write, selected.Count - write);
        }
    }
}
