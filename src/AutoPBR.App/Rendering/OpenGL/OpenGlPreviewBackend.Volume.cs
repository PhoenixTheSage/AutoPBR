using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private GlShaderProgram? _volumeInjectProgram;
    private GlShaderProgram? _volumeInjectComputeProgram;
    private GlShaderProgram? _volumeIntegrateProgram;
    private bool _volumeUseLiteShaders;
    private bool _volumeComputeInjectCompileDisabled;
    private string? _volumeInitFailureDetail;
    private string? _godRayInitFailureDetail;
    private GlVolumeFroxelTarget? _volumeFroxelTarget;
    private GlVolumeFroxelTarget? _volumeFroxelHistory;
    private GlColorRenderTarget? _volumeIntegrateHistory;
    private float _volumeJitter;
    private Matrix4x4 _volumePrevViewProj = Matrix4x4.Identity;
    private bool _volumeIntegrateHistoryValid;
    private bool _volumeFroxelHistoryValid;
    private Vector3 _volumePrevEye;
    private Vector3 _volumePrevCamRight;
    private Vector3 _volumePrevCamUp;
    private Vector3 _volumePrevCamForward;
    private Vector3 _volumePrevHalfExtent;
    private int _volumeHistoryHalfW;
    private int _volumeHistoryHalfH;
    private bool _loggedSharedCloudTransmittance;
    private bool _volumeSharedCloudSignalInitialized;
    private bool _volumePreviousSharedCloudSignal;

    private const float VolumeHeightFogScale = 0.42f;
    private const uint VolumeFroxelImageUnit = 0;
    private const uint VolumeFroxelOccupancyImageUnit = 1;
    private const uint ShaderImageAccessBarrierBit = 0x00000020;
    private const uint TextureFetchBarrierBit = 0x00000008;
    private const uint VolumeComputeLocalSizeX = 8;
    private const uint VolumeComputeLocalSizeY = 8;

    private static float ResolveCloudLayerWorldBase(in PreviewRenderSettingsSnapshot settings) =>
        PreviewStageConstants.CloudLayerBaseWorldY(settings.CloudLayerHeight);

    private void TryInitVolume(GL gl, bool useOpenGlEs)
    {
        DestroyVolumeResources();
        _volumeUseLiteShaders = false;
        _volumeComputeInjectCompileDisabled = false;
        _volumeInitFailureDetail = null;
        if (TryLoadVolumePrograms(gl, useOpenGlEs, lite: false))
        {
            _lastPostPassAppliedSettingsRevision = -1;
            EmitDiagnostic("[3D preview] Volume shaders ready (gles-pack rev 29 (froxel march), full path).");
            return;
        }

        EmitDiagnostic("[3D preview] Full volume shaders failed; trying lite god-ray path.");
        DestroyVolumeResources();
        _volumeUseLiteShaders = true;
        if (TryLoadVolumePrograms(gl, useOpenGlEs, lite: true))
        {
            _lastPostPassAppliedSettingsRevision = -1;
            EmitDiagnostic("[3D preview] Volume lite god-ray path ready (gles-pack rev 29 (froxel march)).");
            return;
        }

        EmitDiagnostic("[3D preview] Volume lite shaders failed; froxel god rays disabled.");
        if (!string.IsNullOrWhiteSpace(_volumeInitFailureDetail))
        {
            EmitDiagnostic("[3D preview] Volume shader init detail: " + _volumeInitFailureDetail);
        }

        DestroyVolumeResources();
        _volumeUseLiteShaders = false;
    }

    private static string TrimShaderDiagnostic(string? error, int maxLen = 360)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "link failed";
        }

        var oneLine = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (oneLine.Contains("  ", StringComparison.Ordinal))
        {
            oneLine = oneLine.Replace("  ", " ", StringComparison.Ordinal);
        }

        return oneLine.Length <= maxLen ? oneLine : oneLine[..maxLen] + "...";
    }

    private void RecordVolumeShaderFailure(bool lite, string stage, string? error)
    {
        var label = lite ? "lite" : "full";
        var entry = $"{label} {stage}: {TrimShaderDiagnostic(error)}";
        _volumeInitFailureDetail = string.IsNullOrEmpty(_volumeInitFailureDetail)
            ? entry
            : _volumeInitFailureDetail + " | " + entry;
    }

    private bool TryLoadVolumePrograms(GL gl, bool useOpenGlEs, bool lite)
    {
        _volumeFroxelTarget = new GlVolumeFroxelTarget(gl, useOpenGlEs);
        _volumeFroxelHistory = new GlVolumeFroxelTarget(gl, useOpenGlEs);
        _volumeIntegrateHistory = new GlColorRenderTarget(gl, useOpenGlEs);

        var injectFile = lite ? "genesis_volume_inject_lite.frag" : "genesis_volume_inject.frag";
        var integrateFile = lite ? "genesis_volume_integrate_lite.frag" : "genesis_volume_integrate.frag";

        _volumeInjectProgram = CreatePreviewProgram("genesis_godrays.vert", injectFile, out var injectErr,
            lite ? "volume-inject-lite" : "volume-inject-full");
        if (_volumeInjectProgram is not { IsValid: true })
        {
            RecordVolumeShaderFailure(lite, "inject", injectErr);
            EmitDiagnostic("[3D preview] Volume inject shader: " + TrimShaderDiagnostic(injectErr));
            return false;
        }

        _volumeInjectUniformLocs = ResolveVolumeInjectUniformLocs(_volumeInjectProgram);
        TryLoadVolumeComputeInject(lite);

        var integrateDefines = BuildVolumeIntegrateDefines(useOpenGlEs, temporal: !_settings.GodRayStabilizeDebug);
        _volumeIntegrateProgram = CreatePreviewProgram("genesis_godrays.vert", integrateFile, out var intErr,
            lite ? "volume-integrate-lite" : "volume-integrate-full",
            integrateDefines);
        if (_volumeIntegrateProgram is not { IsValid: true })
        {
            RecordVolumeShaderFailure(lite, "integrate", intErr);
            EmitDiagnostic("[3D preview] Volume integrate shader: " + TrimShaderDiagnostic(intErr));
            return false;
        }

        _volumeIntegrateUniformLocs = ResolveVolumeIntegrateUniformLocs(_volumeIntegrateProgram);

        return true;
    }

    private void TryLoadVolumeComputeInject(bool lite)
    {
        if (lite || _volumeComputeInjectCompileDisabled || _glCapabilities?.CanUseComputeFroxelInject != true)
        {
            return;
        }

        _volumeInjectComputeProgram = CreatePreviewComputeProgram(
            "genesis_volume_inject.comp",
            out var computeErr,
            "volume-inject-compute");
        if (_volumeInjectComputeProgram is not { IsValid: true })
        {
            _volumeComputeInjectCompileDisabled = true;
            EmitDiagnostic("[3D preview] Compute froxel inject unavailable; using fragment slice path. " +
                           TrimShaderDiagnostic(computeErr));
            _volumeInjectComputeProgram?.Dispose();
            _volumeInjectComputeProgram = null;
            return;
        }

        _volumeInjectComputeUniformLocs = ResolveVolumeInjectComputeUniformLocs(_volumeInjectComputeProgram);
        EmitDiagnostic("[3D preview] Compute froxel inject ready (desktop GL image-store path).");
    }

    private void DestroyVolumeResources()
    {
        _volumeFroxelTarget?.Dispose();
        _volumeFroxelTarget = null;
        _volumeFroxelHistory?.Dispose();
        _volumeFroxelHistory = null;
        _volumeIntegrateHistory?.Dispose();
        _volumeIntegrateHistory = null;
        _volumeInjectComputeProgram?.Dispose();
        _volumeInjectComputeProgram = null;
        _volumeInjectProgram?.Dispose();
        _volumeInjectProgram = null;
        _volumeIntegrateProgram?.Dispose();
        _volumeIntegrateProgram = null;
        _volumeIntegrateHistoryValid = false;
        _volumeFroxelHistoryValid = false;
        _volumeUseLiteShaders = false;
        _loggedSharedCloudTransmittance = false;
        _volumeSharedCloudSignalInitialized = false;
    }

    private bool CanUseVolumeGodRays(in PreviewRenderSettingsSnapshot settings) =>
        settings is { EnableVolumeGodRays: true, EnableGodRays: true } &&
        _volumeFroxelTarget is not null &&
        _volumeInjectProgram is { IsValid: true } &&
        _volumeIntegrateProgram is { IsValid: true } &&
        _sceneCapture is { IsValid: true } &&
        _godRayQuadVao != 0;

    private static Vector3 ComputeFroxelHalfExtent(float fovRadians, float aspect, float forwardDistance)
    {
        // Match the view frustum lateral extent at the far depth of the froxel box so integrate
        // does not clip god rays at a visible vertical/horizontal seam (was 0.52/0.62).
        var halfY = MathF.Tan(fovRadians * 0.5f) * forwardDistance * 1.05f;
        var halfX = halfY * aspect * 1.05f;
        return new Vector3(halfX, halfY, forwardDistance * 0.5f);
    }

    private static void ComputeCameraBasis(Vector3 eye, Vector3 lookTarget, out Vector3 right, out Vector3 up, out Vector3 forward)
    {
        forward = lookTarget - eye;
        if (forward.LengthSquared() < 1e-12f)
        {
            forward = -Vector3.UnitZ;
        }
        else
        {
            forward = Vector3.Normalize(forward);
        }

        right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() < 1e-10f)
        {
            right = Vector3.Cross(forward, Vector3.UnitZ);
        }

        right = Vector3.Normalize(right);
        up = Vector3.Normalize(Vector3.Cross(right, forward));
    }

    private static Vector3 ResolveVolumeHalfExtent(ref GlRenderFrame frame)
    {
        var cam = frame.Scene.Camera;
        var fovRad = cam.FieldOfViewDegrees * (MathF.PI / 180f);
        var aspect = frame.Vw / (float)Math.Max(frame.Vh, 1);
        var layerBase = ResolveCloudLayerWorldBase(frame.Settings);
        ComputeCameraBasis(frame.Eye, frame.LookTarget, out _, out _, out var camForward);

        // World-anchor froxel depth to the cloud slab instead of a fixed camera-relative distance.
        var cloudTop = layerBase + frame.Settings.CloudVolumeHeight;
        var verticalSpan = Math.Max(cloudTop - frame.Eye.Y, 12f);
        var forwardDist = Math.Clamp(verticalSpan / Math.Max(camForward.Y, 0.12f), 28f, 96f);
        return ComputeFroxelHalfExtent(fovRad, aspect, forwardDist);
    }

    private static float ResolveVolumeHeightFogStrength(in PreviewRenderSettingsSnapshot settings) =>
        settings.EnableAtmosphericSky ? settings.AerialFogStrength * VolumeHeightFogScale : 0f;

    private float ResolveFroxelCloudDensity(in PreviewRenderSettingsSnapshot settings)
    {
        if (!settings.EnableVolumetricClouds)
        {
            return 0f;
        }

        return ResolveSharedCloudTransmittanceTarget(settings) is not null ? 0f : settings.CloudDensity;
    }

    private static IReadOnlyDictionary<string, int>? BuildVolumeIntegrateDefines(bool useOpenGlEs, bool temporal)
    {
        Dictionary<string, int>? defines = null;
        if (useOpenGlEs)
        {
            defines = new Dictionary<string, int> { ["GENESIS_VOLUME_MEDIUMP_ACCUM"] = 1 };
        }

        if (temporal)
        {
            defines ??= new Dictionary<string, int>();
            defines["GENESIS_VOLUME_TEMPORAL"] = 1;
        }

        return defines;
    }

    private bool InjectVolumeFroxels(ref GlRenderFrame frame, Vector3 halfExtent, int froxelW, int froxelH, int froxelSlices)
    {
        if (_volumeFroxelTarget is null || _volumeInjectProgram is null)
        {
            return false;
        }

        if (!_volumeFroxelTarget.EnsureSize(froxelW, froxelH, froxelSlices))
        {
            return false;
        }

        _volumeFroxelHistory?.EnsureSize(froxelW, froxelH, froxelSlices);

        var gl = frame.Gl;
        while (gl.GetError() != GLEnum.NoError)
        {
        }

        var injectSw = frame.Settings.LogVolumetricTiming ? Stopwatch.StartNew() : null;
        ComputeCameraBasis(frame.Eye, frame.LookTarget, out var camRight, out var camUp, out var camForward);

        var shadowAvailable = frame.ShadowAvailable && _shadowTarget is not null;
        var shadowFarRes = _shadowTarget?.Resolution ?? Math.Clamp(frame.Settings.ShadowMapResolution, 256, 4096);
        var cascadesActivePreview = shadowAvailable && frame.ShadowCascadesActive;
        var shadowNearRes = cascadesActivePreview
            ? (_shadowTargetCascadeNear?.Resolution ?? shadowFarRes)
            : shadowFarRes;
        var shadowMidRes = cascadesActivePreview
            ? (_shadowTargetCascadeMid?.Resolution ?? shadowFarRes)
            : shadowFarRes;
        var shadowTexelSize = new Vector2(1f / shadowFarRes, 1f / shadowFarRes);
        var shadowTexelSizeNear = new Vector2(1f / shadowNearRes, 1f / shadowNearRes);
        var shadowTexelSizeMid = new Vector2(1f / shadowMidRes, 1f / shadowMidRes);

        if (TryInjectVolumeFroxelsCompute(
                ref frame,
                halfExtent,
                camRight,
                camUp,
                camForward,
                shadowAvailable,
                shadowTexelSize,
                shadowTexelSizeNear,
                shadowTexelSizeMid))
        {
            injectSw?.Stop();
            if (injectSw is not null)
            {
                frame.LastVolumeInjectMs = injectSw.Elapsed.TotalMilliseconds;
            }

            return true;
        }

        _volumeInjectProgram.Use();
        var vi = _volumeInjectUniformLocs;
        SetFloatOnProgramLoc(_volumeInjectProgram, vi.CloudDensity, ResolveFroxelCloudDensity(frame.Settings));
        SetVec3OnProgramLoc(_volumeInjectProgram, vi.CameraPos, frame.Eye);
        SetVec3OnProgramLoc(_volumeInjectProgram, vi.CamRight, camRight);
        SetVec3OnProgramLoc(_volumeInjectProgram, vi.CamUp, camUp);
        SetVec3OnProgramLoc(_volumeInjectProgram, vi.CamForward, camForward);
        SetVec3OnProgramLoc(_volumeInjectProgram, vi.LightDir, frame.LightDir);
        SetVec3OnProgramLoc(_volumeInjectProgram, vi.LightColor, frame.Scene.Light.Color);
        SetVec3OnProgramLoc(_volumeInjectProgram, vi.HalfExtent, halfExtent);
        SetIntOnProgramLoc(_volumeInjectProgram, vi.SliceCount, _volumeFroxelTarget.Slices);
        if (!_volumeUseLiteShaders)
        {
            SetMatrixOnProgramLoc(_volumeInjectProgram, vi.LightViewProj, frame.ShadowVp);
            SetMatrixOnProgramLoc(_volumeInjectProgram, vi.LightViewProjNear, frame.ShadowVpNear);
            SetMatrixOnProgramLoc(_volumeInjectProgram, vi.LightViewProjMid, frame.ShadowVpMid);
            SetVec2OnProgramLoc(_volumeInjectProgram, vi.ShadowTexelSize, shadowTexelSize);
            SetVec2OnProgramLoc(_volumeInjectProgram, vi.ShadowTexelSizeNear, shadowTexelSizeNear);
            SetVec2OnProgramLoc(_volumeInjectProgram, vi.ShadowTexelSizeMid, shadowTexelSizeMid);
            var cascadesActive = shadowAvailable && frame.ShadowCascadesActive;
            SetIntOnProgramLoc(_volumeInjectProgram, vi.EnableShadowMap, shadowAvailable ? 1 : 0);
            SetIntOnProgramLoc(_volumeInjectProgram, vi.EnableShadowCascades, cascadesActive ? 1 : 0);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.CascadeSplitDistance, frame.CascadeSplitWorldDistance);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.CascadeMidSplitDistance, frame.CascadeMidSplitWorldDistance);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.CascadeBlendWidth, frame.CascadeBlendWorldWidth);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.ShadowDistance, frame.ShadowDistance);
            SetFloatOnProgramLoc(_volumeInjectProgram, vi.ShadowFadeStart, frame.ShadowFadeStart);
            gl.ActiveTexture(TextureUnit.Texture0);
            if (_shadowTarget is not null)
            {
                gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
            }
            else if (_sceneCapture is not null)
            {
                gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
            }

            SetIntOnProgramLoc(_volumeInjectProgram, vi.ShadowMap, 0);
            gl.ActiveTexture(TextureUnit.Texture1);
            if (cascadesActive && _shadowTargetCascadeNear is not null)
            {
                gl.BindTexture(TextureTarget.Texture2D, _shadowTargetCascadeNear.DepthTextureHandle);
            }
            else if (_shadowTarget is not null)
            {
                gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
            }
            else if (_sceneCapture is not null)
            {
                gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
            }

            SetIntOnProgramLoc(_volumeInjectProgram, vi.ShadowMapNear, 1);
            gl.ActiveTexture(TextureUnit.Texture2);
            if (cascadesActive && _shadowTargetCascadeMid is not null)
            {
                gl.BindTexture(TextureTarget.Texture2D, _shadowTargetCascadeMid.DepthTextureHandle);
            }
            else if (_shadowTarget is not null)
            {
                gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
            }
            else if (_sceneCapture is not null)
            {
                gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
            }

            SetIntOnProgramLoc(_volumeInjectProgram, vi.ShadowMapMid, 2);
        }

        gl.BindVertexArray(_godRayQuadVao);
        for (var layer = 0; layer < _volumeFroxelTarget.Slices; layer++)
        {
            if (!_volumeFroxelTarget.BindDrawLayer(layer))
            {
                _volumeFroxelTarget.Unbind();
                return false;
            }

            gl.Clear(ClearBufferMask.ColorBufferBit);
            SetIntOnProgramLoc(_volumeInjectProgram, vi.SliceIndex, layer);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        _volumeFroxelTarget.Unbind();
        injectSw?.Stop();
        if (injectSw is not null)
        {
            frame.LastVolumeInjectMs = injectSw.Elapsed.TotalMilliseconds;
        }

        return true;
    }

    private bool TryInjectVolumeFroxelsCompute(
        ref GlRenderFrame frame,
        Vector3 halfExtent,
        Vector3 camRight,
        Vector3 camUp,
        Vector3 camForward,
        bool shadowAvailable,
        Vector2 shadowTexelSize,
        Vector2 shadowTexelSizeNear,
        Vector2 shadowTexelSizeMid)
    {
        if (_volumeUseLiteShaders ||
            _glCapabilities?.CanUseComputeFroxelInject != true ||
            _volumeInjectComputeProgram is not { IsValid: true } ||
            _volumeFroxelTarget is not { IsValid: true })
        {
            return false;
        }

        if (!_volumeFroxelTarget.BindImagesForCompute(VolumeFroxelImageUnit, VolumeFroxelOccupancyImageUnit))
        {
            return false;
        }

        var gl = frame.Gl;
        _volumeInjectComputeProgram.Use();
        var vci = _volumeInjectComputeUniformLocs;
        SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.CloudDensity,
            ResolveFroxelCloudDensity(frame.Settings));
        SetVec3OnProgramLoc(_volumeInjectComputeProgram, vci.CameraPos, frame.Eye);
        SetVec3OnProgramLoc(_volumeInjectComputeProgram, vci.CamRight, camRight);
        SetVec3OnProgramLoc(_volumeInjectComputeProgram, vci.CamUp, camUp);
        SetVec3OnProgramLoc(_volumeInjectComputeProgram, vci.CamForward, camForward);
        SetVec3OnProgramLoc(_volumeInjectComputeProgram, vci.LightDir, frame.LightDir);
        SetVec3OnProgramLoc(_volumeInjectComputeProgram, vci.LightColor, frame.Scene.Light.Color);
        SetVec3OnProgramLoc(_volumeInjectComputeProgram, vci.HalfExtent, halfExtent);
        SetInt3OnProgramLoc(_volumeInjectComputeProgram, vci.FroxelSize,
            _volumeFroxelTarget.Width, _volumeFroxelTarget.Height, _volumeFroxelTarget.Slices);
        SetIntOnProgramLoc(_volumeInjectComputeProgram, vci.SliceCount, _volumeFroxelTarget.Slices);

        SetMatrixOnProgramLoc(_volumeInjectComputeProgram, vci.LightViewProj, frame.ShadowVp);
        SetMatrixOnProgramLoc(_volumeInjectComputeProgram, vci.LightViewProjNear, frame.ShadowVpNear);
        SetMatrixOnProgramLoc(_volumeInjectComputeProgram, vci.LightViewProjMid, frame.ShadowVpMid);
        SetVec2OnProgramLoc(_volumeInjectComputeProgram, vci.ShadowTexelSize, shadowTexelSize);
        SetVec2OnProgramLoc(_volumeInjectComputeProgram, vci.ShadowTexelSizeNear, shadowTexelSizeNear);
        SetVec2OnProgramLoc(_volumeInjectComputeProgram, vci.ShadowTexelSizeMid, shadowTexelSizeMid);
        var cascadesActive = shadowAvailable && frame.ShadowCascadesActive;
        SetIntOnProgramLoc(_volumeInjectComputeProgram, vci.EnableShadowMap, shadowAvailable ? 1 : 0);
        SetIntOnProgramLoc(_volumeInjectComputeProgram, vci.EnableShadowCascades, cascadesActive ? 1 : 0);
        SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.CascadeSplitDistance, frame.CascadeSplitWorldDistance);
        SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.CascadeMidSplitDistance, frame.CascadeMidSplitWorldDistance);
        SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.CascadeBlendWidth, frame.CascadeBlendWorldWidth);
        SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.ShadowDistance, frame.ShadowDistance);
        SetFloatOnProgramLoc(_volumeInjectComputeProgram, vci.ShadowFadeStart, frame.ShadowFadeStart);

        gl.ActiveTexture(TextureUnit.Texture0);
        if (_shadowTarget is not null)
        {
            gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
        }
        else if (_sceneCapture is not null)
        {
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
        }

        SetIntOnProgramLoc(_volumeInjectComputeProgram, vci.ShadowMap, 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        if (cascadesActive && _shadowTargetCascadeNear is not null)
        {
            gl.BindTexture(TextureTarget.Texture2D, _shadowTargetCascadeNear.DepthTextureHandle);
        }
        else if (_shadowTarget is not null)
        {
            gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
        }
        else if (_sceneCapture is not null)
        {
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
        }

        SetIntOnProgramLoc(_volumeInjectComputeProgram, vci.ShadowMapNear, 1);
        gl.ActiveTexture(TextureUnit.Texture2);
        if (cascadesActive && _shadowTargetCascadeMid is not null)
        {
            gl.BindTexture(TextureTarget.Texture2D, _shadowTargetCascadeMid.DepthTextureHandle);
        }
        else if (_shadowTarget is not null)
        {
            gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
        }
        else if (_sceneCapture is not null)
        {
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
        }

        SetIntOnProgramLoc(_volumeInjectComputeProgram, vci.ShadowMapMid, 2);

        var groupsX = (uint)((_volumeFroxelTarget.Width + (int)VolumeComputeLocalSizeX - 1) / (int)VolumeComputeLocalSizeX);
        var groupsY = (uint)((_volumeFroxelTarget.Height + (int)VolumeComputeLocalSizeY - 1) / (int)VolumeComputeLocalSizeY);
        var groupsZ = (uint)_volumeFroxelTarget.Slices;
        gl.DispatchCompute(groupsX, groupsY, groupsZ);
        gl.MemoryBarrier(ShaderImageAccessBarrierBit | TextureFetchBarrierBit);
        return true;
    }

    private string DescribeVolumeGodRayUnavailableReason(in PreviewRenderSettingsSnapshot settings)
    {
        if (!settings.EnableGodRays)
        {
            return "[3D preview] Froxel god rays disabled in settings.";
        }

        if (!settings.EnableVolumeGodRays)
        {
            return "[3D preview] Volume god-ray path disabled in settings.";
        }

        if (_volumeFroxelTarget is null || _volumeInjectProgram is not { IsValid: true } ||
            _volumeIntegrateProgram is not { IsValid: true })
        {
            var msg = "[3D preview] Froxel god rays unavailable; volume shaders or froxel target failed to init.";
            if (!string.IsNullOrWhiteSpace(_volumeInitFailureDetail))
            {
                msg += " " + _volumeInitFailureDetail;
            }

            return msg;
        }

        if (_sceneCapture is not { IsValid: true })
        {
            var msg = "[3D preview] Froxel god rays unavailable; scene capture depth target is invalid.";
            if (!string.IsNullOrWhiteSpace(_godRayInitFailureDetail))
            {
                msg += " " + _godRayInitFailureDetail;
            }

            return msg;
        }

        if (_godRayQuadVao == 0)
        {
            return "[3D preview] Froxel god rays unavailable; god-ray fullscreen quad missing.";
        }

        return _useOpenGlEs
            ? "[3D preview] Froxel god rays unavailable (OpenGL ES); check volume inject/integrate init."
            : "[3D preview] Froxel god rays unavailable; enable volume path or check GPU init.";
    }

    private void BindVolumeIntegrateUniforms(
        GlRenderFrame frame,
        Vector3 halfExtent,
        float strength,
        float jitter)
    {
        if (_volumeIntegrateProgram is null || _volumeFroxelTarget is null || _sceneCapture is null)
        {
            return;
        }

        var gl = frame.Gl;
        var viewProj = frame.Proj * frame.View;
        Matrix4x4.Invert(viewProj, out var invViewProj);
        ComputeCameraBasis(frame.Eye, frame.LookTarget, out var camRight, out var camUp, out var camForward);
        var iu = _volumeIntegrateUniformLocs;

        if (_volumeUseLiteShaders)
        {
            _volumeIntegrateProgram.Use();
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2DArray, _volumeFroxelTarget.ArrayTextureHandle);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.FroxelVolume, 0);
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.SceneDepth, 1);
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2DArray, _volumeFroxelTarget.OccupancyTextureHandle);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.FroxelOccupancy, 2);
            SetMatrixOnProgramLoc(_volumeIntegrateProgram, iu.InvViewProj, invViewProj);
            SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.CameraPos, frame.Eye);
            SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.CamRight, camRight);
            SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.CamUp, camUp);
            SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.CamForward, camForward);
            SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.LightDir, frame.LightDir);
            SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.HalfExtent, halfExtent);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.SliceCount, _volumeFroxelTarget.Slices);
            SetVec2OnProgramLoc(_volumeIntegrateProgram, iu.FroxelTexelSize,
                new Vector2(1f / _volumeFroxelTarget.Width, 1f / _volumeFroxelTarget.Height));
            SetFloatOnProgramLoc(_volumeIntegrateProgram, iu.Strength, strength);
            SetFloatOnProgramLoc(_volumeIntegrateProgram, iu.Jitter, jitter);
            BindSharedCloudTransmittance(frame, _volumeIntegrateProgram, iu);
            return;
        }

        var halfW = Math.Max(1, frame.Vw / 2);
        var halfH = Math.Max(1, frame.Vh / 2);
        if (_volumeHistoryHalfW != halfW || _volumeHistoryHalfH != halfH)
        {
            _volumeIntegrateHistoryValid = false;
            _volumeHistoryHalfW = halfW;
            _volumeHistoryHalfH = halfH;
        }

        _volumeIntegrateProgram.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2DArray, _volumeFroxelTarget.ArrayTextureHandle);
        SetIntOnProgramLoc(_volumeIntegrateProgram, iu.FroxelVolume, 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
        SetIntOnProgramLoc(_volumeIntegrateProgram, iu.SceneDepth, 1);
        var stabilize = frame.Settings.GodRayStabilizeDebug;
        gl.ActiveTexture(TextureUnit.Texture2);
        if (!stabilize && _volumeIntegrateHistory is not null && _volumeIntegrateHistoryValid)
        {
            gl.BindTexture(TextureTarget.Texture2D, _volumeIntegrateHistory.ColorTextureHandle);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.PrevIntegrate, 2);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.HasPrevIntegrate, 1);
        }
        else
        {
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.PrevIntegrate, 2);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.HasPrevIntegrate, 0);
        }

        gl.ActiveTexture(TextureUnit.Texture3);
        if (!stabilize && _volumeFroxelHistory is not null && _volumeFroxelHistoryValid && _volumeFroxelHistory.IsValid)
        {
            gl.BindTexture(TextureTarget.Texture2DArray, _volumeFroxelHistory.ArrayTextureHandle);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.PrevFroxelVolume, 3);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.HasPrevFroxel, 1);
        }
        else
        {
            gl.BindTexture(TextureTarget.Texture2DArray, _volumeFroxelTarget.ArrayTextureHandle);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.PrevFroxelVolume, 3);
            SetIntOnProgramLoc(_volumeIntegrateProgram, iu.HasPrevFroxel, 0);
        }

        gl.ActiveTexture(TextureUnit.Texture4);
        gl.BindTexture(TextureTarget.Texture2DArray, _volumeFroxelTarget.OccupancyTextureHandle);
        SetIntOnProgramLoc(_volumeIntegrateProgram, iu.FroxelOccupancy, 4);
        BindSharedCloudTransmittance(frame, _volumeIntegrateProgram, iu);

        SetMatrixOnProgramLoc(_volumeIntegrateProgram, iu.InvViewProj, invViewProj);
        SetMatrixOnProgramLoc(_volumeIntegrateProgram, iu.PrevViewProj, _volumePrevViewProj);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.CameraPos, frame.Eye);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.PrevCameraPos, _volumePrevEye);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.CamRight, camRight);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.CamUp, camUp);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.CamForward, camForward);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.PrevCamRight, _volumePrevCamRight);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.PrevCamUp, _volumePrevCamUp);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.PrevCamForward, _volumePrevCamForward);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.LightDir, frame.LightDir);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.HalfExtent, halfExtent);
        SetVec3OnProgramLoc(_volumeIntegrateProgram, iu.PrevHalfExtent, _volumePrevHalfExtent);
        SetIntOnProgramLoc(_volumeIntegrateProgram, iu.SliceCount, _volumeFroxelTarget.Slices);
        SetVec2OnProgramLoc(_volumeIntegrateProgram, iu.FroxelTexelSize,
            new Vector2(1f / _volumeFroxelTarget.Width, 1f / _volumeFroxelTarget.Height));
        SetFloatOnProgramLoc(_volumeIntegrateProgram, iu.Strength, strength);
        SetFloatOnProgramLoc(_volumeIntegrateProgram, iu.Jitter, jitter);
        var quality = PreviewVolumetricQuality.Resolve(frame.Settings.VolumetricQuality);
        SetFloatOnProgramLoc(_volumeIntegrateProgram, iu.TemporalWeight,
            stabilize ? 0f : PreviewVolumetricQuality.EffectivePassTemporalWeight(
                quality.VolumeIntegrateTemporalWeight, frame.Settings));
        SetFloatOnProgramLoc(_volumeIntegrateProgram, iu.FroxelTemporalWeight,
            stabilize ? 0f : PreviewVolumetricQuality.EffectivePassTemporalWeight(
                quality.FroxelTemporal3DWeight, frame.Settings));
    }

    private void BindSharedCloudTransmittance(
        GlRenderFrame frame,
        GlShaderProgram program,
        VolumeIntegrateUniformLocs uniforms)
    {
        var gl = frame.Gl;
        var sharedClouds = ResolveSharedCloudTransmittanceTarget(frame.Settings);
        var fallbackTexture = _sceneCapture?.DepthTextureHandle ?? 0;
        gl.ActiveTexture(TextureUnit.Texture5);
        gl.BindTexture(TextureTarget.Texture2D, sharedClouds?.ColorTextureHandle ?? fallbackTexture);
        SetIntOnProgramLoc(program, uniforms.CloudTransmittance, 5);
        gl.ActiveTexture(TextureUnit.Texture6);
        gl.BindTexture(TextureTarget.Texture2D, sharedClouds?.DataTextureHandle ?? fallbackTexture);
        SetIntOnProgramLoc(program, uniforms.CloudData, 6);
        SetIntOnProgramLoc(program, uniforms.HasCloudTransmittance, sharedClouds is not null ? 1 : 0);

        if (sharedClouds is not null && !_loggedSharedCloudTransmittance)
        {
            _loggedSharedCloudTransmittance = true;
            EmitDiagnostic(
                "[3D preview] Froxel integration using detailed cloud transmittance/depth; " +
                "analytic slab cloud injection disabled.");
        }
    }

    private void CommitFroxelHistory(
        Vector3 eye,
        Vector3 halfExtent,
        Vector3 camRight,
        Vector3 camUp,
        Vector3 camForward)
    {
        if (_volumeFroxelHistory is null || _volumeFroxelTarget is null)
        {
            return;
        }

        (_volumeFroxelTarget, _volumeFroxelHistory) = (_volumeFroxelHistory, _volumeFroxelTarget);
        _volumePrevEye = eye;
        _volumePrevHalfExtent = halfExtent;
        _volumePrevCamRight = camRight;
        _volumePrevCamUp = camUp;
        _volumePrevCamForward = camForward;
        _volumeFroxelHistoryValid = true;
    }

    private bool TryIntegrateVolumeGodRaysToHalfRes(ref GlRenderFrame frame, Vector3 halfExtent, float marchJitter)
    {
        if (_godRayHalfResTarget is null || _volumeIntegrateHistory is null ||
            _sceneCapture is not { IsValid: true } || _volumeIntegrateProgram is not { IsValid: true })
        {
            return false;
        }

        var halfW = Math.Max(1, frame.Vw / 2);
        var halfH = Math.Max(1, frame.Vh / 2);
        if (!_volumeIntegrateHistory.EnsureSize(halfW, halfH))
        {
            return false;
        }

        _godRayHalfResTarget.BindDraw();
        frame.Gl.Clear(ClearBufferMask.ColorBufferBit);
        var integrateSw = frame.Settings.LogVolumetricTiming ? Stopwatch.StartNew() : null;
        BindVolumeIntegrateUniforms(frame, halfExtent, frame.Settings.GodRayStrength, marchJitter);
        frame.Gl.BindVertexArray(_godRayQuadVao);
        frame.Gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        integrateSw?.Stop();
        if (integrateSw is not null)
        {
            frame.LastVolumeIntegrateMs = integrateSw.Elapsed.TotalMilliseconds;
        }

        if (!frame.Settings.GodRayStabilizeDebug)
        {
            _volumeIntegrateHistory.CopyColorFrom(_godRayHalfResTarget);
            _volumePrevViewProj = frame.Proj * frame.View;
            _volumeIntegrateHistoryValid = true;
        }

        return _godRayHalfResTarget.IsValid;
    }

    private bool TryRunVolumeGodRayPass(
        ref GlRenderFrame frame,
        out double injectMs,
        out double integrateMs)
    {
        injectMs = 0;
        integrateMs = 0;
        var hasSharedCloudSignal = ResolveSharedCloudTransmittanceTarget(frame.Settings) is not null;
        if (_volumeSharedCloudSignalInitialized &&
            hasSharedCloudSignal != _volumePreviousSharedCloudSignal)
        {
            _volumeIntegrateHistoryValid = false;
            _volumeFroxelHistoryValid = false;
            _godRayHistoryValid = false;
        }

        _volumePreviousSharedCloudSignal = hasSharedCloudSignal;
        _volumeSharedCloudSignalInitialized = true;
        var halfExtent = ResolveVolumeHalfExtent(ref frame);
        var quality = PreviewVolumetricQuality.Resolve(frame.Settings.VolumetricQuality);
        var froxelW = quality.ResolveFroxelWidth(frame.Vw);
        var froxelH = quality.ResolveFroxelHeight(frame.Vh);
        if (!InjectVolumeFroxels(ref frame, halfExtent, froxelW, froxelH, quality.FroxelSlices))
        {
            return false;
        }

        injectMs = frame.LastVolumeInjectMs;
        var marchJitter = frame.Settings.GodRayStabilizeDebug ? 0f : _volumeJitter;
        if (!frame.Settings.GodRayStabilizeDebug)
        {
            _volumeJitter = (_volumeJitter + 0.0618f) % 1f;
        }

        if (!TryIntegrateVolumeGodRaysToHalfRes(ref frame, halfExtent, marchJitter))
        {
            return false;
        }

        integrateMs = frame.LastVolumeIntegrateMs;
        if (!frame.Settings.GodRayStabilizeDebug)
        {
            ComputeCameraBasis(frame.Eye, frame.LookTarget, out var camRight, out var camUp, out var camForward);
            CommitFroxelHistory(frame.Eye, halfExtent, camRight, camUp, camForward);
        }

        return true;
    }
}
