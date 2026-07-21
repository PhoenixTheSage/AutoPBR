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
    private Matrix4x4 _godRayPrevViewProj = Matrix4x4.Identity;
    private bool _godRayHistoryValid;
    private int _godRayHistoryVw;
    private int _godRayHistoryVh;
    private int _volumePathFailLogged;
    private bool _godRaySparseMarchCompiled;
    private uint _pendingGodRayCompositeTexture;

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
        }

        if (invalidation.ResetCloudDrawLog)
        {
            _loggedCloudDraw = false;
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
        var godRayDefines = BuildGodRaySparseMarchDefines(_godRaySparseMarchCompiled);
        _screenSpaceGodRayProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_godrays.frag", out var ssErr,
            defines: godRayDefines);
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

    private float ResolvePreviewSceneCaptureScale(in PreviewRenderSettingsSnapshot settings) =>
        GodRaysPassCoordinator.ResolveSceneCaptureScale(
            settings,
            s => IsPreviewTaaActive(s),
            s => ResolveEffectivePreviewTaa(s));

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
        if ((!CanUseGodRayCapture(frame.Settings) && !CanUseTaaSceneCapture(frame.Settings) &&
             !CanUseCloudSceneCapture(frame.Settings)) || _sceneCapture is null)
        {
            return false;
        }

        ResolveSceneCaptureSize(ref frame, out var captureW, out var captureH, out var captureScale);
        if (!_sceneCapture.EnsureSize(captureW, captureH, frame.Settings.HdrPresentActive))
        {
            EmitDiagnostic("[3D preview] Shared scene-capture target incomplete; rendering directly to the default FBO.");
            return false;
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

        // Never blit raw capture to an HDR DXGI target — that skips scRGB encode + Y-flip and
        // produces upside-down / flashing over-bright frames.
        var presented = TryPresentSceneCaptureToDefault(ref frame);
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

    private void TryCompositeAdditiveRays(ref GlRenderFrame frame, uint raysTexture, uint cloudMaskTexture = 0)
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
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        gl.BindVertexArray(_godRayQuadVao);
        _godRayCompositeProgram.Use();
        var cu = _godRayCompositeUniformLocs;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, raysTexture);
        SetIntOnProgramLoc(_godRayCompositeProgram, cu.Rays, 0);
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
            TryCompositeAdditiveRays(ref frame, texture);
        }
    }

    private void TryRunScreenSpaceGodRays(ref GlRenderFrame frame)
    {
        if (_screenSpaceGodRayProgram is null || _sceneCapture is null || _godRayQuadVao == 0)
        {
            return;
        }

        var aspect = frame.Vw / (float)Math.Max(frame.Vh, 1);
        var coneScale = Math.Max(frame.Settings.GodRayConeScale, 0.05f);
        var towardSun = -frame.WorldLightDir;
        var tls = towardSun.LengthSquared();
        if (tls < 1e-12f)
        {
            return;
        }

        towardSun /= MathF.Sqrt(tls);

        Vector2 lightUv;
        float discRadiusUv;
        float coneRadiusUv;
        if (towardSun.Y < 0.04f)
        {
            PreviewSunScreenProjection.ComputeMoon(frame.Eye, frame.WorldLightDir, frame.View, frame.Proj, aspect,
                out lightUv, out discRadiusUv, out _);
            coneRadiusUv = Math.Max(discRadiusUv * PreviewSunScreenProjection.ShaftScale * coneScale,
                PreviewSunScreenProjection.MinShaftRadiusUv * coneScale);
        }
        else
        {
            PreviewSunScreenProjection.Compute(frame.Eye, frame.WorldLightDir, frame.View, frame.Proj, aspect, coneScale,
                frame.Settings.AtmosphereSunDiscSize, out lightUv, out discRadiusUv, out coneRadiusUv, out _);
        }

        var gl = frame.Gl;
        BindDefaultFramebuffer(ref frame);
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        gl.BindVertexArray(_godRayQuadVao);
        _screenSpaceGodRayProgram.Use();
        var ssu = _screenSpaceGodRayUniformLocs;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
        SetIntOnProgramLoc(_screenSpaceGodRayProgram, ssu.SceneDepth, 0);
        SetVec2OnProgramLoc(_screenSpaceGodRayProgram, ssu.SunUv, lightUv);
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.SunDiscRadius, discRadiusUv);
        SetFloatOnProgramLoc(_screenSpaceGodRayProgram, ssu.SunConeRadius, coneRadiusUv);
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

        if (_screenSpaceGodRayLogged == 0)
        {
            _screenSpaceGodRayLogged = 1;
            EmitDiagnostic("[3D preview] Screen-space god-ray fallback active.");
        }
    }

    private bool TryRunShadowAwareGodRays(ref GlRenderFrame frame)
    {
        TryEnsureShadowAwareGodRayProgram();
        if (_shadowAwareGodRayProgram is null || _sceneCapture is null || _godRayQuadVao == 0 ||
            !frame.ShadowAvailable || _shadowTarget is null)
        {
            return false;
        }

        var aspect = frame.Vw / (float)Math.Max(frame.Vh, 1);
        var coneScale = Math.Max(frame.Settings.GodRayConeScale, 0.05f);
        var towardSun = -frame.WorldLightDir;
        var tls = towardSun.LengthSquared();
        if (tls < 1e-12f)
        {
            return false;
        }

        towardSun /= MathF.Sqrt(tls);

        Vector2 lightUv;
        float discRadiusUv;
        float coneRadiusUv;
        if (towardSun.Y < 0.04f)
        {
            PreviewSunScreenProjection.ComputeMoon(frame.Eye, frame.WorldLightDir, frame.View, frame.Proj, aspect,
                out lightUv, out discRadiusUv, out _);
            coneRadiusUv = Math.Max(discRadiusUv * PreviewSunScreenProjection.ShaftScale * coneScale,
                PreviewSunScreenProjection.MinShaftRadiusUv * coneScale);
        }
        else
        {
            PreviewSunScreenProjection.Compute(frame.Eye, frame.WorldLightDir, frame.View, frame.Proj, aspect, coneScale,
                frame.Settings.AtmosphereSunDiscSize, out lightUv, out discRadiusUv, out coneRadiusUv, out _);
        }

        var viewProj = frame.Proj * frame.View;
        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
        {
            return false;
        }

        var gl = frame.Gl;
        var shadowRes = _shadowTarget.Resolution;
        var shadowTexelSize = new Vector2(1f / shadowRes, 1f / shadowRes);
        var layerWorldY = PreviewStageConstants.CloudLayerBaseWorldY(frame.Settings.CloudLayerHeight);
        var cascadesActive = frame.ShadowCascadesActive;

        BindDefaultFramebuffer(ref frame);
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
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
        SetMatrixOnProgramLoc(_shadowAwareGodRayProgram, shu.InvViewProj, invViewProj);
        SetMatrixOnProgramLoc(_shadowAwareGodRayProgram, shu.LightViewProj, frame.ShadowVp);
        SetMatrixOnProgramLoc(_shadowAwareGodRayProgram, shu.LightViewProjNear, frame.ShadowVpNear);
        SetVec3OnProgramLoc(_shadowAwareGodRayProgram, shu.CameraPos, frame.Eye);
        SetVec2OnProgramLoc(_shadowAwareGodRayProgram, shu.SunUv, lightUv);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.SunDiscRadius, discRadiusUv);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.SunConeRadius, coneRadiusUv);
        SetVec2OnProgramLoc(_shadowAwareGodRayProgram, shu.ShadowTexelSize, shadowTexelSize);
        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.EnableShadowMap, 1);
        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.EnableShadowCascades, cascadesActive ? 1 : 0);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.CascadeSplitDistance, frame.CascadeSplitWorldDistance);
        SetFloatOnProgramLoc(_shadowAwareGodRayProgram, shu.CascadeBlendWidth, frame.CascadeBlendWorldWidth);
        SetIntOnProgramLoc(_shadowAwareGodRayProgram, shu.EnableCloudAttenuation,
            frame.Settings.EnableVolumetricClouds &&
            ResolveSharedCloudTransmittanceTarget(frame.Settings) is null ? 1 : 0);
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
            EmitDiagnostic("[3D preview] Shadow-aware god-ray fallback active.");
        }

        return true;
    }

    private void MaybeLogVolumetricTiming(in PreviewRenderSettingsSnapshot settings, double injectMs, double integrateMs) =>
        _godRaysPassCoordinator.TryLogVolumetricTiming(settings, injectMs, integrateMs, EmitDiagnostic);

    private void DrawGodRayComposite(ref GlRenderFrame frame)
    {
        SyncGodRayToggleState(frame.Settings);

        if (!frame.GodRayCaptureActive || _sceneCapture is null || !_sceneCapture.IsValid ||
            _godRayCompositeProgram is not { IsValid: true } ||
            _screenSpaceGodRayProgram is not { IsValid: true } ||
            _godRayQuadVao == 0)
        {
            return;
        }

        var halfW = Math.Max(1, frame.Vw / 2);
        var halfH = Math.Max(1, frame.Vh / 2);
        var canVolume = CanUseVolumeGodRays(frame.Settings) &&
                        _godRayUpsampleProgram is { IsValid: true } &&
                        _godRayHalfResTarget is not null &&
                        _godRayResolveTarget is not null &&
                        _godRayHistoryTarget is not null;
        if (canVolume &&
            (!_godRayHalfResTarget!.EnsureSize(halfW, halfH) ||
             !_godRayResolveTarget!.EnsureSize(frame.Vw, frame.Vh) ||
             !_godRayHistoryTarget!.EnsureSize(frame.Vw, frame.Vh)))
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
            if (_volumePathFailLogged == 0)
            {
                _volumePathFailLogged = 1;
                EmitDiagnostic(!canVolume
                    ? DescribeVolumeGodRayUnavailableReason(frame.Settings)
                    : "[3D preview] Froxel god rays inject or integrate pass failed; using screen-space fallback.");
            }

            if (!TryRunShadowAwareGodRays(ref frame))
            {
                TryRunScreenSpaceGodRays(ref frame);
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
        _godRayResolveTarget!.BindDraw();
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
        }

        // Detailed cloud opacity/depth has already attenuated samples behind the cloud in
        // the integrate shader. Defer additive composition until after the cloud color pass
        // so foreground shaft radiance is not attenuated a second time.
        _pendingGodRayCompositeTexture = _godRayResolveTarget.ColorTextureHandle;
        gl.BindVertexArray(0);

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

    private void SetVec2OnProgram(GlShaderProgram program, string name, Vector2 v)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            _gl!.Uniform2(loc, v.X, v.Y);
        }
    }
}
