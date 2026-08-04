using System.Diagnostics;
using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private readonly GodRaysPassCoordinator _godRaysPassCoordinator = new();

    private GlShaderProgram? _scenePresentProgram;
    private GlShaderProgram? _screenSpaceGodRayProgram;
    private GlShaderProgram? _shadowAwareGodRayProgram;
    private GlShaderProgram? _godRayUpsampleProgram;
    private GlShaderProgram? _godRayCanopyRefineProgram;
    private GlShaderProgram? _godRayCompositeProgram;
    private GlSceneCaptureTarget? _sceneCapture;
    private GlColorRenderTarget? _godRayHalfResTarget;
    private GlColorRenderTarget? _godRayResolveTarget;
    private GlColorRenderTarget? _godRayHistoryTarget;
    private uint _godRayQuadVao;
    private uint _godRayQuadVbo;
    private int _godRayBlitFailLogged;
    private int _screenSpaceGodRayLogged;
    private int _shadowAwareGodRayLogged;
    private int _godRayCanopyRefineLogged;
    private bool _godRayCanopyRefineInitAttempted;
    private Matrix4x4 _godRayPrevViewProj = Matrix4x4.Identity;
    private bool _godRayHistoryValid;
    private int _godRayHistoryVw;
    private int _godRayHistoryVh;
    private bool _godRayLinearTargetsActive;
    private int _volumePathFailLogged;
    private bool _godRaySparseMarchCompiled;
    private uint _pendingGodRayCompositeTexture;
    private bool _pendingScreenSpaceGodRayLayer;

    private static IReadOnlyDictionary<string, int>? BuildGodRaySparseMarchDefines(bool sparseMarch) =>
        sparseMarch
            ? new Dictionary<string, int> { ["GENESIS_GODRAY_SPARSE_MARCH"] = 1 }
            : null;

    private void ApplyGodRaysPassInvalidation(in GodRaysPassInvalidation invalidation)
    {
        if (invalidation.GodRayHistory)
        {
            _godRayHistoryValid = false;
        }

        if (invalidation.VolumeFroxelHistory)
        {
            _volumeFroxelHistoryValid = false;
        }

        if (invalidation.VolumeIntegrateHistory)
        {
            _volumeIntegrateHistoryValid = false;
        }

        if (invalidation.CloudHistory)
        {
            InvalidateCloudTemporalHistory();
        }

        if (invalidation.TaaHistory)
        {
            _taaHistoryValid = false;
        }

        if (invalidation.ResetGodRayLogs)
        {
            _volumePathFailLogged = 0;
            _screenSpaceGodRayLogged = 0;
            _shadowAwareGodRayLogged = 0;
            _godRayBlitFailLogged = 0;
            _godRayCanopyRefineLogged = 0;
        }

    }

    private void TryInitGodRaysCore(GL gl, bool useOpenGlEs)
    {
        DestroyGodRayResources();
        _godRayInitFailureDetail = null;
        if (!TryInitSceneCaptureCore(gl, useOpenGlEs, out var sceneErr))
        {
            _godRayInitFailureDetail = "scene-capture: " + TrimShaderDiagnostic(sceneErr);
            EmitDiagnostic("[3D preview] Scene capture shader: " + TrimShaderDiagnostic(sceneErr));
            DestroyGodRayResources();
            return;
        }

        _godRayHalfResTarget = new GlColorRenderTarget(gl, useOpenGlEs);
        _godRayResolveTarget = new GlColorRenderTarget(gl, useOpenGlEs);
        _godRayHistoryTarget = new GlColorRenderTarget(gl, useOpenGlEs);

        _godRaySparseMarchCompiled = _settings.GodRaySparseMarch;
        // Dense march for the leaf-gap overlay — sparse steps read as square stair bands.
        _screenSpaceGodRayProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_godrays.frag", out var ssErr,
            defines: null);
        if (_screenSpaceGodRayProgram is not { IsValid: true })
        {
            _godRayInitFailureDetail = "screen-space: " + TrimShaderDiagnostic(ssErr);
            EmitDiagnostic("[3D preview] Screen-space god-ray shader: " + TrimShaderDiagnostic(ssErr));
            DestroyGodRayResources();
            return;
        }

        _screenSpaceGodRayUniformLocs = ResolveScreenSpaceGodRayUniformLocs(_screenSpaceGodRayProgram);

        _godRayUpsampleProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_godrays_upsample.frag", out var upErr);
        if (_godRayUpsampleProgram is not { IsValid: true })
        {
            _godRayInitFailureDetail = "upsample: " + TrimShaderDiagnostic(upErr);
            EmitDiagnostic("[3D preview] God-ray upsample shader: " + TrimShaderDiagnostic(upErr));
            DestroyGodRayResources();
            return;
        }

        _godRayUpsampleUniformLocs = ResolveGodRayUpsampleUniformLocs(_godRayUpsampleProgram);

        _godRayCompositeProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_godrays_composite.frag", out var compErr);
        if (_godRayCompositeProgram is not { IsValid: true })
        {
            _godRayInitFailureDetail = "composite: " + TrimShaderDiagnostic(compErr);
            EmitDiagnostic("[3D preview] God-ray composite shader: " + TrimShaderDiagnostic(compErr));
            DestroyGodRayResources();
            return;
        }

        _godRayCompositeUniformLocs = ResolveGodRayCompositeUniformLocs(_godRayCompositeProgram);

    }

    private bool TryInitSceneCaptureCore(GL gl, bool useOpenGlEs, out string? error)
    {
        error = null;
        _sceneCapture ??= new GlSceneCaptureTarget(gl, useOpenGlEs);
        if (_scenePresentProgram is not { IsValid: true })
        {
            _scenePresentProgram?.Dispose();
            _scenePresentProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_scene_present.frag", out error);
            if (_scenePresentProgram is not { IsValid: true })
            {
                _scenePresentProgram?.Dispose();
                _scenePresentProgram = null;
                return false;
            }

            _scenePresentUniformLocs = ResolveScenePresentUniformLocs(_scenePresentProgram);
        }

        if (_godRayQuadVao == 0 || _godRayQuadVbo == 0)
        {
            CreateSceneFullscreenQuad(gl);
        }

        return _sceneCapture is not null &&
               _scenePresentProgram is { IsValid: true } &&
               _godRayQuadVao != 0;
    }

    private void CreateSceneFullscreenQuad(GL gl)
    {
        if (_godRayQuadVbo != 0)
        {
            gl.DeleteBuffer(_godRayQuadVbo);
            _godRayQuadVbo = 0;
        }

        if (_godRayQuadVao != 0)
        {
            gl.DeleteVertexArray(_godRayQuadVao);
            _godRayQuadVao = 0;
        }

        Span<float> quad =
        [
            -1f, -1f, 1f, -1f, 1f, 1f,
            -1f, -1f, 1f, 1f, -1f, 1f
        ];
        _godRayQuadVao = gl.GenVertexArray();
        _godRayQuadVbo = gl.GenBuffer();
        gl.BindVertexArray(_godRayQuadVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _godRayQuadVbo);
        gl.BufferData<float>(GLEnum.ArrayBuffer, quad, GLEnum.StaticDraw);
        unsafe
        {
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }

        gl.BindVertexArray(0);
    }

    private void DestroyGodRayResources()
    {
        var gl = _gl;
        _sceneCapture?.Dispose();
        _sceneCapture = null;
        _godRayHalfResTarget?.Dispose();
        _godRayHalfResTarget = null;
        _godRayResolveTarget?.Dispose();
        _godRayResolveTarget = null;
        _godRayHistoryTarget?.Dispose();
        _godRayHistoryTarget = null;
        _godRayUpsampleProgram?.Dispose();
        _godRayUpsampleProgram = null;
        _godRayCanopyRefineProgram?.Dispose();
        _godRayCanopyRefineProgram = null;
        _godRayCanopyRefineInitAttempted = false;
        _screenSpaceGodRayProgram?.Dispose();
        _screenSpaceGodRayProgram = null;
        _shadowAwareGodRayProgram?.Dispose();
        _shadowAwareGodRayProgram = null;
        _scenePresentProgram?.Dispose();
        _scenePresentProgram = null;
        _godRayCompositeProgram?.Dispose();
        _godRayCompositeProgram = null;
        _godRayHistoryValid = false;

        if (gl is null)
        {
            _godRayQuadVao = _godRayQuadVbo = 0;
            return;
        }

        if (_godRayQuadVbo != 0)
        {
            gl.DeleteBuffer(_godRayQuadVbo);
            _godRayQuadVbo = 0;
        }

        if (_godRayQuadVao != 0)
        {
            gl.DeleteVertexArray(_godRayQuadVao);
            _godRayQuadVao = 0;
        }
    }

    private bool CanUseGodRayCapture(in PreviewRenderSettingsSnapshot settings) =>
        settings.EnableGodRays &&
        _sceneCapture is not null &&
        _scenePresentProgram is { IsValid: true } &&
        _screenSpaceGodRayProgram is { IsValid: true } &&
        _godRayCompositeProgram is { IsValid: true } &&
        _godRayHalfResTarget is not null &&
        _godRayResolveTarget is not null &&
        _godRayHistoryTarget is not null &&
        _godRayQuadVao != 0;

    private bool CanUseScreenSpaceGodRayCapture(in PreviewRenderSettingsSnapshot settings) =>
        settings.EnableScreenSpaceGodRays &&
        settings.ScreenSpaceGodRayStrength > 1e-5f &&
        _sceneCapture is not null &&
        _scenePresentProgram is { IsValid: true } &&
        (_screenSpaceGodRayProgram is { IsValid: true } || _shadowAwareGodRayProgram is { IsValid: true }) &&
        _godRayQuadVao != 0;

    /// <summary>
    /// Desktop FP targets keep froxel inscatter linear until composite encode (matches cloud CQ1.4).
    /// GLES/ANGLE stays on RGBA8; composite still applies the same present encode.
    /// </summary>
    private bool WantsLinearGodRayTargets() =>
        !_useOpenGlEs && (_glCapabilities?.CanUseFloatingPointCloudTargets ?? true);

    private bool CanUseTaaSceneCapture(in PreviewRenderSettingsSnapshot settings) =>
        IsPreviewTaaActive(settings) &&
        _sceneCapture is not null &&
        _scenePresentProgram is { IsValid: true } &&
        _godRayQuadVao != 0;

    private bool CanUseCloudSceneCapture(in PreviewRenderSettingsSnapshot settings) =>
        settings.EnableVolumetricClouds &&
        _sceneCapture is not null &&
        _scenePresentProgram is { IsValid: true } &&
        _godRayQuadVao != 0;

    private void ResolveSceneCaptureSize(ref GlRenderFrame frame, out int captureW, out int captureH, out float captureScale) =>
        GodRaysPassCoordinator.ResolveSceneCaptureSize(
            in frame,
            s => IsPreviewTaaActive(s),
            s => ResolveEffectivePreviewTaa(s),
            out captureW,
            out captureH,
            out captureScale);

    private void MaybeLogSceneCaptureAaScale(ref GlRenderFrame frame) =>
        _godRaysPassCoordinator.TryLogSceneCaptureAaScale(in frame, EmitDiagnostic);

    private void SyncGodRayToggleState(in PreviewRenderSettingsSnapshot settings) =>
        ApplyGodRaysPassInvalidation(_godRaysPassCoordinator.SyncGodRayToggleState(settings));

    private void SyncVolumetricToggleState(in PreviewRenderSettingsSnapshot settings) =>
        ApplyGodRaysPassInvalidation(_godRaysPassCoordinator.SyncVolumetricToggleState(settings));

    private bool TryBeginGodRaySceneRender(ref GlRenderFrame frame)
    {
        var wantsCapture = CanUseGodRayCapture(frame.Settings) ||
                           CanUseScreenSpaceGodRayCapture(frame.Settings) ||
                           CanUseTaaSceneCapture(frame.Settings) ||
                           CanUseCloudSceneCapture(frame.Settings) ||
                           CanUseAoSceneCapture(frame.Settings);
        if (!wantsCapture)
        {
            return false;
        }

        if (_sceneCapture is null && _gl is not null)
        {
            TryInitSceneCaptureCore(_gl, _useOpenGlEs, out _);
        }

        if (_sceneCapture is null)
        {
            return false;
        }

        ResolveSceneCaptureSize(ref frame, out var captureW, out var captureH, out var captureScale);
        var requireNormals = frame.Settings.EnableScreenSpaceAo && !_aoCapabilityDenied;
        if (!_sceneCapture.EnsureSize(
                captureW,
                captureH,
                frame.Settings.HdrPresentActive,
                requireViewNormals: requireNormals))
        {
            if (requireNormals &&
                _sceneCapture.EnsureSize(captureW, captureH, frame.Settings.HdrPresentActive, requireViewNormals: false))
            {
                if (_aoFailLogged != 3)
                {
                    _aoFailLogged = 3;
                    EmitDiagnostic(
                        "[3D preview] Scene-capture view-normal MRT unavailable; screen-space AO will use depth-derived normals.");
                }
            }
            else
            {
                EmitDiagnostic("[3D preview] Shared scene-capture target incomplete; rendering directly to the default FBO.");
                return false;
            }
        }

        frame.SceneCaptureW = captureW;
        frame.SceneCaptureH = captureH;
        frame.SceneCaptureScale = captureScale;
        _sceneCapture.BindDraw(captureW, captureH);
        MaybeLogSceneCaptureAaScale(ref frame);
        return true;
    }

    private void FinishGodRaySceneRender(ref GlRenderFrame frame)
    {
        if (!frame.GodRayCaptureActive || _sceneCapture is null)
        {
            return;
        }

        MaybeLogScreenSpaceAoInactive(frame.Settings);
        uint aoTexture = 0;
        if (IsScreenSpaceAoActive(frame.Settings))
        {
            using (BeginPassTimerScope(GlGpuTimerScope.Ao))
            {
                aoTexture = DrawScreenSpaceAo(ref frame);
            }
        }

        // Never blit raw capture to an HDR DXGI target — that skips scRGB encode + Y-flip and
        // produces upside-down / flashing over-bright frames.
        var presented = false;
        if (aoTexture != 0)
        {
            presented = TryPresentSceneCaptureWithAo(ref frame, aoTexture);
        }

        if (!presented)
        {
            presented = TryPresentSceneCaptureToDefault(ref frame);
        }

        if (!presented && !frame.Settings.HdrPresentActive)
        {
            presented = _sceneCapture.BlitColorToDefault(
                frame.DefaultFbo, frame.VpX, frame.VpY, frame.Vw, frame.Vh);
        }

        if (!presented)
        {
            var key = frame.Vw + frame.Vh * 10000;
            if (_godRayBlitFailLogged != key)
            {
                _godRayBlitFailLogged = key;
                EmitDiagnostic("[3D preview] God-ray scene present to default FBO failed.");
            }
        }

        BindDefaultFramebuffer(ref frame);
    }

    private void BindDefaultFramebuffer(ref GlRenderFrame frame)
    {
        if (frame.DefaultFbo != 0)
        {
            frame.Gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)frame.DefaultFbo);
        }
        else
        {
            frame.Gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        frame.Gl.Viewport(frame.VpX, frame.VpY, (uint)frame.Vw, (uint)frame.Vh);
        ConfigureDefaultFramebufferColorOutput(frame.Gl, frame.DefaultFbo);
    }

    private void ConfigureDefaultFramebufferColorOutput(GL gl, int defaultFbo)
    {
        var colorTarget = defaultFbo == 0 ? DrawBufferMode.Back : DrawBufferMode.ColorAttachment0;
        if (_useOpenGlEs)
        {
            unsafe
            {
                gl.DrawBuffers(1, &colorTarget);
            }
        }
        else
        {
            gl.DrawBuffer(colorTarget);
        }
    }

    private static void FlushPendingGlErrors(GL gl)
    {
        while (gl.GetError() != GLEnum.NoError)
        {
        }
    }

    private bool TryPresentSceneCaptureToDefault(ref GlRenderFrame frame)
    {
        if (_sceneCapture is null || !_sceneCapture.IsValid || _godRayQuadVao == 0)
        {
            return false;
        }

        if (_scenePresentProgram is not { IsValid: true })
        {
            return _sceneCapture.BlitColorToDefault(frame.DefaultFbo, frame.VpX, frame.VpY, frame.Vw, frame.Vh);
        }

        var gl = frame.Gl;
        BindDefaultFramebuffer(ref frame);

        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorCullFace = gl.IsEnabled(EnableCap.CullFace);
        var priorScissor = gl.IsEnabled(EnableCap.ScissorTest);
        var priorDepthMask = gl.GetBoolean(GetPName.DepthWritemask);
        var priorColorMask = new bool[4];
        gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);

        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.ScissorTest);
        gl.DepthMask(false);
        gl.ColorMask(true, true, true, true);
        FlushPendingGlErrors(gl);
        gl.BindVertexArray(_godRayQuadVao);
        _scenePresentProgram.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.ColorTextureHandle);
        BindScenePresentUniforms(frame.Settings, sceneIsLinear: frame.Settings.HdrPresentActive);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        var err = gl.GetError();
        gl.BindVertexArray(0);

        gl.DepthMask(priorDepthMask);
        gl.ColorMask(priorColorMask[0], priorColorMask[1], priorColorMask[2], priorColorMask[3]);
        if (priorDepthTest)
        {
            gl.Enable(EnableCap.DepthTest);
        }

        if (priorBlend)
        {
            gl.Enable(EnableCap.Blend);
        }

        if (priorCullFace)
        {
            gl.Enable(EnableCap.CullFace);
        }

        if (priorScissor)
        {
            gl.Enable(EnableCap.ScissorTest);
        }

        if (err == GLEnum.NoError)
        {
            return true;
        }

        return _sceneCapture.BlitColorToDefault(frame.DefaultFbo, frame.VpX, frame.VpY, frame.Vw, frame.Vh);
    }

    private void TryCompositeAdditiveRays(ref GlRenderFrame frame, uint raysTexture, uint cloudMaskTexture = 0,
        bool transmittanceComposite = false)
    {
        if (_godRayCompositeProgram is not { IsValid: true } || _godRayQuadVao == 0)
        {
            return;
        }

        var gl = frame.Gl;
        BindDefaultFramebuffer(ref frame);
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        // Transmittance path: dst = inscatter + dst * T  (One, SrcAlpha).
        // Legacy additive shafts: dst = rays * luma + dst  (SrcAlpha, One).
        if (transmittanceComposite)
        {
            gl.BlendFunc(BlendingFactor.One, BlendingFactor.SrcAlpha);
        }
        else
        {
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        }

        gl.BindVertexArray(_godRayQuadVao);
        _godRayCompositeProgram.Use();
        var cu = _godRayCompositeUniformLocs;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, raysTexture);
        SetIntOnProgramLoc(_godRayCompositeProgram, cu.Rays, 0);
        SetIntOnProgramLoc(_godRayCompositeProgram, cu.CloudPresent, 0);
        SetIntOnProgramLoc(_godRayCompositeProgram, cu.HdrPresent, frame.Settings.HdrPresentActive ? 1 : 0);
        SetFloatOnProgramLoc(_godRayCompositeProgram, cu.HdrPaperWhiteNits, frame.Settings.HdrPaperWhiteNits);
        SetFloatOnProgramLoc(_godRayCompositeProgram, cu.HdrPeakNits, frame.Settings.HdrPeakNits);
        SetIntOnProgramLoc(_godRayCompositeProgram, cu.TransmittanceComposite, transmittanceComposite ? 1 : 0);
        var hasCloudMask = cloudMaskTexture != 0;
        SetIntOnProgramLoc(_godRayCompositeProgram, cu.HasCloudMask, hasCloudMask ? 1 : 0);
        if (hasCloudMask)
        {
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, cloudMaskTexture);
            SetIntOnProgramLoc(_godRayCompositeProgram, cu.CloudMask, 1);
        }

        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.BindVertexArray(0);
        if (priorDepthTest)
        {
            gl.Enable(EnableCap.DepthTest);
        }
        else
        {
            gl.Disable(EnableCap.DepthTest);
        }

        if (!priorBlend)
        {
            gl.Disable(EnableCap.Blend);
        }
    }

    private void CompositePendingGodRays(ref GlRenderFrame frame)
    {
        var texture = _pendingGodRayCompositeTexture;
        _pendingGodRayCompositeTexture = 0;
        if (texture != 0)
        {
            // Volume path stores RGB in-scatter + A transmittance.
            TryCompositeAdditiveRays(ref frame, texture, transmittanceComposite: true);
        }

        if (_pendingScreenSpaceGodRayLayer)
        {
            _pendingScreenSpaceGodRayLayer = false;
            TryCompositeScreenSpaceGodRayLayer(ref frame);
        }
    }

    private void TryCompositeScreenSpaceGodRayLayer(ref GlRenderFrame frame)
    {
        if (!frame.Settings.EnableScreenSpaceGodRays ||
            frame.Settings.ScreenSpaceGodRayStrength <= 1e-5f)
        {
            return;
        }

        var strength = frame.Settings.ScreenSpaceGodRayStrength;
        using (BeginPassTimerScope(GlGpuTimerScope.GodRayResolve))
        {
            // Depth-occlusion radial beams only. Shadow-map sampling at sky-reconstructed
            // world positions paints cascade/texel squares into the sky and skews shaft direction.
            TryRunScreenSpaceGodRays(ref frame, strength);
        }

        if (_screenSpaceGodRayLogged == 0)
        {
            _screenSpaceGodRayLogged = 1;
            EmitDiagnostic(
                "[3D preview] Screen-space god-ray layer active (fine beams over froxel volumetric fog).");
        }
    }

    private void TryRunScreenSpaceGodRays(ref GlRenderFrame frame, float? strengthOverride = null)
    {
        if (_screenSpaceGodRayProgram is null || _sceneCapture is null || _godRayQuadVao == 0 ||
            !TryResolveGodRaySunScreenUv(ref frame, out var lightUv, out var discRadiusUv, out var coneRadiusUv))
        {
            return;
        }

        var tod = PreviewGodRayTod.Evaluate(frame.WorldLightDir);
        var strength = (strengthOverride ?? frame.Settings.GodRayStrength) * tod.StrengthScale;
        if (strength <= 1e-5f)
        {
            return;
        }

        var cloudTarget = _cloudCompositeTarget is { IsValid: true }
            ? _cloudCompositeTarget
            : _cloudRenderTarget is { IsValid: true }
                ? _cloudRenderTarget
                : null;
        var hasCloudOpacity = cloudTarget is not null && cloudTarget.ColorTextureHandle != 0;

        var gl = frame.Gl;
        BindDefaultFramebuffer(ref frame);
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        // True additive — shaders output display-referred RGB with A=1 (do not use SrcAlpha,One
        // with A=luma; that squares shaft energy and makes beams invisible).
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        gl.BindVertexArray(_godRayQuadVao);
        _screenSpaceGodRayProgram.Use();
        var ssu = _screenSpaceGodRayUniformLocs;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
        SetIntOnProgramLoc(_screenSpaceGodRayProgram, ssu.SceneDepth, 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        if (hasCloudOpacity)
        {
            gl.BindTexture(TextureTarget.Texture2D, cloudTarget!.ColorTextureHandle);
        }
        else
        {
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
        }

        SetIntOnProgramLoc(_screenSpaceGodRayProgram, ssu.CloudOpacity, 1);
        SetIntOnProgramLoc(_screenSpaceGodRayProgram, ssu.HasCloudOpacity, hasCloudOpacity ? 1 : 0);
        SetVec2OnProgramLoc(_screenSpaceGodRayProgram, ssu.SunUv, lightUv);
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.SunDiscRadius, discRadiusUv);
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.SunConeRadius, coneRadiusUv);
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.Aspect, frame.Vw / (float)Math.Max(frame.Vh, 1));
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.Strength, strength);
        SetVec3OnProgramLoc(_screenSpaceGodRayProgram, ssu.ScatterTint, tod.ScatterTint);
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.SkyWashFloor, tod.SkyWashFloor);
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.TerrainShaftScale, tod.TerrainShaftScale);
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.EnergyKnee, tod.EnergyKnee);
        SetIntOnProgramLoc(_screenSpaceGodRayProgram, ssu.HdrPresent, frame.Settings.HdrPresentActive ? 1 : 0);
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.HdrPaperWhiteNits, frame.Settings.HdrPaperWhiteNits);
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.HdrPeakNits, frame.Settings.HdrPeakNits);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.BindVertexArray(0);
        if (priorDepthTest)
        {
            gl.Enable(EnableCap.DepthTest);
        }
        else
        {
            gl.Disable(EnableCap.DepthTest);
        }

        if (!priorBlend)
        {
            gl.Disable(EnableCap.Blend);
        }
    }

    private bool TryRunShadowAwareGodRays(ref GlRenderFrame frame, float? strengthOverride = null)
    {
        TryEnsureShadowAwareGodRayProgram();
        if (_shadowAwareGodRayProgram is null || _sceneCapture is null || _godRayQuadVao == 0 ||
            !frame.ShadowAvailable || _shadowTarget is null ||
            !TryResolveGodRaySunScreenUv(ref frame, out var lightUv, out var discRadiusUv, out var coneRadiusUv))
        {
            return false;
        }

        var strength = strengthOverride ?? frame.Settings.GodRayStrength;
        if (strength <= 1e-5f)
        {
            return false;
        }

        var viewProj = frame.Proj * frame.View;
        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
        {
            return false;
        }

        var gl = frame.Gl;
        var cascadesActive = frame.ShadowCascadesActive;
        var shadowFarRes = _shadowTarget.Resolution;
        var shadowNearRes = cascadesActive
            ? (_shadowTargetCascadeNear?.Resolution ?? shadowFarRes)
            : shadowFarRes;
        var shadowMidRes = cascadesActive
            ? (_shadowTargetCascadeMid?.Resolution ?? shadowFarRes)
            : shadowFarRes;
        var shadowTexelSize = new Vector2(1f / shadowFarRes, 1f / shadowFarRes);
        var shadowTexelSizeNear = new Vector2(1f / shadowNearRes, 1f / shadowNearRes);
        var shadowTexelSizeMid = new Vector2(1f / shadowMidRes, 1f / shadowMidRes);

        BindDefaultFramebuffer(ref frame);
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        // True additive — see TryRunScreenSpaceGodRays (SrcAlpha×luma squared shafts away).
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        gl.BindVertexArray(_godRayQuadVao);
        _shadowAwareGodRayProgram.Use();
        var shu = _shadowAwareGodRayUniformLocs;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.SceneDepth, 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowMap, 1);
        gl.ActiveTexture(TextureUnit.Texture2);
        if (cascadesActive && _shadowTargetCascadeNear is not null)
        {
            gl.BindTexture(TextureTarget.Texture2D, _shadowTargetCascadeNear.DepthTextureHandle);
        }
        else
        {
            gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
        }

        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowMapNear, 2);
        gl.ActiveTexture(TextureUnit.Texture3);
        if (cascadesActive && _shadowTargetCascadeMid is not null)
        {
            gl.BindTexture(TextureTarget.Texture2D, _shadowTargetCascadeMid.DepthTextureHandle);
        }
        else
        {
            gl.BindTexture(TextureTarget.Texture2D, _shadowTarget.DepthTextureHandle);
        }

        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowMapMid, 3);
        SetMatrixOnProgramLoc(_shadowAwareGodRayProgram, shu.InvViewProj, invViewProj);
        SetMatrixOnProgramLoc(_shadowAwareGodRayProgram, shu.LightViewProj, frame.ShadowVp);
        SetMatrixOnProgramLoc(_shadowAwareGodRayProgram, shu.LightViewProjNear, frame.ShadowVpNear);
        SetMatrixOnProgramLoc(_shadowAwareGodRayProgram, shu.LightViewProjMid, frame.ShadowVpMid);
        SetVec3OnProgramLoc(_shadowAwareGodRayProgram, shu.CameraPos, frame.Eye);
        SetVec2OnProgramLoc(_shadowAwareGodRayProgram, shu.SunUv, lightUv);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.SunDiscRadius, discRadiusUv);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.SunConeRadius, coneRadiusUv);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.Aspect, frame.Vw / (float)Math.Max(frame.Vh, 1));
        SetVec2OnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowTexelSize, shadowTexelSize);
        SetVec2OnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowTexelSizeNear, shadowTexelSizeNear);
        SetVec2OnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowTexelSizeMid, shadowTexelSizeMid);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.Strength, strength);
        // Bind fog/layer uniforms every draw — shadow-aware program may be created after
        // ApplyGodRayPerSettingsUniforms already ran this frame.
        var layerWorldY = PreviewStageConstants.CloudLayerBaseWorldY(frame.Settings.CloudLayerHeight);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.LayerHeight, layerWorldY);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.VolumeHeight, frame.Settings.CloudVolumeHeight);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.CloudDensity, frame.Settings.CloudDensity);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.VolumeSize, frame.Settings.CloudVolumeSize);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.FogSlabHeight, PreviewStageConstants.GroundFogSlabHeight);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.HeightFogStrength,
            ResolveVolumeHeightFogStrength(frame.Settings));
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowMinBias, frame.Settings.ShadowMinBias);
        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.EnableShadowMap, 1);
        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.EnableShadowCascades, cascadesActive ? 1 : 0);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.CascadeSplitDistance, frame.CascadeSplitWorldDistance);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.CascadeMidSplitDistance, frame.CascadeMidSplitWorldDistance);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.CascadeBlendWidth, frame.CascadeBlendWorldWidth);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowDistance, frame.ShadowDistance);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowFadeStart, frame.ShadowFadeStart);
        // Screen-space beams are for leaf/gap detail; procedural cloud density would crush them.
        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.EnableCloudAttenuation, 0);
        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.HdrPresent, frame.Settings.HdrPresentActive ? 1 : 0);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.HdrPaperWhiteNits, frame.Settings.HdrPaperWhiteNits);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.HdrPeakNits, frame.Settings.HdrPeakNits);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.BindVertexArray(0);
        if (priorDepthTest)
        {
            gl.Enable(EnableCap.DepthTest);
        }
        else
        {
            gl.Disable(EnableCap.DepthTest);
        }

        if (!priorBlend)
        {
            gl.Disable(EnableCap.Blend);
        }

        if (_shadowAwareGodRayLogged == 0)
        {
            _shadowAwareGodRayLogged = 1;
            EmitDiagnostic("[3D preview] Shadow-aware screen-space god rays active.");
        }

        return true;
    }

    private void MaybeLogVolumetricTiming(in PreviewRenderSettingsSnapshot settings, double injectMs, double integrateMs) =>
        _godRaysPassCoordinator.TryLogVolumetricTiming(settings, injectMs, integrateMs, EmitDiagnostic);

    private void TryEnsureGodRayCanopyRefineProgram()
    {
        if (_godRayCanopyRefineProgram is { IsValid: true } || _shaderCtx is null || _godRayCanopyRefineInitAttempted)
        {
            return;
        }

        _godRayCanopyRefineInitAttempted = true;
        _godRayCanopyRefineProgram = CreatePreviewProgram(
            "genesis_godrays.vert",
            "genesis_godrays_canopy_refine.frag",
            out var err);
        if (_godRayCanopyRefineProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] God-ray canopy refine shader: " + TrimShaderDiagnostic(err));
            _godRayCanopyRefineProgram?.Dispose();
            _godRayCanopyRefineProgram = null;
            return;
        }

        _godRayCanopyRefineUniformLocs = ResolveGodRayCanopyRefineUniformLocs(_godRayCanopyRefineProgram);
    }

    private bool TryResolveGodRaySunScreenUv(
        ref GlRenderFrame frame,
        out Vector2 lightUv,
        out float discRadiusUv,
        out float coneRadiusUv)
    {
        lightUv = default;
        discRadiusUv = 0f;
        coneRadiusUv = 0f;
        var aspect = frame.Vw / (float)Math.Max(frame.Vh, 1);
        var coneScale = Math.Max(frame.Settings.GodRayConeScale, 0.05f);
        var towardSun = -frame.WorldLightDir;
        var tls = towardSun.LengthSquared();
        if (tls < 1e-12f)
        {
            return false;
        }

        towardSun /= MathF.Sqrt(tls);
        // Match celestial billboards: moon when the sun is at/below the horizon band.
        // Use direction-at-infinity projection so UV stays locked to the disc as far plane / orbit change.
        // Do NOT hard-kill when UV is off-screen — shafts still enter from the frame edge.
        if (towardSun.Y < PreviewGodRayTod.MoonHorizonBandY)
        {
            if (!PreviewSunScreenProjection.TryComputeMoon(
                    frame.Eye,
                    frame.WorldLightDir,
                    frame.View,
                    frame.Proj,
                    aspect,
                    out lightUv,
                    out discRadiusUv,
                    out _))
            {
                return false;
            }

            coneRadiusUv = PreviewSunScreenProjection.EnsureConeReachesViewport(
                lightUv,
                PreviewSunScreenProjection.ClampConeRadius(
                    Math.Max(
                        discRadiusUv * PreviewSunScreenProjection.ShaftScale * coneScale,
                        PreviewSunScreenProjection.MinShaftRadiusUv * coneScale),
                    coneScale),
                aspect,
                coneScale);
        }
        else if (!PreviewSunScreenProjection.TryCompute(
                     frame.Eye,
                     frame.WorldLightDir,
                     frame.View,
                     frame.Proj,
                     aspect,
                     coneScale,
                     frame.Settings.AtmosphereSunDiscSize,
                     out lightUv,
                     out discRadiusUv,
                     out coneRadiusUv,
                     out _))
        {
            return false;
        }

        return true;
    }

    private void TryRunGodRayCanopyRefine(ref GlRenderFrame frame)
    {
        TryEnsureGodRayCanopyRefineProgram();
        if (_godRayCanopyRefineProgram is not { IsValid: true } ||
            _sceneCapture is null ||
            _godRayResolveTarget is null ||
            _godRayHistoryTarget is null ||
            _godRayQuadVao == 0 ||
            _sceneCapture.TaaSignalTextureHandle == 0)
        {
            return;
        }

        // Prefer history copy of upsample to avoid FBO feedback on resolve.
        var sourceHandle = _godRayHistoryTarget.ColorTextureHandle;
        if (sourceHandle == 0)
        {
            return;
        }

        var gl = frame.Gl;
        _godRayResolveTarget.BindDraw();
        _godRayCanopyRefineProgram.Use();
        var cru = _godRayCanopyRefineUniformLocs;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, sourceHandle);
        SetIntOnProgramLoc(_godRayCanopyRefineProgram, cru.Rays, 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.TaaSignalTextureHandle);
        SetIntOnProgramLoc(_godRayCanopyRefineProgram, cru.FoliageMask, 1);
        // Mild attenuation on cutout faces; leaf holes come from cutout shadow inject.
        var refineStrength = Math.Clamp(frame.Settings.GodRayStrength * 0.65f, 0f, 0.85f);
        SetFloatOnProgramLoc(_godRayCanopyRefineProgram, cru.Strength, refineStrength);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        if (_godRayCanopyRefineLogged == 0)
        {
            _godRayCanopyRefineLogged = 1;
            EmitDiagnostic("[3D preview] God-ray foliage mask refine active (TaaSignal.A cutout occupancy).");
        }
    }

    private void DrawGodRayComposite(ref GlRenderFrame frame)
    {
        SyncGodRayToggleState(frame.Settings);
        _pendingScreenSpaceGodRayLayer = false;

        var wantsFroxel = frame.Settings.EnableGodRays;
        var wantsScreenSpace = frame.Settings.EnableScreenSpaceGodRays &&
                               frame.Settings.ScreenSpaceGodRayStrength > 1e-5f;
        if (!frame.GodRayCaptureActive || _sceneCapture is null || !_sceneCapture.IsValid ||
            _godRayQuadVao == 0 ||
            (!wantsFroxel && !wantsScreenSpace))
        {
            return;
        }

        if (wantsFroxel && _godRayCompositeProgram is not { IsValid: true })
        {
            wantsFroxel = false;
        }

        if (wantsScreenSpace &&
            _screenSpaceGodRayProgram is not { IsValid: true } &&
            _shadowAwareGodRayProgram is not { IsValid: true })
        {
            wantsScreenSpace = false;
        }

        if (!wantsFroxel && !wantsScreenSpace)
        {
            return;
        }

        var halfW = Math.Max(1, frame.Vw / 2);
        var halfH = Math.Max(1, frame.Vh / 2);
        var useLinearRayTargets = WantsLinearGodRayTargets();
        if (_godRayLinearTargetsActive != useLinearRayTargets)
        {
            _godRayHistoryValid = false;
            _volumeIntegrateHistoryValid = false;
            _godRayLinearTargetsActive = useLinearRayTargets;
        }

        var canVolume = wantsFroxel &&
                        CanUseVolumeGodRays(frame.Settings) &&
                        _godRayUpsampleProgram is { IsValid: true } &&
                        _godRayHalfResTarget is not null &&
                        _godRayResolveTarget is not null &&
                        _godRayHistoryTarget is not null;
        if (canVolume &&
            (!_godRayHalfResTarget!.EnsureSize(halfW, halfH, useLinearRayTargets, floatPreserveAlpha: useLinearRayTargets) ||
             !_godRayResolveTarget!.EnsureSize(frame.Vw, frame.Vh, useLinearRayTargets, floatPreserveAlpha: useLinearRayTargets) ||
             !_godRayHistoryTarget!.EnsureSize(frame.Vw, frame.Vh, useLinearRayTargets, floatPreserveAlpha: useLinearRayTargets)))
        {
            canVolume = false;
        }

        if (_godRayHistoryVw != frame.Vw || _godRayHistoryVh != frame.Vh)
        {
            _godRayHistoryValid = false;
            _godRayHistoryVw = frame.Vw;
            _godRayHistoryVh = frame.Vh;
        }

        var gl = frame.Gl;
        var viewProj = frame.Proj * frame.View;
        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
        {
            return;
        }

        var quality = PreviewVolumetricQuality.Resolve(frame.Settings.VolumetricQuality);
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorCullFace = gl.IsEnabled(EnableCap.CullFace);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorDepthMask = gl.GetBoolean(GetPName.DepthWritemask);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.DepthMask(false);
        gl.BindVertexArray(_godRayQuadVao);

        var volumeSw = frame.Settings.LogVolumetricTiming ? Stopwatch.StartNew() : null;
        var injectMs = 0.0;
        var integrateMs = 0.0;
        var usedVolumePath = canVolume && TryRunVolumeGodRayPass(ref frame, out injectMs, out integrateMs);
        if (volumeSw is not null)
        {
            volumeSw.Stop();
            MaybeLogVolumetricTiming(frame.Settings, injectMs, integrateMs);
        }

        if (!usedVolumePath)
        {
            if (wantsFroxel && _volumePathFailLogged == 0)
            {
                _volumePathFailLogged = 1;
                EmitDiagnostic(!canVolume
                    ? DescribeVolumeGodRayUnavailableReason(frame.Settings)
                    : "[3D preview] Froxel volumetric fog inject or integrate failed; using screen-space beams.");
            }

            if (wantsScreenSpace)
            {
                using (BeginPassTimerScope(GlGpuTimerScope.GodRayResolve))
                {
                    // Prefer depth-only SS (see TryCompositeScreenSpaceGodRayLayer).
                    TryRunScreenSpaceGodRays(ref frame, frame.Settings.ScreenSpaceGodRayStrength);
                }
            }
            else if (wantsFroxel)
            {
                using (BeginPassTimerScope(GlGpuTimerScope.GodRayResolve))
                {
                    TryRunScreenSpaceGodRays(ref frame, frame.Settings.GodRayStrength);
                }
            }

            gl.DepthMask(priorDepthMask);
            if (priorDepthTest)
            {
                gl.Enable(EnableCap.DepthTest);
            }
            else
            {
                gl.Disable(EnableCap.DepthTest);
            }

            if (priorCullFace)
            {
                gl.Enable(EnableCap.CullFace);
            }
            else
            {
                gl.Disable(EnableCap.CullFace);
            }

            if (!priorBlend)
            {
                gl.Disable(EnableCap.Blend);
            }

            gl.BindVertexArray(0);
            return;
        }

        // Full-res bilateral upsample + temporal reprojection.
        using (BeginPassTimerScope(GlGpuTimerScope.GodRayResolve))
        {
            _godRayResolveTarget!.BindDraw();
            gl.ClearColor(0f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            _godRayUpsampleProgram!.Use();
            var upu = _godRayUpsampleUniformLocs;
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _godRayHalfResTarget!.ColorTextureHandle);
            SetIntOnProgramLoc(_godRayUpsampleProgram!, upu.HalfResRays, 0);
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture!.DepthTextureHandle);
            SetIntOnProgramLoc(_godRayUpsampleProgram!, upu.SceneDepth, 1);
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2D, _godRayHistoryTarget!.ColorTextureHandle);
            SetIntOnProgramLoc(_godRayUpsampleProgram!, upu.History, 2);
            SetMatrixOnProgramLoc(_godRayUpsampleProgram!, upu.InvViewProj, invViewProj);
            SetMatrixOnProgramLoc(_godRayUpsampleProgram!, upu.PrevViewProj, _godRayPrevViewProj);
            SetVec2OnProgramLoc(_godRayUpsampleProgram!, upu.HalfResTexelSize, new Vector2(1f / halfW, 1f / halfH));
            var upsampleTemporal = frame.Settings.GodRayStabilizeDebug
                ? 0f
                : PreviewVolumetricQuality.EffectivePassTemporalWeight(
                    quality.UpsampleTemporalWeight, frame.Settings);
            SetFloatOnProgramLoc(_godRayUpsampleProgram!, upu.TemporalWeight, upsampleTemporal);
            SetIntOnProgramLoc(_godRayUpsampleProgram!, upu.HasHistory,
                !frame.Settings.GodRayStabilizeDebug &&
                _godRayHistoryValid && upsampleTemporal > 0f ? 1 : 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

            if (!frame.Settings.GodRayStabilizeDebug)
            {
                _godRayHistoryTarget.CopyColorFrom(_godRayResolveTarget);
                _godRayPrevViewProj = viewProj;
                _godRayHistoryValid = true;
                // Screen-space canopy refine: darken froxel in-scatter where sun-UV depth
                // march hits cutout foliage / thin occluders the froxel grid cannot resolve.
                // Sample history (pre-refine upsample) → write resolve to avoid FBO feedback.
                TryRunGodRayCanopyRefine(ref frame);
            }

            // Detailed cloud opacity/depth has already attenuated samples behind the cloud in
            // the integrate shader. Defer scene*T + inscatter composition until after the cloud
            // color pass so foreground shaft radiance is not attenuated a second time.
            _pendingGodRayCompositeTexture = _godRayResolveTarget.ColorTextureHandle;
            // Fine leaf-gap beams: shadow-aware SS layer composites after froxel fog.
            _pendingScreenSpaceGodRayLayer = wantsScreenSpace;
            gl.BindVertexArray(0);
        }

        gl.DepthMask(priorDepthMask);
        if (priorDepthTest)
        {
            gl.Enable(EnableCap.DepthTest);
        }
        else
        {
            gl.Disable(EnableCap.DepthTest);
        }

        if (priorCullFace)
        {
            gl.Enable(EnableCap.CullFace);
        }
        else
        {
            gl.Disable(EnableCap.CullFace);
        }

        if (!priorBlend)
        {
            gl.Disable(EnableCap.Blend);
        }
    }
}
