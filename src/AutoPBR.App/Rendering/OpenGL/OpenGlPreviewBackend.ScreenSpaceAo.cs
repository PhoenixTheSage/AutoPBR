using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private GlShaderProgram? _ssaoProgram;
    private GlShaderProgram? _gtaoProgram;
    private GlShaderProgram? _aoBilateralProgram;
    private GlShaderProgram? _aoTemporalProgram;
    private GlShaderProgram? _aoCompositeProgram;
    private GlScreenSpaceAoResources? _aoResources;
    private Matrix4x4 _aoPrevViewProj = Matrix4x4.Identity;
    private bool _aoHistoryValid;
    private int _aoFrameIndex;
    private int _aoFailLogged;
    private bool _aoCapabilityDenied;

    private void TryInitScreenSpaceAo(GL gl, bool useOpenGlEs)
    {
        DestroyScreenSpaceAoResources();
        if (_glCapabilities is { CanUseScreenSpaceAo: false })
        {
            _aoCapabilityDenied = true;
            EmitDiagnostic("[3D preview] Screen-space AO disabled (need 3 color attachments / draw buffers).");
            return;
        }

        _aoCapabilityDenied = false;
        _ssaoProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_ssao.frag", out var ssaoErr, "preview-ssao");
        if (_ssaoProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] SSAO shader: " + (ssaoErr ?? "link failed"));
            DestroyScreenSpaceAoResources();
            return;
        }

        _gtaoProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_gtao.frag", out var gtaoErr, "preview-gtao");
        if (_gtaoProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] GTAO shader: " + (gtaoErr ?? "link failed"));
            DestroyScreenSpaceAoResources();
            return;
        }

        _aoBilateralProgram = CreatePreviewProgram(
            "genesis_godrays.vert", "genesis_ao_bilateral.frag", out var blurErr, "preview-ao-bilateral");
        if (_aoBilateralProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] AO bilateral shader: " + (blurErr ?? "link failed"));
            DestroyScreenSpaceAoResources();
            return;
        }

        _aoTemporalProgram = CreatePreviewProgram(
            "genesis_godrays.vert", "genesis_ao_temporal.frag", out var temporalErr, "preview-ao-temporal");
        if (_aoTemporalProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] AO temporal shader: " + (temporalErr ?? "link failed"));
            DestroyScreenSpaceAoResources();
            return;
        }

        _aoCompositeProgram = CreatePreviewProgram(
            "genesis_godrays.vert", "genesis_ao_composite.frag", out var compositeErr, "preview-ao-composite");
        if (_aoCompositeProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] AO composite shader: " + (compositeErr ?? "link failed"));
            DestroyScreenSpaceAoResources();
            return;
        }

        _ssaoUniformLocs = ResolveSsaoUniformLocs(_ssaoProgram);
        _gtaoUniformLocs = ResolveGtaoUniformLocs(_gtaoProgram);
        _aoBilateralUniformLocs = ResolveAoBilateralUniformLocs(_aoBilateralProgram);
        _aoTemporalUniformLocs = ResolveAoTemporalUniformLocs(_aoTemporalProgram);
        _aoCompositeUniformLocs = ResolveAoCompositeUniformLocs(_aoCompositeProgram);
        _aoResources = new GlScreenSpaceAoResources(gl, useOpenGlEs);

        if (!TryInitSceneCaptureCore(gl, useOpenGlEs, out var sceneErr))
        {
            EmitDiagnostic("[3D preview] Screen-space AO scene capture: " + TrimShaderDiagnostic(sceneErr));
        }
    }

    private void DestroyScreenSpaceAoResources()
    {
        _ssaoProgram?.Dispose();
        _ssaoProgram = null;
        _gtaoProgram?.Dispose();
        _gtaoProgram = null;
        _aoBilateralProgram?.Dispose();
        _aoBilateralProgram = null;
        _aoTemporalProgram?.Dispose();
        _aoTemporalProgram = null;
        _aoCompositeProgram?.Dispose();
        _aoCompositeProgram = null;
        _aoResources?.Dispose();
        _aoResources = null;
        _aoHistoryValid = false;
        _aoFrameIndex = 0;
    }

    private bool IsScreenSpaceAoActive(in PreviewRenderSettingsSnapshot settings) =>
        settings.EnableScreenSpaceAo &&
        !_aoCapabilityDenied &&
        _ssaoProgram is { IsValid: true } &&
        _gtaoProgram is { IsValid: true } &&
        _aoBilateralProgram is { IsValid: true } &&
        _aoTemporalProgram is { IsValid: true } &&
        _aoCompositeProgram is { IsValid: true } &&
        _aoResources is not null &&
        _godRayQuadVao != 0 &&
        _sceneCapture is { IsValid: true };

    private bool CanUseAoSceneCapture(in PreviewRenderSettingsSnapshot settings) =>
        settings.EnableScreenSpaceAo &&
        !_aoCapabilityDenied &&
        _godRayQuadVao != 0 &&
        (_aoCompositeProgram is { IsValid: true } || _scenePresentProgram is { IsValid: true });

    private void MaybeLogScreenSpaceAoInactive(in PreviewRenderSettingsSnapshot settings)
    {
        if (!settings.EnableScreenSpaceAo || IsScreenSpaceAoActive(settings))
        {
            return;
        }

        if (_aoFailLogged == 2)
        {
            return;
        }

        _aoFailLogged = 2;
        var reason =
            _aoCapabilityDenied ? "capability denied" :
            _ssaoProgram is not { IsValid: true } ? "shaders not ready" :
            _aoResources is null ? "resources missing" :
            _sceneCapture is not { IsValid: true } ? "scene capture missing" :
            "waiting for GPU init";
        EmitDiagnostic("[3D preview] Screen-space AO enabled but inactive (" + reason + ").");
    }
    private PreviewScreenSpaceAoQuality.Profile ResolveScreenSpaceAoProfile(in PreviewRenderSettingsSnapshot settings) =>
        PreviewScreenSpaceAoQuality.Resolve((PreviewAoMode)settings.PreviewAoMode, settings.VolumetricQuality);

    /// <summary>
    /// Computes half-res AO, bilateral blur, optional temporal filter. Returns the AO texture for present,
    /// or 0 when AO should be skipped.
    /// </summary>
    private uint DrawScreenSpaceAo(ref GlRenderFrame frame)
    {
        if (!IsScreenSpaceAoActive(frame.Settings) || _sceneCapture is null || _aoResources is null)
        {
            return 0;
        }

        var profile = ResolveScreenSpaceAoProfile(frame.Settings);
        var captureW = Math.Max(1, frame.SceneCaptureW > 0 ? frame.SceneCaptureW : frame.Vw);
        var captureH = Math.Max(1, frame.SceneCaptureH > 0 ? frame.SceneCaptureH : frame.Vh);
        var aoW = Math.Max(1, (int)MathF.Round(captureW * profile.ResolutionScale));
        var aoH = Math.Max(1, (int)MathF.Round(captureH * profile.ResolutionScale));
        if (!_aoResources.EnsureSize(aoW, aoH))
        {
            if (_aoFailLogged != 1)
            {
                _aoFailLogged = 1;
                EmitDiagnostic("[3D preview] Screen-space AO targets incomplete; presenting without AO.");
            }

            return 0;
        }

        var gl = frame.Gl;
        var depthTex = _sceneCapture.DepthTextureHandle;
        if (depthTex == 0)
        {
            return 0;
        }

        var hasViewNormal = _sceneCapture.HasViewNormals;
        var normalTex = hasViewNormal ? _sceneCapture.ViewNormalTextureHandle : depthTex;

        Matrix4x4.Invert(frame.Proj, out var invProj);
        var viewProj = frame.UnjitteredProj * frame.View;
        Matrix4x4.Invert(viewProj, out var invViewProj);
        var radius = Math.Clamp(frame.Settings.AoRadius, 0.05f, 8f) * profile.RadiusScale;
        var aoTexel = new Vector2(1f / aoW, 1f / aoH);
        // Full-res depth/normal texel for derivative fallback when AO runs at half-res.
        var depthTexel = new Vector2(1f / Math.Max(1, captureW), 1f / Math.Max(1, captureH));
        var frameIndex = _aoFrameIndex++;

        // --- Raw AO ---
        var useGtao = profile.Technique == PreviewAoMode.Gtao;
        var aoProgram = useGtao ? _gtaoProgram! : _ssaoProgram!;
        _aoResources.Raw.BindDraw();
        // Unwritten texels must mean "no occlusion". SSAA scale changes recreate AO targets;
        // leaving them uncleared made half the screen multiply by ~0 after a bad/partial fill.
        var priorClear = new float[4];
        gl.GetFloat(GetPName.ColorClearValue, priorClear);
        gl.ClearColor(1f, 1f, 1f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        gl.ClearColor(priorClear[0], priorClear[1], priorClear[2], priorClear[3]);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.ScissorTest);
        gl.DepthMask(false);
        gl.ColorMask(true, true, true, true);
        gl.BindVertexArray(_godRayQuadVao);
        aoProgram.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, depthTex);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, normalTex);
        if (useGtao)
        {
            var gu = _gtaoUniformLocs;
            SetIntOnProgramLoc(aoProgram, gu.SceneDepth, 0);
            SetIntOnProgramLoc(aoProgram, gu.ViewNormal, 1);
            SetMatrixOnProgramLoc(aoProgram, gu.InvProj, invProj);
            SetMatrixOnProgramLoc(aoProgram, gu.Proj, frame.Proj);
            SetVec2OnProgramLoc(aoProgram, gu.AoTexelSize, depthTexel);
            SetFloatOnProgramLoc(aoProgram, gu.AoRadius, radius);
            SetFloatOnProgramLoc(aoProgram, gu.AoBias, 0.02f);
            SetFloatOnProgramLoc(aoProgram, gu.AoPower, Math.Clamp(frame.Settings.AoPower, 0.1f, 4f));
            SetFloatOnProgramLoc(aoProgram, gu.AoIntensity, 1.5f);
            SetIntOnProgramLoc(aoProgram, gu.GtaoSlices, profile.GtaoSlices);
            SetIntOnProgramLoc(aoProgram, gu.GtaoSteps, profile.GtaoSteps);
            SetFloatOnProgramLoc(aoProgram, gu.FrameIndex, frameIndex);
            SetIntOnProgramLoc(aoProgram, gu.HasSceneDepth, 1);
            SetIntOnProgramLoc(aoProgram, gu.HasViewNormal, hasViewNormal ? 1 : 0);
            // Multi-bounce lifts crevice AO toward mid-gray; keep off so GTAO reads like SSAO.
            SetIntOnProgramLoc(aoProgram, gu.EnableMultiBounce, 0);
        }
        else
        {
            var su = _ssaoUniformLocs;
            SetIntOnProgramLoc(aoProgram, su.SceneDepth, 0);
            SetIntOnProgramLoc(aoProgram, su.ViewNormal, 1);
            SetMatrixOnProgramLoc(aoProgram, su.InvProj, invProj);
            SetMatrixOnProgramLoc(aoProgram, su.Proj, frame.Proj);
            SetVec2OnProgramLoc(aoProgram, su.AoTexelSize, depthTexel);
            SetFloatOnProgramLoc(aoProgram, su.AoRadius, radius);
            SetFloatOnProgramLoc(aoProgram, su.AoBias, 0.025f);
            SetFloatOnProgramLoc(aoProgram, su.AoPower, Math.Clamp(frame.Settings.AoPower, 0.1f, 4f));
            SetFloatOnProgramLoc(aoProgram, su.AoIntensity, 1.5f);
            SetIntOnProgramLoc(aoProgram, su.AoSampleCount, profile.SsaoSamples);
            SetFloatOnProgramLoc(aoProgram, su.FrameIndex, frameIndex);
            SetIntOnProgramLoc(aoProgram, su.HasSceneDepth, 1);
            SetIntOnProgramLoc(aoProgram, su.HasViewNormal, hasViewNormal ? 1 : 0);
        }

        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        // --- Bilateral blur passes ---
        var srcTex = _aoResources.Raw.ColorTextureHandle;
        for (var pass = 0; pass < Math.Max(1, profile.BilateralPasses); pass++)
        {
            _aoResources.BlurA.BindDraw();
            _aoBilateralProgram!.Use();
            BindAoBilateral(gl, srcTex, depthTex, aoTexel, new Vector2(1f, 0f));
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

            _aoResources.BlurB.BindDraw();
            BindAoBilateral(gl, _aoResources.BlurA.ColorTextureHandle, depthTex, aoTexel, new Vector2(0f, 1f));
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            srcTex = _aoResources.BlurB.ColorTextureHandle;
        }

        var blurredTex = srcTex;

        // --- Temporal (High/Cinematic when preview TAA is on) ---
        var useTemporal = profile.UseTemporal && IsPreviewTaaActive(frame.Settings);
        uint aoTex = blurredTex;
        if (useTemporal)
        {
            var historySrc = _aoResources.CurrentHistory;
            var historyDst = _aoResources.NextHistory;
            historyDst.BindDraw();
            _aoTemporalProgram!.Use();
            var tu = _aoTemporalUniformLocs;
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, blurredTex);
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, historySrc.ColorTextureHandle);
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2D, depthTex);
            SetIntOnProgramLoc(_aoTemporalProgram, tu.AoCurrent, 0);
            SetIntOnProgramLoc(_aoTemporalProgram, tu.AoHistory, 1);
            SetIntOnProgramLoc(_aoTemporalProgram, tu.SceneDepth, 2);
            SetMatrixOnProgramLoc(_aoTemporalProgram, tu.InvViewProj, invViewProj);
            SetMatrixOnProgramLoc(_aoTemporalProgram, tu.PrevViewProj, _aoHistoryValid ? _aoPrevViewProj : viewProj);
            SetVec3OnProgramLoc(_aoTemporalProgram, tu.CameraPos, frame.Eye);
            SetFloatOnProgramLoc(_aoTemporalProgram, tu.TemporalWeight, 0.85f);
            SetIntOnProgramLoc(_aoTemporalProgram, tu.HasHistory, _aoHistoryValid ? 1 : 0);
            SetIntOnProgramLoc(_aoTemporalProgram, tu.HasSceneDepth, 1);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            _aoResources.SwapHistory();
            _aoHistoryValid = true;
            _aoPrevViewProj = viewProj;
            aoTex = historyDst.ColorTextureHandle;
        }
        else
        {
            _aoHistoryValid = false;
        }

        gl.BindVertexArray(0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return aoTex;
    }

    private void BindAoBilateral(GL gl, uint aoSource, uint depthTex, Vector2 texel, Vector2 direction)
    {
        var bu = _aoBilateralUniformLocs;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, aoSource);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, depthTex);
        SetIntOnProgramLoc(_aoBilateralProgram!, bu.AoSource, 0);
        SetIntOnProgramLoc(_aoBilateralProgram!, bu.SceneDepth, 1);
        SetVec2OnProgramLoc(_aoBilateralProgram!, bu.AoTexelSize, texel);
        SetVec2OnProgramLoc(_aoBilateralProgram!, bu.BlurDirection, direction);
        SetFloatOnProgramLoc(_aoBilateralProgram!, bu.DepthSigma, 80f);
        SetIntOnProgramLoc(_aoBilateralProgram!, bu.HasSceneDepth, 1);
    }

    private bool TryPresentSceneCaptureWithAo(ref GlRenderFrame frame, uint aoTexture)
    {
        if (_sceneCapture is null || !_sceneCapture.IsValid || _godRayQuadVao == 0)
        {
            return false;
        }

        if (_aoCompositeProgram is not { IsValid: true } || aoTexture == 0)
        {
            return false;
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
        _aoCompositeProgram.Use();

        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.ColorTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, aoTexture);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.ViewNormalTextureHandle);

        var cu = _aoCompositeUniformLocs;
        SetIntOnProgramLoc(_aoCompositeProgram, cu.SceneColor, 0);
        SetIntOnProgramLoc(_aoCompositeProgram, cu.Ao, 1);
        SetIntOnProgramLoc(_aoCompositeProgram, cu.ViewNormal, 2);
        SetIntOnProgramLoc(_aoCompositeProgram, cu.HdrPresent, frame.Settings.HdrPresentActive ? 1 : 0);
        SetIntOnProgramLoc(_aoCompositeProgram, cu.SceneIsLinear, frame.Settings.HdrPresentActive ? 1 : 0);
        SetFloatOnProgramLoc(_aoCompositeProgram, cu.HdrPaperWhiteNits, frame.Settings.HdrPaperWhiteNits);
        SetFloatOnProgramLoc(_aoCompositeProgram, cu.HdrPeakNits, frame.Settings.HdrPeakNits);
        SetFloatOnProgramLoc(_aoCompositeProgram, cu.AoStrength, Math.Clamp(frame.Settings.AoStrength, 0f, 1f));
        SetIntOnProgramLoc(_aoCompositeProgram, cu.HasAo, 1);
        SetIntOnProgramLoc(_aoCompositeProgram, cu.HasViewNormal, _sceneCapture.HasViewNormals ? 1 : 0);
        SetIntOnProgramLoc(_aoCompositeProgram, cu.AoDebugView, Math.Clamp(frame.Settings.AoDebugView, 0, 2));

        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        var err = gl.GetError();
        gl.BindVertexArray(0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, 0);

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

        return err == GLEnum.NoError;
    }
}
