using System.Numerics;

using AutoPBR.App.Lang;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;
using AutoPBR.App.Services;

using Avalonia.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>OpenGL implementation of <see cref="IRenderPreviewBackend"/>; GPU entry points must run on the OpenGL thread (Avalonia <see cref="AutoPBR.App.Controls.GlPbrPreviewControl"/> callbacks).</summary>
public sealed partial class OpenGlPreviewBackend
{
    private string? _lastRenderFaultSignature;

    /// <summary>Called from <see cref="AutoPBR.App.Controls.GlPbrPreviewControl.OnOpenGlRender"/> only.</summary>
    internal void GlRender(GlInterface glInterface, int framebuffer, int pixelWidth, int pixelHeight)
    {
        if (IsAwaitingDesktopWglSidecar)
        {
            return;
        }

        PreviewDesktopWglContext? sidecar;
        lock (_sync)
        {
            sidecar = _desktopWglSidecar;
        }

        if (sidecar is not null)
        {
            var activeSidecar = sidecar;
            PreviewOpenGlCompositionBridge? compositionBridge;
            lock (_sync)
            {
                compositionBridge = _compositionBridge;
            }

            if (compositionBridge is not null &&
                IsSidecarAdapterMatchComplete &&
                activeSidecar.CanAttemptDxInterop &&
                activeSidecar.TryRenderViaDxInterop(
                    compositionBridge,
                    glInterface,
                    framebuffer,
                    pixelWidth,
                    pixelHeight,
                    fbo => GlRenderCore(fbo, pixelWidth, pixelHeight)))
            {
                lock (_sync)
                {
                    if (!_dxInteropSuccessLogged)
                    {
                        _dxInteropSuccessLogged = true;
                        activeSidecar.EnableDxInteropHangDiagnostics(EmitDiagnostic, RequestPreviewFrame);
                        EmitDiagnostic("[3D preview] D3D11/WGL interop active; async GPU present (timed mutex + timed GPU drain).");
                        RecordActiveContextSummary();
                    }
                }

                return;
            }

            if (compositionBridge is not null)
            {
                lock (_sync)
                {
                    if (!_dxInteropFallbackLogged)
                    {
                        _dxInteropFallbackLogged = true;
                        EmitDiagnostic(activeSidecar.DxInteropOptInEnabled
                            ? "[3D preview] D3D11/WGL interop unavailable; using async PBO sidecar presentation. " +
                              activeSidecar.LastInteropFailureSummary
                            : "[3D preview] D3D11/WGL interop skipped; using stable async PBO sidecar presentation.");
                    }
                }
            }

            var forceSyncPresent = false;
            if (activeSidecar.IsOwnerThreadLikelyWedged)
            {
                ClearPresentationFramebuffer(glInterface, framebuffer, pixelWidth, pixelHeight);
                return;
            }

            lock (_sync)
            {
                if (_forceSyncSidecarPresent)
                {
                    forceSyncPresent = true;
                    _forceSyncSidecarPresent = false;
                }
            }

            try
            {
                activeSidecar.ScheduleAsyncPboFrame(
                    pixelWidth,
                    pixelHeight,
                    fbo => GlRenderCore(fbo, pixelWidth, pixelHeight),
                    forceSyncPresent,
                    RequestPreviewFrame);
                if (!activeSidecar.TryCopyLatestColorToEsFbo(
                        glInterface,
                        framebuffer,
                        pixelWidth,
                        pixelHeight))
                {
                    ClearPresentationFramebuffer(
                        glInterface,
                        framebuffer,
                        pixelWidth,
                        pixelHeight);
                }
            }
            catch (Exception ex)
            {
                EmitDiagnostic(
                    $"[3D preview] Async sidecar PBO presentation failed: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (activeSidecar.UsesAsyncPboReadback)
            {
                lock (_sync)
                {
                    if (!_asyncPboReadbackLogged)
                    {
                        _asyncPboReadbackLogged = true;
                        EmitDiagnostic("[3D preview] Async PBO readback active for sidecar CPU presentation fallback.");
                    }
                }
            }

            return;
        }

        GlRenderCore(framebuffer, pixelWidth, pixelHeight);
    }

    internal void GlRenderNativeWglPresenter(int pixelWidth, int pixelHeight, int framebuffer = 0) =>
        GlRenderCore(framebuffer, pixelWidth, pixelHeight);

    private int _hdrPresentSuppressed;

    internal bool HdrPresentActive =>
        _settings.HdrPresentActive && Volatile.Read(ref _hdrPresentSuppressed) == 0;

    /// <summary>Immediately disable HDR GPU encode/present after a native fault (SDR SwapBuffers fallback).</summary>
    internal void SuppressHdrPresentForSession() => Volatile.Write(ref _hdrPresentSuppressed, 1);

    internal void ClearHdrPresentSuppression() => Volatile.Write(ref _hdrPresentSuppressed, 0);

    internal bool TryGetSilkGl(out GL? gl)
    {
        gl = _gl;
        return gl is not null;
    }

    private void GlRenderCore(int framebuffer, int pixelWidth, int pixelHeight)
    {
        try
        {
            GlRenderCoreUnsafe(framebuffer, pixelWidth, pixelHeight);
        }
        catch (Exception ex)
        {
            HandleUnhandledRenderException(framebuffer, pixelWidth, pixelHeight, ex);
        }
    }

    private void GlRenderCoreUnsafe(int framebuffer, int pixelWidth, int pixelHeight)
    {
        PreviewRenderSettingsSnapshot settings;
        int settingsRevision;
        IRenderPreviewScene? scene;
        PreviewMaterial? material;
        PreviewModelSubject? blockModel;
        PreviewMaterial[]? blockSlots;
        double rotation;
        double renderTime;
        Vector3 orbitBaseTarget;
        Vector3 orbitPan;
        bool flyCamActive;
        Vector3 flyPosition;
        float flyYaw;
        float flyPitch;
        float orbitYaw;
        float orbitPitch;
        float orbitDistance;
        float holdFovZoomMagnification;
        bool drawBootstrapOnly;
        int previewPixelWidth;
        int previewPixelHeight;
        bool meshDirty;
        bool materialDirty;
        lock (_sync)
        {
            if (_gl is null)
            {
                return;
            }

            settings = _settings;
            if (Volatile.Read(ref _hdrPresentSuppressed) != 0 && settings.HdrPresentActive)
            {
                settings = settings with { HdrPresentActive = false };
            }

            settingsRevision = _settingsRevision;
            _previewPixelWidth = Math.Max(1, pixelWidth);
            _previewPixelHeight = Math.Max(1, pixelHeight);
            previewPixelWidth = _previewPixelWidth;
            previewPixelHeight = _previewPixelHeight;
            meshDirty = _meshDirty;
            materialDirty = _materialDirty;

            HandlePendingShaderReloadLocked();
            if (_gpuBootstrap is not null && _desktopWglSidecar is null)
            {
                if (!_gpuBootstrap.IsComplete)
                {
                    _gpuBootstrap.Advance(this, 14.0);
                }

                // Advance may abort bootstrap (e.g. shader link failure) and clear the runner.
                var bootstrap = _gpuBootstrap;
                if (bootstrap is not null)
                {
                    var phase = bootstrap.IsComplete ? PreviewGpuInitPhases.CoreReady : bootstrap.Phase;
                    RaiseGpuInitProgress(phase, settings);
                    if (bootstrap.IsComplete || _gpuBootstrapAborted)
                    {
                        if (bootstrap.IsComplete && _scene is null)
                        {
                            _scene = PreviewStageSceneFactory.CreateIdle(settings);
                            _meshDirty = true;
                        }

                        _gpuBootstrap = null;
                        _gpuBootstrapAborted = false;
                    }
                }
            }
            else if (_gpuBootstrap is not null && _desktopWglSidecar is not null)
            {
                var bootstrap = _gpuBootstrap;
                if (bootstrap is not null)
                {
                    RaiseGpuInitProgress(bootstrap.Phase, settings);
                }
            }

            drawBootstrapOnly = !_gpuAlive || _gpuBootstrap is not null;
            if (drawBootstrapOnly)
            {
                scene = _scene;
                material = null;
                blockModel = null;
                blockSlots = null;
                rotation = 0;
                renderTime = _renderTimeAccum;
                orbitBaseTarget = default;
                orbitPan = default;
                flyCamActive = false;
                flyPosition = default;
                flyYaw = 0;
                flyPitch = 0;
                orbitYaw = 0;
                orbitPitch = 0;
                orbitDistance = 0;
                holdFovZoomMagnification = 1f;
            }
            else if (_program is null || !_program.IsValid || _albedo is null ||
                     _normal is null || _spec is null || _height is null || _mesh is null || _groundMesh is null ||
                     _neutralNormal is null || _neutralSpec is null || _neutralHeight is null)
            {
                return;
            }
            else if ((_grassGroundAlbedo is null || _grassGroundNormal is null ||
                      _grassGroundSpec is null || _grassGroundHeight is null) &&
                     !_grassGroundMaterialDirty)
            {
                // Ground textures missing and nothing queued to re-upload — cannot draw the stage.
                return;
            }
            else
            {
                scene = _scene;
                material = _material;
                blockModel = _blockModelSubject;
                blockSlots = _blockModelSlots;
                rotation = _rotationAccum;
                renderTime = _renderTimeAccum;
                orbitBaseTarget = _orbitBaseTarget;
                orbitPan = _orbitPan;
                flyCamActive = _debugFlyRmbHeld && _flyEngaged;
                flyPosition = _flyPosition;
                flyYaw = _flyYaw;
                flyPitch = _flyPitch;
                orbitYaw = _orbitYaw;
                orbitPitch = _orbitPitch;
                orbitDistance = _orbitDistance;
                holdFovZoomMagnification = _holdFovZoomActive
                    ? Math.Max(_holdFovZoomLevel, 1f)
                    : 1f;
            }
        }

        var gl = _gl!;
        var defaultFbo = framebuffer;
        var vpX = 0;
        var vpY = 0;
        var vw = previewPixelWidth;
        var vh = previewPixelHeight;

        if (defaultFbo != 0)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)defaultFbo);
        }
        else
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        ConfigureDefaultFramebufferColorOutput(gl, defaultFbo);
        gl.Viewport(vpX, vpY, (uint)vw, (uint)vh);
        gl.Disable(EnableCap.ScissorTest);

        if (drawBootstrapOnly)
        {
            // Multi-frame bootstrap: drain any completed occluder bake between steps.
            PumpTerrainOccluderAtlasBootstrap();
            WarmStartTerrainStreamingBootstrap();
            gl.ClearColor(0.01f, 0.012f, 0.02f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            return;
        }

        EnsureGpuTier(settings);
        var frameSettings = ResolveStartupFrameSettings(settings);

        scene ??= PreviewStageSceneFactory.CreateIdle(settings);

        var frame = new GlRenderFrame
        {
            Gl = gl,
            DefaultFbo = defaultFbo,
            VpX = vpX,
            VpY = vpY,
            Vw = vw,
            Vh = vh,
            Settings = frameSettings,
            SettingsRevision = settingsRevision,
            Scene = scene,
            Material = material,
            BlockModel = blockModel,
            BlockSlots = blockSlots,
            Rotation = rotation,
            RenderTime = renderTime,
            OrbitBaseTarget = orbitBaseTarget,
            OrbitPan = orbitPan,
            FlyCamActive = flyCamActive,
            FlyPosition = flyPosition,
            FlyYaw = flyYaw,
            FlyPitch = flyPitch,
            OrbitYaw = orbitYaw,
            OrbitPitch = orbitPitch,
            OrbitDistance = orbitDistance,
            HoldFovZoomMagnification = holdFovZoomMagnification,
            MeshDirty = meshDirty,
            MaterialDirty = materialDirty,
        };

        if (TryPresentHdr2DComposite(ref frame))
        {
            return;
        }

        BeginCpuTimerFrame();
        _ = BeginGpuTimerFrame(gl);
        try
        {
            _frameSubjectGpuUploadsReady = false;
            using (BeginPassTimerScope(GlGpuTimerScope.Setup))
            {
                GlRenderPassSetup(ref frame);
            }

            // Eye / view-proj / frustum before shadow so light-frustum caster cull shares camera eye.
            PopulateCameraMatricesAndFrustum(ref frame);

            using (BeginPassTimerScope(GlGpuTimerScope.Shadow))
            {
                GlRenderPassShadow(ref frame);
            }

            // Scene manages DepthPrepass / HiZ / Scene timer scopes internally (cannot nest queries).
            GlRenderPassScene(ref frame);

            GlRenderPassPost(ref frame);

            using (BeginPassTimerScope(GlGpuTimerScope.Overlay))
            {
                DrawNativeWglOverlayIfNeeded(gl, defaultFbo, vw, vh);
            }
        }
        finally
        {
            FinishFrameSubjectGpuUploads();
            EndPassTimerFrame(renderTime);
        }
    }

    private PreviewRenderSettingsSnapshot ResolveStartupFrameSettings(
        in PreviewRenderSettingsSnapshot settings)
    {
        if (_terrainStartupReadyLatched)
        {
            return settings;
        }

        if (!settings.ShowGroundMesh || ResolveTerrainInitProgressFraction() >= 1.0)
        {
            // Startup readiness is one-way for this GL resource lifetime. Camera movement can
            // temporarily reduce local Full residency, but must never toggle post shaders off.
            _terrainStartupReadyLatched = true;
            return settings;
        }

        // Compile requested post tiers normally, but do not execute the expensive cloud/TAA/AO
        // pipeline against a sky-only viewport. This keeps the WGL driver and terrain latency
        // lane responsive until the camera-local Full pad is paintable.
        return settings with
        {
            EnableGodRays = false,
            EnableVolumetricClouds = false,
            EnableScreenSpaceGodRays = false,
            EnablePreviewTaa = false,
            EnableScreenSpaceAo = false,
        };
    }

    private void HandleUnhandledRenderException(int framebuffer, int pixelWidth, int pixelHeight, Exception exception)
    {
        PreviewRenderSettingsSnapshot settings;
        Vector3 flyPosition;
        float flyYaw;
        float flyPitch;
        lock (_sync)
        {
            settings = _settings;
            flyPosition = _flyPosition;
            flyYaw = _flyYaw;
            flyPitch = _flyPitch;
        }

        var signature = exception.GetType().FullName + ":" + exception.Message;
        if (!string.Equals(_lastRenderFaultSignature, signature, StringComparison.Ordinal))
        {
            _lastRenderFaultSignature = signature;
            var cloudAltitude =
                FormatCloudAltitudeDiagnostic(flyPosition, settings);
            var detail = FormattableString.Invariant($"Framebuffer: {framebuffer}; viewport: {pixelWidth}x{pixelHeight}\nFly camera: ({flyPosition.X:R}, {flyPosition.Y:R}, {flyPosition.Z:R}); yaw={flyYaw:R}; pitch={flyPitch:R}\nClouds: enabled={settings.EnableVolumetricClouds}; runtimeFaulted={_cloudRuntimeFaulted}; altitude={cloudAltitude}; cloudQuality={settings.CloudQuality}; volumetricQuality={settings.VolumetricQuality}\nContext: {_glCapabilities?.FormatContextSuffix() ?? "unavailable"}\n{exception}");
            LogService.AppendEmergencyDiagnostic("3D preview render exception", detail);
            EmitDiagnostic(
                $"[3D preview] Render exception contained ({exception.GetType().Name}: {exception.Message}). " +
                $"Emergency log: {LogService.EmergencyLogPath}");
        }

        try
        {
            var gl = _gl;
            if (gl is null)
            {
                return;
            }

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)Math.Max(framebuffer, 0));
            gl.Viewport(0, 0, (uint)Math.Max(pixelWidth, 1), (uint)Math.Max(pixelHeight, 1));
            gl.Disable(EnableCap.ScissorTest);
            gl.Disable(EnableCap.Blend);
            gl.Enable(EnableCap.DepthTest);
            gl.DepthMask(true);
            gl.ClearColor(0.01f, 0.012f, 0.02f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        }
        catch (Exception recoveryException)
        {
            LogService.AppendEmergencyDiagnostic(
                "3D preview render recovery failure",
                recoveryException.ToString());
        }
    }

    private static void ClearPresentationFramebuffer(GlInterface glInterface, int framebuffer, int width, int height)
    {
        var esGl = GL.GetApi(glInterface.GetProcAddress);
        esGl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)framebuffer);
        esGl.Viewport(0, 0, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
        esGl.ClearColor(0.01f, 0.012f, 0.02f, 1f);
        esGl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }
}
