using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.App.Services;

using Silk.NET.OpenGL;

using AutoPBR.PreviewGpuAssets;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private GlShaderProgram? _cloudProgram;
    private GlShaderProgram? _cloudTemporalProgram;
    private GlShaderProgram? _cloudUpsampleProgram;
    private uint _cloudQuadVao;
    private uint _cloudQuadVbo;
    private GlTexture3D? _cloudNoiseTex;
    private GlTexture3D? _cloudDetailTex;
    private GlTexture2D? _cloudCoverageTex;
    private GlCloudTemporalRenderTarget? _cloudRenderTarget;
    private GlCloudTemporalRenderTarget? _cloudResolveTarget;
    private GlCloudTemporalRenderTarget? _cloudHistoryTarget;
    private GlCloudTemporalRenderTarget? _cloudCompositeTarget;
    private Matrix4x4 _cloudPrevViewProj = Matrix4x4.Identity;
    private Vector3 _cloudPrevCameraPos;
    private Vector3 _cloudPrevWindOffset;
    private Vector2 _cloudPrevCirrusWindOffset;
    private bool _cloudHistoryValid;
    private int _cloudFrameIndex;
    private int _cloudHistorySettingsHash;
    private int _cloudHistoryW;
    private int _cloudHistoryH;
    private bool _loggedCloudDraw;
    private int _cloudDeferredCompositeRetries;
    private int _loggedCloudDeferredCompositeMiss;
    private int _cloudTierReadyWarmupDraws;
    private bool _cloudRuntimeFaulted;
    private PreviewCloudCameraRegion? _cloudCameraRegion;

    private void TryInitVolumetricClouds(GL gl, bool useOpenGlEs)
    {
        DestroyVolumetricCloudResources();
        _cloudProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_clouds.frag", out var err);
        if (_cloudProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Volumetric cloud shader: " + (err ?? "link failed"));
            _cloudProgram?.Dispose();
            _cloudProgram = null;
            return;
        }

        _cloudUniformLocs = ResolveCloudUniformLocs(_cloudProgram);

        _cloudTemporalProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_clouds_temporal.frag",
            out var temporalErr, "clouds-temporal");
        if (_cloudTemporalProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Cloud temporal shader: " + (temporalErr ?? "link failed"));
            _cloudTemporalProgram?.Dispose();
            _cloudTemporalProgram = null;
        }
        else
        {
            _cloudTemporalUniformLocs = ResolveCloudTemporalUniformLocs(_cloudTemporalProgram);
        }

        // Depth-aware half-res upsample; non-fatal, falls back to the god-ray composite blit.
        _cloudUpsampleProgram = CreatePreviewProgram("genesis_godrays.vert", "genesis_clouds_upsample.frag",
            out var upErr, "clouds-upsample");
        if (_cloudUpsampleProgram is not { IsValid: true })
        {
            EmitDiagnostic("[3D preview] Cloud upsample shader: " + (upErr ?? "link failed"));
            _cloudUpsampleProgram?.Dispose();
            _cloudUpsampleProgram = null;
        }
        else
        {
            _cloudUpsampleUniformLocs = ResolveCloudUpsampleUniformLocs(_cloudUpsampleProgram);
        }

        // Clouds need the opaque color/depth capture even when god rays and preview TAA are off.
        // Failure is non-fatal: the shader keeps its no-depth fallback for limited GLES drivers.
        if (!TryInitSceneCaptureCore(gl, useOpenGlEs, out var sceneCaptureErr))
        {
            EmitDiagnostic("[3D preview] Cloud scene-depth capture unavailable: " +
                TrimShaderDiagnostic(sceneCaptureErr));
        }

        if (_godRayCompositeProgram is { IsValid: true })
        {
            _cloudCompositeUniformLocs = ResolveCloudCompositeUniformLocs(_godRayCompositeProgram);
        }

        _cloudNoiseTex = new GlTexture3D(gl);
        if (PreviewCloudBakedAssetLoader.TryLoadShapeNoise(out var shapeRgba))
        {
            _cloudNoiseTex.UploadRgba(PreviewCloudNoiseTextureGenerator.Size, shapeRgba);
        }
        else
        {
            _cloudNoiseTex.UploadRgba(PreviewCloudNoiseTextureGenerator.Size,
                PreviewCloudNoiseTextureGenerator.GenerateRgba8());
        }

        _cloudDetailTex = new GlTexture3D(gl);
        if (PreviewCloudBakedAssetLoader.TryLoadDetailNoise(out var detailRgba))
        {
            _cloudDetailTex.UploadRgba(PreviewCloudNoiseTextureGenerator.DetailSize, detailRgba);
        }
        else
        {
            _cloudDetailTex.UploadRgba(PreviewCloudNoiseTextureGenerator.DetailSize,
                PreviewCloudNoiseTextureGenerator.GenerateDetailRgba8());
        }

        _cloudCoverageTex = new GlTexture2D(gl, nearestFilter: false, mipmapped: true);
        if (PreviewCloudBakedAssetLoader.TryLoadCoverageMap(out var coverageRgba))
        {
            _cloudCoverageTex.UploadRgba(PreviewCloudCoverageMapGenerator.Size, PreviewCloudCoverageMapGenerator.Size,
                coverageRgba, nearestFilter: false);
        }
        else
        {
            _cloudCoverageTex.UploadRgba(PreviewCloudCoverageMapGenerator.Size, PreviewCloudCoverageMapGenerator.Size,
                PreviewCloudCoverageMapGenerator.GenerateRgba8(), nearestFilter: false);
        }
        _cloudRenderTarget = new GlCloudTemporalRenderTarget(gl);
        _cloudResolveTarget = new GlCloudTemporalRenderTarget(gl);
        _cloudHistoryTarget = new GlCloudTemporalRenderTarget(gl);

        Span<float> quad =
        [
            -1f, -1f, 1f, -1f, 1f, 1f,
            -1f, -1f, 1f, 1f, -1f, 1f
        ];
        _cloudQuadVao = gl.GenVertexArray();
        _cloudQuadVbo = gl.GenBuffer();
        gl.BindVertexArray(_cloudQuadVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _cloudQuadVbo);
        gl.BufferData<float>(GLEnum.ArrayBuffer, quad, GLEnum.StaticDraw);
        unsafe
        {
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }

        gl.BindVertexArray(0);
    }

    private void TryWarmCloudOffscreenTargets(int fullWidth, int fullHeight)
    {
        var w = Math.Max(1, fullWidth / 2);
        var h = Math.Max(1, fullHeight / 2);
        _cloudHistoryW = w;
        _cloudHistoryH = h;
        InvalidateCloudTemporalHistory();
        if (_cloudRenderTarget is not null)
        {
            _ = _cloudRenderTarget.EnsureSize(w, h);
        }

        if (_cloudResolveTarget is not null)
        {
            _ = _cloudResolveTarget.EnsureSize(w, h);
        }

        if (_cloudHistoryTarget is not null)
        {
            _ = _cloudHistoryTarget.EnsureSize(w, h);
        }
    }

    private void DestroyVolumetricCloudResources()
    {
        var gl = _gl;
        _cloudProgram?.Dispose();
        _cloudProgram = null;
        _cloudTemporalProgram?.Dispose();
        _cloudTemporalProgram = null;
        _cloudUpsampleProgram?.Dispose();
        _cloudUpsampleProgram = null;
        _cloudNoiseTex?.Dispose();
        _cloudNoiseTex = null;
        _cloudDetailTex?.Dispose();
        _cloudDetailTex = null;
        _cloudCoverageTex?.Dispose();
        _cloudCoverageTex = null;
        _cloudRenderTarget?.Dispose();
        _cloudRenderTarget = null;
        _cloudResolveTarget?.Dispose();
        _cloudResolveTarget = null;
        _cloudHistoryTarget?.Dispose();
        _cloudHistoryTarget = null;
        _cloudCompositeTarget = null;
        InvalidateCloudTemporalHistory();
        _loggedCloudDraw = false;
        _cloudDeferredCompositeRetries = 0;
        _loggedCloudDeferredCompositeMiss = 0;
        _cloudTierReadyWarmupDraws = 0;
        _cloudRuntimeFaulted = false;
        _cloudCameraRegion = null;

        if (gl is null)
        {
            _cloudQuadVao = _cloudQuadVbo = 0;
            return;
        }

        if (_cloudQuadVbo != 0)
        {
            gl.DeleteBuffer(_cloudQuadVbo);
            _cloudQuadVbo = 0;
        }

        if (_cloudQuadVao != 0)
        {
            gl.DeleteVertexArray(_cloudQuadVao);
            _cloudQuadVao = 0;
        }
    }

    private bool CanDrawVolumetricClouds(in PreviewRenderSettingsSnapshot settings) =>
        settings.EnableVolumetricClouds &&
        !_cloudRuntimeFaulted &&
        _cloudProgram is { IsValid: true } &&
        _cloudQuadVao != 0;

    private GlCloudTemporalRenderTarget? ResolveSharedCloudTransmittanceTarget(
        in PreviewRenderSettingsSnapshot settings) =>
        settings.EnableVolumetricClouds &&
        !_cloudRuntimeFaulted &&
        settings.CloudDebugView == PreviewCloudDebugView.Off &&
        _cloudCompositeTarget is { IsValid: true }
            ? _cloudCompositeTarget
            : null;

    private void InvalidateCloudTemporalHistory()
    {
        _cloudHistoryValid = false;
        _cloudFrameIndex = 0;
    }

    private void ObserveCloudCameraRegion(ref GlRenderFrame frame)
    {
        var groundY = PreviewStageConstants.GroundPlaneWorldY;
        var center = PreviewCloudShellGeometry.PlanetCenter(groundY);
        var layerBase = PreviewStageConstants.CloudLayerBaseWorldY(frame.Settings.CloudLayerHeight) - groundY;
        var layerTop = layerBase + Math.Max(frame.Settings.CloudVolumeHeight, 0.01f);
        var region = PreviewCloudShellGeometry.ClassifyCamera(frame.Eye, center, layerBase, layerTop);
        if (_cloudCameraRegion == region)
        {
            return;
        }

        var previous = _cloudCameraRegion?.ToString() ?? "Unknown";
        _cloudCameraRegion = region;
        var radialAltitude = (frame.Eye - center).Length() - PreviewCloudShellGeometry.PlanetRadius;
        var transition = FormattableString.Invariant($"[3D preview] Cloud camera region transition: {previous} -> {region} (radialAltitude={radialAltitude:F3}, layer={layerBase:F3}..{layerTop:F3}, eye=({frame.Eye.X:F3},{frame.Eye.Y:F3},{frame.Eye.Z:F3})).");
        EmitDiagnostic(transition);
        // Persist before issuing the cloud draw. A native driver fault can bypass every managed catch,
        // so this marker records the last shell boundary crossed even in that failure mode.
        LogService.AppendEmergencyDiagnostic("Cloud camera transition", transition);
    }

    private void HandleCloudRuntimeFailure(ref GlRenderFrame frame, string stage, Exception exception)
    {
        _cloudRuntimeFaulted = true;
        _cloudCompositeTarget = null;
        InvalidateCloudTemporalHistory();
        _volumeFroxelHistoryValid = false;
        _volumeIntegrateHistoryValid = false;
        _godRayHistoryValid = false;

        try
        {
            BindDefaultFramebuffer(ref frame);
            frame.Gl.Disable(EnableCap.ScissorTest);
            frame.Gl.Disable(EnableCap.Blend);
            frame.Gl.Enable(EnableCap.DepthTest);
            frame.Gl.DepthMask(true);
        }
        catch (Exception recoveryException)
        {
            LogService.AppendEmergencyDiagnostic(
                "Cloud render-state recovery failure",
                recoveryException.ToString());
        }

        var groundY = PreviewStageConstants.GroundPlaneWorldY;
        var center = PreviewCloudShellGeometry.PlanetCenter(groundY);
        var layerBase = PreviewStageConstants.CloudLayerBaseWorldY(frame.Settings.CloudLayerHeight) - groundY;
        var layerTop = layerBase + Math.Max(frame.Settings.CloudVolumeHeight, 0.01f);
        var radialAltitude = (frame.Eye - center).Length() - PreviewCloudShellGeometry.PlanetRadius;
        var detail = FormattableString.Invariant($"Cloud stage: {stage}\nCamera region: {_cloudCameraRegion?.ToString() ?? "Unknown"}\nEye: ({frame.Eye.X:R}, {frame.Eye.Y:R}, {frame.Eye.Z:R})\nRadial altitude: {radialAltitude:R}; layer: {layerBase:R}..{layerTop:R}\nViewport: {frame.Vw}x{frame.Vh}; cloud quality: {frame.Settings.CloudQuality}; volumetric quality: {frame.Settings.VolumetricQuality}; temporal: {ShouldUseCloudShaderTemporal(frame.Settings)}\nContext: {_glCapabilities?.FormatContextSuffix() ?? "unavailable"}\n{exception}");
        LogService.AppendEmergencyDiagnostic("Volumetric cloud render exception", detail);
        EmitDiagnostic(
            $"[3D preview] Volumetric cloud {stage} failed ({exception.GetType().Name}: {exception.Message}). " +
            "Detailed clouds are disabled for this GPU session; analytic fog/cloud fallback remains active. " +
            $"Emergency log: {LogService.EmergencyLogPath}");
    }

    private bool DrawVolumetricClouds(
        ref GlRenderFrame frame,
        bool deferComposite = false,
        bool? forceTemporal = null,
        bool updateHistory = true)
    {
        if (!CanDrawVolumetricClouds(frame.Settings))
        {
            return false;
        }

        BindDefaultFramebuffer(ref frame);
        return DrawVolumetricCloudsInternal(ref frame, deferComposite, forceTemporal, updateHistory);
    }

    /// <summary>
    /// Cloud temporal reconstruction owns a separate history from god rays and final preview TAA.
    /// Representative-distance rejection prevents the old sky-frustum history leak, so it remains
    /// useful when either of those later passes is active.
    /// </summary>
    private static bool ShouldUseCloudShaderTemporal(in PreviewRenderSettingsSnapshot settings)
    {
        if (settings.CloudDisableTemporal || settings.CloudDebugView != PreviewCloudDebugView.Off)
        {
            return false;
        }

        var profile = PreviewVolumetricQuality.Resolve(settings.VolumetricQuality);
        return profile.CloudTemporalWeight > 0f;
    }

    private static bool CanUseCloudTemporalReproject(in PreviewRenderSettingsSnapshot settings)
    {
        return ShouldUseCloudShaderTemporal(settings);
    }

    private bool DrawVolumetricCloudsInternal(
        ref GlRenderFrame frame,
        bool deferComposite = false,
        bool? forceTemporal = null,
        bool updateHistory = true)
    {
        var settings = frame.Settings;
        var viewProj = frame.Proj * frame.View;
        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
        {
            return false;
        }

        _cloudCompositeTarget = null;

        var gl = frame.Gl;
        var profile = PreviewVolumetricQuality.Resolve(settings.VolumetricQuality);
        var layerWorldY = PreviewStageConstants.CloudLayerBaseWorldY(settings.CloudLayerHeight);
        var useSceneDepth = frame.GodRayCaptureActive && _sceneCapture is { IsValid: true };
        var windTime = settings.CloudFreezeWind ? 0.0 : frame.RenderTime;
        var windOffset = ComputeCloudWindOffset(windTime, settings);
        var cirrusWindOffset = ComputeCirrusWindOffset(windTime, settings);
        var settingsHash = ComputeCloudHistorySettingsHash(settings);
        if (_cloudHistoryValid && (_cloudHistorySettingsHash != settingsHash ||
            Vector3.Distance(frame.Eye, _cloudPrevCameraPos) > Math.Max(settings.CloudVolumeSize, 80f)))
        {
            InvalidateCloudTemporalHistory();
        }

        var temporalAvailable = _cloudTemporalProgram is { IsValid: true } &&
            _cloudRenderTarget is not null && _cloudResolveTarget is not null && _cloudHistoryTarget is not null;
        var useTemporalReproject = (forceTemporal ?? CanUseCloudTemporalReproject(settings)) && temporalAvailable;
        // Every tier traces at half resolution. In particular, Low must never become the
        // most expensive tier merely because its temporal weight is zero.
        var useOffscreen = true;

        if (useOffscreen)
        {
            var w = Math.Max(1, frame.Vw / 2);
            var h = Math.Max(1, frame.Vh / 2);
            if (_cloudHistoryW != w || _cloudHistoryH != h)
            {
                InvalidateCloudTemporalHistory();
                _cloudHistoryW = w;
                _cloudHistoryH = h;
            }

            if (_cloudRenderTarget is null || !_cloudRenderTarget.EnsureSize(w, h))
            {
                if (deferComposite)
                {
                    if (_cloudDeferredCompositeRetries > 0)
                    {
                        _cloudDeferredCompositeRetries--;
                    }

                    return false;
                }

                useOffscreen = false;
                useTemporalReproject = false;
            }
            else if (deferComposite)
            {
                _cloudDeferredCompositeRetries = 0;
            }

            if (useTemporalReproject &&
                (_cloudResolveTarget is null || !_cloudResolveTarget.EnsureSize(w, h) ||
                 _cloudHistoryTarget is null || !_cloudHistoryTarget.EnsureSize(w, h)))
            {
                useTemporalReproject = false;
                InvalidateCloudTemporalHistory();
            }
        }

        if (useOffscreen)
        {
            _cloudRenderTarget!.BindDraw();
            // Transparent black, not the scene clear color: discarded pixels must stay
            // alpha 0 or the composite stamps opaque near-black over the sky between clouds.
            gl.ClearColor(0f, 0f, 0f, 0f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        var jitterPhase = PreviewCloudTemporalJitter.Sample(_cloudFrameIndex);
        BindCloudShaderUniforms(frame, invViewProj, layerWorldY, profile, useSceneDepth,
            windOffset, cirrusWindOffset, jitterPhase);

        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorScissor = gl.IsEnabled(EnableCap.ScissorTest);
        var priorColorMask = new bool[4];
        gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);
        if (useOffscreen)
        {
            gl.Disable(EnableCap.Blend);
        }
        else
        {
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        }

        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.Disable(EnableCap.ScissorTest);
        gl.ColorMask(true, true, true, true);
        FlushPendingGlErrors(gl);
        gl.BindVertexArray(_cloudQuadVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        var cloudDrawErr = gl.GetError();
        gl.BindVertexArray(0);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
        gl.ColorMask(priorColorMask[0], priorColorMask[1], priorColorMask[2], priorColorMask[3]);
        if (priorScissor)
        {
            gl.Enable(EnableCap.ScissorTest);
        }

        if (priorBlend)
        {
            gl.Enable(EnableCap.Blend);
        }
        else
        {
            gl.Disable(EnableCap.Blend);
        }

        if (cloudDrawErr != GLEnum.NoError)
        {
            throw new InvalidOperationException($"Volumetric cloud draw produced GL error {cloudDrawErr}.");
        }

        if (useOffscreen)
        {
            _cloudCompositeTarget = _cloudRenderTarget;
            if (useTemporalReproject && ResolveCloudTemporal(
                    frame, invViewProj, windOffset, cirrusWindOffset, profile))
            {
                _cloudCompositeTarget = _cloudResolveTarget;
                if (updateHistory && _cloudHistoryTarget!.CopyFrom(_cloudResolveTarget!))
                {
                    _cloudPrevViewProj = viewProj;
                    _cloudPrevCameraPos = frame.Eye;
                    _cloudPrevWindOffset = windOffset;
                    _cloudPrevCirrusWindOffset = cirrusWindOffset;
                    _cloudHistorySettingsHash = settingsHash;
                    _cloudHistoryValid = true;
                }
                else if (updateHistory)
                {
                    InvalidateCloudTemporalHistory();
                }
            }
            else if (!useTemporalReproject)
            {
                InvalidateCloudTemporalHistory();
            }

            if (!deferComposite)
            {
                CompositeCloudRenderTargetToDefault(ref frame);
            }
        }

        _cloudFrameIndex = (_cloudFrameIndex + 1) % PreviewCloudTemporalJitter.Period;

        if (useOffscreen && deferComposite)
        {
            BindDefaultFramebuffer(ref frame);
        }

        if (!_loggedCloudDraw)
        {
            _loggedCloudDraw = true;
            var godRays = frame.GodRayCaptureActive && _sceneCapture is { IsValid: true };
            EmitDiagnostic($"[3D preview] Curved-shell volumetric clouds active (sceneDepth={useSceneDepth}, " +
                $"temporalResolve={useTemporalReproject}, cloudDepthHistory={useTemporalReproject}, godRays={godRays}, " +
                $"previewTaa={frame.Settings.EnablePreviewTaa}, warmupDraws={_cloudTierReadyWarmupDraws}, " +
                $"noiseTex={_cloudNoiseTex is not null}, coverageMap={_cloudCoverageTex is not null}).");
        }

        return true;
    }

    private void BindCloudShaderUniforms(
        GlRenderFrame frame,
        Matrix4x4 invViewProj,
        float layerWorldY,
        PreviewVolumetricQuality.Profile profile,
        bool useSceneDepth,
        Vector3 windOffset,
        Vector2 cirrusWindOffset,
        float jitterPhase)
    {
        if (_cloudProgram is not { } program)
        {
            return;
        }

        var gl = frame.Gl;
        var settings = frame.Settings;
        var cu = _cloudUniformLocs;

        // GLES/ANGLE: sampler uniforms default to texture unit 0, and draw validation
        // rejects a program whose samplers of different types (sampler3D uCloudNoise vs
        // the sampler2D uniforms) reference the same unit — the whole cloud quad is then
        // silently dropped with GL_INVALID_OPERATION. On cold start with god rays active,
        // uSceneDepth on the warmup path could otherwise sit on unit 0 alongside uCloudNoise.
        // Pin every sampler to its own unit unconditionally; the uHas* flags keep
        // unbound units from being sampled.
        SetIntOnProgramLoc(program, cu.CloudNoise, 0);
        SetIntOnProgramLoc(program, cu.CoverageMap, 1);
        SetIntOnProgramLoc(program, cu.SkyViewLut, 2);
        SetIntOnProgramLoc(program, cu.DetailNoise, 3);
        SetIntOnProgramLoc(program, cu.SceneDepth, 5);

        SetFloatOnProgramLoc(program, cu.SunIntensity, settings.AtmosphereSunIntensity);
        SetFloatOnProgramLoc(program, cu.SkyExposure, settings.AtmosphereSkyExposure);
        SetFloatOnProgramLoc(program, cu.LayerHeight, layerWorldY);
        SetFloatOnProgramLoc(program, cu.VolumeHeight, settings.CloudVolumeHeight);
        SetFloatOnProgramLoc(program, cu.Density, settings.CloudDensity);
        SetFloatOnProgramLoc(program, cu.CoverageScale, settings.CloudCoverageScale);
        SetFloatOnProgramLoc(program, cu.VolumeSize, settings.CloudVolumeSize);
        SetIntOnProgramLoc(program, cu.Quality, profile.CloudQuality);
        SetIntOnProgramLoc(program, cu.MarchSteps, Math.Clamp(settings.CloudMarchStepOverride, 0, 64));
        SetIntOnProgramLoc(program, cu.DebugView, (int)settings.CloudDebugView);
        SetMatrixOnProgramLoc(program, cu.InvViewProj, invViewProj);
        SetVec3OnProgramLoc(program, cu.CameraPos, frame.Eye);
        SetFloatOnProgramLoc(program, cu.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
        SetFloatOnProgramLoc(program, cu.PlanetRadius, PreviewStageConstants.CloudPlanetRadius);
        SetVec3OnProgramLoc(program, cu.SunDir, frame.LightDir);
        SetVec3OnProgramLoc(program, cu.WindOffset, windOffset);
        SetFloatOnProgramLoc(program, cu.CirrusStrength, settings.CloudCirrusStrength);
        SetVec2OnProgramLoc(program, cu.CirrusWindOffset, cirrusWindOffset);
        SetVec2OnProgramLoc(program, cu.CirrusWindDir, ComputeCirrusWindDirection(settings));
        SetIntOnProgramLoc(program, cu.HasSceneDepth, useSceneDepth ? 1 : 0);
        SetFloatOnProgramLoc(program, cu.FramePhase, jitterPhase);
        SetIntOnProgramLoc(program, cu.HasCloudNoise, _cloudNoiseTex is not null ? 1 : 0);
        SetIntOnProgramLoc(program, cu.HasDetailNoise, _cloudDetailTex is not null ? 1 : 0);
        SetIntOnProgramLoc(program, cu.HasCoverageMap, _cloudCoverageTex is not null ? 1 : 0);
        SetIntOnProgramLoc(program, cu.HasSkyLut, _atmoLutsValid && _atmoSkyViewTex != 0 ? 1 : 0);
        SetIntOnProgramLoc(program, cu.HdrPresent, settings.HdrPresentActive ? 1 : 0);

        if (_cloudNoiseTex is not null)
        {
            _cloudNoiseTex.Bind(0);
        }

        if (_cloudCoverageTex is not null)
        {
            _cloudCoverageTex.Bind(1);
        }

        if (_cloudDetailTex is not null)
        {
            _cloudDetailTex.Bind(3);
        }

        if (_atmoLutsValid && _atmoSkyViewTex != 0)
        {
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2D, _atmoSkyViewTex);
        }

        if (useSceneDepth && _sceneCapture is not null)
        {
            gl.ActiveTexture(TextureUnit.Texture5);
            gl.BindTexture(TextureTarget.Texture2D, _sceneCapture.DepthTextureHandle);
        }
    }

    private bool ResolveCloudTemporal(
        GlRenderFrame frame,
        Matrix4x4 invViewProj,
        Vector3 windOffset,
        Vector2 cirrusWindOffset,
        PreviewVolumetricQuality.Profile profile)
    {
        if (_cloudTemporalProgram is not { IsValid: true } program ||
            _cloudRenderTarget is not { IsValid: true } current ||
            _cloudResolveTarget is not { IsValid: true } resolve ||
            _cloudHistoryTarget is not { IsValid: true } history)
        {
            return false;
        }

        var gl = frame.Gl;
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorScissor = gl.IsEnabled(EnableCap.ScissorTest);
        var priorDepthMask = gl.GetBoolean(GetPName.DepthWritemask);
        var priorColorMask = new bool[4];
        gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);

        resolve.BindDraw();
        gl.ClearColor(0f, 0f, 0f, 0f);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.ScissorTest);
        gl.DepthMask(false);
        gl.ColorMask(true, true, true, true);
        program.Use();

        var tu = _cloudTemporalUniformLocs;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, current.ColorTextureHandle);
        SetIntOnProgramLoc(program, tu.CurrentClouds, 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, current.DataTextureHandle);
        SetIntOnProgramLoc(program, tu.CurrentCloudData, 1);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, history.ColorTextureHandle);
        SetIntOnProgramLoc(program, tu.HistoryClouds, 2);
        gl.ActiveTexture(TextureUnit.Texture3);
        gl.BindTexture(TextureTarget.Texture2D, history.DataTextureHandle);
        SetIntOnProgramLoc(program, tu.HistoryCloudData, 3);

        SetMatrixOnProgramLoc(program, tu.InvViewProj, invViewProj);
        SetMatrixOnProgramLoc(program, tu.PrevViewProj, _cloudPrevViewProj);
        SetVec3OnProgramLoc(program, tu.CameraPos, frame.Eye);
        SetVec3OnProgramLoc(program, tu.PrevCameraPos, _cloudPrevCameraPos);
        var windPeriod = Math.Max(frame.Settings.CloudVolumeSize, 8f) * 4f;
        var windDelta = ComputeWrappedCloudWindDelta(windOffset, _cloudPrevWindOffset, windPeriod);
        SetVec2OnProgramLoc(program, tu.WindDelta, new Vector2(windDelta.X, windDelta.Z));
        SetVec2OnProgramLoc(program, tu.CirrusWindDelta, cirrusWindOffset - _cloudPrevCirrusWindOffset);
        SetVec2OnProgramLoc(program, tu.TexelSize,
            new Vector2(1f / Math.Max(current.Width, 1), 1f / Math.Max(current.Height, 1)));
        SetFloatOnProgramLoc(program, tu.TemporalWeight,
            PreviewVolumetricQuality.EffectivePassTemporalWeight(profile.CloudTemporalWeight, frame.Settings));
        SetIntOnProgramLoc(program, tu.HasHistory, _cloudHistoryValid ? 1 : 0);

        FlushPendingGlErrors(gl);
        gl.BindVertexArray(_cloudQuadVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        var resolveError = gl.GetError();
        gl.BindVertexArray(0);

        gl.DepthMask(priorDepthMask);
        gl.ColorMask(priorColorMask[0], priorColorMask[1], priorColorMask[2], priorColorMask[3]);
        if (priorBlend) gl.Enable(EnableCap.Blend); else gl.Disable(EnableCap.Blend);
        if (priorDepthTest) gl.Enable(EnableCap.DepthTest); else gl.Disable(EnableCap.DepthTest);
        if (priorScissor) gl.Enable(EnableCap.ScissorTest); else gl.Disable(EnableCap.ScissorTest);

        if (resolveError != GLEnum.NoError)
        {
            throw new InvalidOperationException($"Cloud temporal resolve produced GL error {resolveError}.");
        }

        return true;
    }

    private static Vector3 ComputeWrappedCloudWindDelta(Vector3 current, Vector3 previous, float period)
    {
        static float ShortestDelta(float value, float range)
        {
            var half = range * 0.5f;
            if (value > half) return value - range;
            if (value < -half) return value + range;
            return value;
        }

        var delta = current - previous;
        return new Vector3(ShortestDelta(delta.X, period), 0f, ShortestDelta(delta.Z, period));
    }

    private static int ComputeCloudHistorySettingsHash(in PreviewRenderSettingsSnapshot settings)
    {
        var hash = new HashCode();
        hash.Add(settings.VolumetricQuality);
        hash.Add(settings.CloudDensity);
        hash.Add(settings.CloudVolumeSize);
        hash.Add(settings.CloudLayerHeight);
        hash.Add(settings.CloudVolumeHeight);
        hash.Add(settings.CloudCoverageScale);
        hash.Add(settings.CloudWindSpeed);
        hash.Add(settings.CloudWindHeadingDegrees);
        hash.Add(settings.CloudCirrusStrength);
        hash.Add(settings.CloudMarchStepOverride);
        hash.Add(settings.CloudFreezeWind);
        hash.Add(settings.AtmosphereSunIntensity);
        hash.Add(settings.AtmosphereSkyExposure);
        return hash.ToHashCode();
    }

    /// <summary>
    /// World-space wind drift for the cloud field. Components wrap at the weather-map period
    /// (volumeSize * 4); the shape (×2) and detail (×1 in offset space) periods divide it evenly,
    /// so the wrap never produces a visible snap, and floats stay small over long sessions.
    /// </summary>
    private static Vector3 ComputeCloudWindOffset(double renderTime, in PreviewRenderSettingsSnapshot settings)
    {
        var period = Math.Max(settings.CloudVolumeSize, 8f) * 4f;
        var heading = settings.CloudWindHeadingDegrees * (MathF.PI / 180f);
        var travel = renderTime * settings.CloudWindSpeed;
        var wx = (float)((MathF.Cos(heading) * travel) % period);
        var wz = (float)((MathF.Sin(heading) * travel) % period);
        return new Vector3(wx, 0f, wz);
    }

    /// <summary>
    /// High-altitude wind for the cirrus sheet: faster than the cumulus layer and slightly
    /// veered, as real upper winds are. The cirrus noise is procedural (non-tiling), so the
    /// offset stays unwrapped; float precision is ample for multi-hour preview sessions.
    /// </summary>
    private static Vector2 ComputeCirrusWindOffset(double renderTime, in PreviewRenderSettingsSnapshot settings)
    {
        var direction = ComputeCirrusWindDirection(settings);
        var travel = (float)(renderTime * settings.CloudWindSpeed * 2.4);
        return direction * travel;
    }

    private static Vector2 ComputeCirrusWindDirection(in PreviewRenderSettingsSnapshot settings)
    {
        var heading = (settings.CloudWindHeadingDegrees + 18f) * (MathF.PI / 180f);
        return new Vector2(MathF.Cos(heading), MathF.Sin(heading));
    }

    private void CompositeCloudRenderTargetToDefault(ref GlRenderFrame frame)
    {
        var useUpsample = _cloudUpsampleProgram is { IsValid: true };
        var program = useUpsample ? _cloudUpsampleProgram : _godRayCompositeProgram;
        var source = _cloudCompositeTarget ?? _cloudRenderTarget;
        if (source is null || program is not { IsValid: true } || _cloudQuadVao == 0)
        {
            BindDefaultFramebuffer(ref frame);
            return;
        }

        var gl = frame.Gl;
        BindDefaultFramebuffer(ref frame);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorScissor = gl.IsEnabled(EnableCap.ScissorTest);
        var priorColorMask = new bool[4];
        gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.ScissorTest);
        gl.ColorMask(true, true, true, true);
        FlushPendingGlErrors(gl);
        gl.BindVertexArray(_cloudQuadVao);
        program.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, source.ColorTextureHandle);
        if (useUpsample)
        {
            var upu = _cloudUpsampleUniformLocs;
            SetIntOnProgramLoc(program, upu.Clouds, 0);
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
            SetIntOnProgramLoc(program, upu.CloudData, 2);
            SetVec2OnProgramLoc(program, upu.CloudTexelSize, new Vector2(
                1f / Math.Max(source.Width, 1),
                1f / Math.Max(source.Height, 1)));
            var hasDepth = _sceneCapture is { IsValid: true };
            SetIntOnProgramLoc(program, upu.HasSceneDepth, hasDepth ? 1 : 0);
            var viewProj = frame.Proj * frame.View;
            if (Matrix4x4.Invert(viewProj, out var invViewProj))
            {
                SetMatrixOnProgramLoc(program, upu.InvViewProj, invViewProj);
            }
            SetVec3OnProgramLoc(program, upu.CameraPos, frame.Eye);
            SetFloatOnProgramLoc(program, upu.GroundWorldY, PreviewStageConstants.GroundPlaneWorldY);
            SetFloatOnProgramLoc(program, upu.PlanetRadius, PreviewStageConstants.CloudPlanetRadius);
            if (hasDepth)
            {
                gl.ActiveTexture(TextureUnit.Texture1);
                gl.BindTexture(TextureTarget.Texture2D, _sceneCapture!.DepthTextureHandle);
                SetIntOnProgramLoc(program, upu.SceneDepth, 1);
            }
        }
        else
        {
            SetIntOnProgramLoc(program, _cloudCompositeUniformLocs.Rays, 0);
        }

        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.BindVertexArray(0);
        gl.Enable(EnableCap.DepthTest);
        gl.ColorMask(priorColorMask[0], priorColorMask[1], priorColorMask[2], priorColorMask[3]);
        if (priorScissor)
        {
            gl.Enable(EnableCap.ScissorTest);
        }

        if (!priorBlend)
        {
            gl.Disable(EnableCap.Blend);
        }
    }
}
