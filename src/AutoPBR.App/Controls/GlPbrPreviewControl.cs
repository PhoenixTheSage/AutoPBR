using System.Diagnostics;
using System.Numerics;
using System.Reflection;

using System.Runtime.InteropServices;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

using Avalonia;
using JetBrains.Annotations;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Controls;

/// <summary>
/// OpenGL PBR preview. Hosts either an ANGLE <see cref="OpenGlControlBase"/> or a native WGL child window.
/// </summary>
public sealed class GlPbrPreviewControl : UserControl, ICustomHitTest, IDisposable
{
    public static readonly StyledProperty<string?> OverlayDebugTextProperty =
        AvaloniaProperty.Register<GlPbrPreviewControl, string?>(nameof(OverlayDebugText));

    public static readonly StyledProperty<string?> OverlayFpsTextProperty =
        AvaloniaProperty.Register<GlPbrPreviewControl, string?>(nameof(OverlayFpsText));

    public static readonly StyledProperty<string?> OverlayCpuTextProperty =
        AvaloniaProperty.Register<GlPbrPreviewControl, string?>(nameof(OverlayCpuText));

    public static readonly StyledProperty<bool> OverlayFpsVisibleProperty =
        AvaloniaProperty.Register<GlPbrPreviewControl, bool>(nameof(OverlayFpsVisible));

    private static readonly FieldInfo? UpdateQueuedField = typeof(OpenGlControlBase)
        .GetField("_updateQueued", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly OpenGlPreviewBackend _backend = new();
    private readonly AngleOpenGlSurface _angleSurface;
    private readonly PreviewNativeWglHost _nativeHost;
    private long _lastTicks = Stopwatch.GetTimestamp();
    private double _fpsAccumSeconds;
    private int _fpsFrameCount;
    private double _smoothedFps;
    private bool _presentationVsyncEnabled;
    private bool _disposed;
    private bool _gpuCoreReadyFrameRequested;
    private int _openGlUpdateQueuedResets;
    private long _lastOpenGlRenderTicks;
    private GlInterface? _glInterface;
    private GlInterface? _angleGlInterface;
    private PreviewNativeWglPresenter? _nativeWglPresenter;
    private PreviewHdrDxgiSwapchain? _hdrSwapchain;
    private IntPtr _nativeHwnd;
    private bool _hdrPresentPathFailed;
    private bool _hdrYFlipFailureLogged;
    private bool _hdrYFlipPathLogged;
    private bool _hdrTearingLogged;
    private bool _hdrPresentResolvePathLogged;
    private bool _anglePathStarted;
    private bool _backendInitialized;
    private bool _lastRaisedHdrNativeWglActive;
    private bool _lastRaisedHdrPresentPathFailed;
    private bool _lastRaisedHdrDisplaySupportsHdr;
    private float _lastRaisedHdrPeakNits;
    private PreviewHdrDisplayInfo _cachedHdrDisplayInfo = PreviewHdrDisplayInfo.Unsupported;
    private long _cachedHdrProbeTicks;
    private static readonly long HdrProbeCacheTicks = Stopwatch.Frequency; // ~1s

    /// <summary>Raised on the UI thread when HDR display probe / present status changes.</summary>
    public event Action<PreviewHdrDisplayInfo, bool, bool>? HdrProbeUpdated;
    private int _cachedPreviewPixelWidth = 1;
    private int _cachedPreviewPixelHeight = 1;
    /// <summary>
    /// Only clear Avalonia's <c>_updateQueued</c> after this long without <see cref="OnOpenGlRender"/>.
    /// Clearing a live queued flag races the compositor and recreates Avalonia #17865.
    /// </summary>
    private const double StuckOpenGlUpdateQueuedMs = 250.0;
    private Key _cameraResetKey = Key.R;
    private bool _cameraDrag;
    private bool _cameraDragIsOrbit;
    private bool _debugFlyRmbLook;
    private bool _flyKeyW;
    private bool _flyKeyA;
    private bool _flyKeyS;
    private bool _flyKeyD;
    private bool _flyKeyQ;
    private bool _flyKeyE;
    private bool _flySpeedBoost;
    private bool _flySpeedSlow;
    private Point _cameraDragLast;
    private DateTime _lastLeftClickUtc = DateTime.MinValue;
    private Point _lastLeftClickPos;
    private const int DoubleClickMs = 400;
    private const double DoubleClickMaxDist = 8;

    internal bool PresentationVsyncEnabled => _presentationVsyncEnabled;

    public string? OverlayDebugText
    {
        get => GetValue(OverlayDebugTextProperty);
        set => SetValue(OverlayDebugTextProperty, value);
    }

    public string? OverlayFpsText
    {
        get => GetValue(OverlayFpsTextProperty);
        set => SetValue(OverlayFpsTextProperty, value);
    }

    public string? OverlayCpuText
    {
        get => GetValue(OverlayCpuTextProperty);
        set => SetValue(OverlayCpuTextProperty, value);
    }

    public bool OverlayFpsVisible
    {
        get => GetValue(OverlayFpsVisibleProperty);
        set => SetValue(OverlayFpsVisibleProperty, value);
    }

    public GlPbrPreviewControl()
    {
        ClipToBounds = true;
        Focusable = true;
        IsTabStop = false;
        _angleSurface = new AngleOpenGlSurface(this)
        {
            IsVisible = !PreviewOpenGlSession.RequestedDesktopGl4
        };
        _nativeHost = new PreviewNativeWglHost
        {
            IsVisible = PreviewOpenGlSession.RequestedDesktopGl4 && OperatingSystem.IsWindows()
        };
        _nativeHost.NativeWindowCreated += OnNativeHostWindowCreated;
        _nativeHost.NativeWindowDestroyed += OnNativeHostWindowDestroyed;
        _nativeHost.NativeWindowCreationFailed += StartAngleFallbackFromNativeWgl;
        _nativeHost.NativePointerPressed += OnNativeHostPointerPressed;
        _nativeHost.NativePointerMoved += OnNativeHostPointerMoved;
        _nativeHost.NativePointerReleased += OnNativeHostPointerReleased;
        _nativeHost.NativePointerWheel += OnNativeHostPointerWheel;
        _nativeHost.NativeKeyDown += OnNativeHostKeyDown;
        _nativeHost.NativeKeyUp += OnNativeHostKeyUp;
        _nativeHost.NativeInputLost += OnNativeHostInputLost;
        Content = new Grid
        {
            ClipToBounds = true,
            Children =
            {
                _angleSurface,
                _nativeHost
            }
        };

        // Watchdog / idle recovery: unstick only when OnOpenGlRender has been silent.
        _backend.SetRequestPreviewFrame(RecoverPreviewFrame);
        EnsureBackendInitialized();
        PointerPressed += OnPreviewPointerPressed;
        PointerMoved += OnPreviewPointerMoved;
        PointerReleased += OnPreviewPointerReleased;
        PointerWheelChanged += OnPreviewPointerWheelChanged;
        PointerEntered += OnPreviewPointerEntered;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    /// <inheritdoc />
    public bool HitTest(Point point) =>
        Bounds is { Width: > 0, Height: > 0 } && new Rect(Bounds.Size).Contains(point);

    public IRenderPreviewBackend Backend => _backend;

    /// <summary>Frames per second averaged over the last ~0.5s of rendered preview frames.</summary>
    public double SmoothedPreviewFps => Volatile.Read(ref _smoothedFps);

    public string? LatestGpuTimingHudText => _backend.LatestGpuTimingHudText;

    public string? LatestCpuTimingHudText => _backend.LatestCpuTimingHudText;

    public bool TryGetPreviewViewportInfo(out int pixelWidth, out int pixelHeight,
        out double logicalWidth, out double logicalHeight, out double renderScale)
    {
        renderScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        logicalWidth = Bounds.Width;
        logicalHeight = Bounds.Height;
        pixelWidth = Math.Max(1, (int)Math.Ceiling(logicalWidth * renderScale));
        pixelHeight = Math.Max(1, (int)Math.Ceiling(logicalHeight * renderScale));
        return logicalWidth > 0.0 && logicalHeight > 0.0;
    }

    /// <summary>Routes shader and GL init messages into the main app log (invoked from the GL thread).</summary>
    public void SetRendererLog(Action<string>? log) => _backend.SetDiagnosticLog(log);

    public void InvalidateShaderCaches() => _backend.InvalidateShaderCachesAndReload();

    /// <summary>When true, native WGL presentation uses swap interval 1; false sets swap interval 0 and removes app-side frame delays.</summary>
    public void SetPreviewVSync(bool enabled)
    {
        if (_presentationVsyncEnabled == enabled)
        {
            return;
        }

        _presentationVsyncEnabled = enabled;
        ApplyPresentationVsync();
        RecoverPreviewFrame();
    }

    private void ApplyPresentationVsync()
    {
        if (_nativeWglPresenter is { } nativePresenter)
        {
            nativePresenter.ConfigureVsync(_presentationVsyncEnabled);
            return;
        }

        if (_glInterface is null)
        {
            return;
        }

        _backend.ConfigurePresentationVsync(_glInterface, _presentationVsyncEnabled);
    }

    /// <summary>Updates orbit boom arm length, orbit/pan/zoom sensitivities, and the reset-camera key.</summary>
    public void SetCameraInteractionFromSettings(float orbitRadPerPx, float panPerPixel, float zoomPerWheelStep,
        float flyLookRadPerPx, bool invertLookY, float flyMoveSpeed, bool flySmoothAcceleration,
        Key resetKey, float orbitBoomArmDistanceWorld)
    {
        _cameraResetKey = resetKey;
        _backend.SetCameraSensitivities(orbitRadPerPx, panPerPixel, zoomPerWheelStep, flyLookRadPerPx, invertLookY,
            flyMoveSpeed, flySmoothAcceleration);
        _backend.SetOrbitBoomArmDistance(orbitBoomArmDistanceWorld);
    }

    /// <summary>Updates render settings only (no scene/block-model re-push).</summary>
    public void UpdatePreview3DSettings(PreviewRenderSettings settings)
    {
        void Core()
        {
            _backend.SetRenderSettings(settings);
            // Settings-only pushes used to leave a null/stale scene after GPU init; keep the
            // idle stage alive whenever there is no subject so terrain still renders.
            if (!settings.DrawPreviewSubject)
            {
                _backend.SetScene(PreviewStageSceneFactory.CreateIdle(settings));
                _backend.SetBlockModelPreview(null, null);
            }

            RecoverPreviewFrame();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Core();
        }
        else
        {
            Dispatcher.UIThread.Post(Core);
        }
    }

    /// <summary>Updates scene, GPU settings, and material on the UI thread.</summary>
    public void UpdatePreview3D(
        PreviewMaterial material,
        PreviewRenderSettings settings,
        PreviewSceneKind kind,
        PreviewModelSubject? javaBlockModel = null,
        PreviewMaterial[]? javaSlotMaterials = null)
    {
        void Core()
        {
            PreviewScene scene;
            if (javaBlockModel is not null && javaSlotMaterials is not null)
            {
                var displaySubject = PreviewSubjectPlacement.LiftSubjectIfClipping(javaBlockModel);
                var stride = displaySubject.VertexStrideFloats > 0
                    ? displaySubject.VertexStrideFloats
                    : PreviewMesh.FloatsPerVertex;
                var orbitTarget = displaySubject.EmulatedRebake is not null
                    ? EntityPreviewPlacement.ComputeEntityOrbitTarget(displaySubject.InterleavedVertices, stride)
                    : (Vector3?)null;
                // Emulated entities upload geometry only via OpenGL TryRebakeMesh commit — never from scene mesh.
                var mesh = displaySubject.EmulatedRebake is not null
                    ? PreviewMeshFactory.CreateEmptySubjectPlaceholder("entity_rebake_pending")
                    : new PreviewMesh
                    {
                        Name = "java_block_model",
                        InterleavedVertices = displaySubject.InterleavedVertices,
                        Indices = displaySubject.Indices
                    };
                scene = BlockModelPreviewSceneFactory.Create(settings, mesh, orbitTarget);
                javaBlockModel = displaySubject;
            }
            else if (kind == PreviewSceneKind.ItemPlane)
            {
                scene = ItemPreviewSceneFactory.Create(settings, material);
            }
            else if (!settings.DrawPreviewSubject)
            {
                // No texture/subject selected: keep the stage (terrain/sky/grid) alive without a unit cube.
                scene = PreviewStageSceneFactory.CreateIdle(settings);
            }
            else
            {
                scene = BlockPreviewSceneFactory.Create(settings);
            }

            _backend.SetScene(scene);
            _backend.SetRenderSettings(settings);
            _backend.SetMaterial(material);
            _backend.SetBlockModelPreview(javaBlockModel, javaSlotMaterials);
            RecoverPreviewFrame();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Core();
        }
        else
        {
            Dispatcher.UIThread.Post(Core);
        }
    }

    /// <summary>Updates the LabPBR ground plane material (grass_block_top).</summary>
    [UsedImplicitly]
    public void SetGroundMaterial(PreviewMaterial? material)
    {
        void Core()
        {
            _backend.SetGroundMaterial(material);
            RecoverPreviewFrame();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Core();
        }
        else
        {
            Dispatcher.UIThread.Post(Core);
        }
    }

    /// <summary>Updates multi-slot LabPBR ground materials (BlockModel-style terrain).</summary>
    public void SetGroundMaterials(
        PreviewMaterial[]? slotMaterials,
        bool overlayIsCutout = true,
        bool[]? cutoutBySlot = null)
    {
        void Core()
        {
            _backend.SetGroundMaterials(slotMaterials, overlayIsCutout, cutoutBySlot);
            RecoverPreviewFrame();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Core();
        }
        else
        {
            Dispatcher.UIThread.Post(Core);
        }
    }

    /// <summary>Updates Full-chunk terrain grass bake flags and invalidates resident meshes.</summary>
    public void SetTerrainGrassBakeSettings(PreviewTerrainGrassBakeSettings settings)
    {
        void Core()
        {
            _backend.SetTerrainGrassBakeSettings(settings);
            RecoverPreviewFrame();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Core();
        }
        else
        {
            Dispatcher.UIThread.Post(Core);
        }
    }

    /// <summary>Updates vegetation decoration bake plan and invalidates resident meshes.</summary>
    public void SetTerrainVegetationBakePlan(PreviewTerrainVegetationBakePlan? plan)
    {
        void Core()
        {
            _backend.SetTerrainVegetationBakePlan(plan);
            RecoverPreviewFrame();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Core();
        }
        else
        {
            Dispatcher.UIThread.Post(Core);
        }
    }

    private void EnsureBackendInitialized()
    {
        if (_backendInitialized)
        {
            return;
        }

        _backendInitialized = true;
        _backend.GpuInitProgressChanged += OnGpuInitProgressChanged;
        _backend.Initialize(new RenderPreviewInitializationOptions());
    }

    private void OnAngleOpenGlInit(GlInterface gl)
    {
        EnsureBackendInitialized();
        _angleGlInterface = gl;
        _glInterface = gl;
        _gpuCoreReadyFrameRequested = false;
        UpdateCachedPreviewViewportInfo();
        if (_nativeWglPresenter is not null)
        {
            return;
        }

        StartAngleOpenGlPath(gl);
    }

    private void StartAngleOpenGlPath(GlInterface gl)
    {
        if (_anglePathStarted)
        {
            return;
        }

        _anglePathStarted = true;
        if (PreviewOpenGlCompositionBridge.TryCreate(_angleSurface, out var compositionBridge))
        {
            _backend.SetCompositionBridge(compositionBridge);
            compositionBridge.TryWarmPresentationCache();
        }

        _backend.GlInit(gl);
        ApplyPresentationVsync();
        RaiseHdrProbeUpdated(
            TryProbeHdrDisplayForTopLevel(),
            nativeWglActive: false,
            presentPathFailed: false);
        if (PreviewOpenGlSession.RequestedDesktopGl4)
        {
            _backend.ScheduleDesktopWglSidecarInit(RecoverPreviewFrame);
        }
    }

    private PreviewHdrDisplayInfo TryProbeHdrDisplayForTopLevel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return PreviewHdrDisplayInfo.Unsupported;
        }

        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.TryGetPlatformHandle() is { Handle: var hwnd } && hwnd != IntPtr.Zero)
            {
                return PreviewHdrDisplayProbe.ProbeForWindow(hwnd);
            }
        }
        catch
        {
            // Probe is best-effort for status text.
        }

        return PreviewHdrDisplayInfo.Unsupported;
    }

    private void OnNativeHostWindowCreated(IntPtr hwnd)
    {
        EnsureBackendInitialized();
        if (!PreviewOpenGlSession.RequestedDesktopGl4 || _disposed)
        {
            return;
        }

        UpdateCachedPreviewViewportInfo();
        if (!TryStartNativeWglPresenter(hwnd))
        {
            StartAngleFallbackFromNativeWgl();
        }
    }

    private void OnNativeHostWindowDestroyed(IntPtr hwnd)
    {
        _ = hwnd;
        _nativeWglPresenter?.Dispose();
        _nativeWglPresenter = null;
        _hdrSwapchain?.Dispose();
        _hdrSwapchain = null;
        _nativeHwnd = IntPtr.Zero;
        _hdrPresentPathFailed = false;
        _hdrYFlipFailureLogged = false;
        _hdrYFlipPathLogged = false;
        _hdrTearingLogged = false;
        _hdrPresentResolvePathLogged = false;
        RaiseHdrProbeUpdated(PreviewHdrDisplayInfo.Unsupported, nativeWglActive: false, presentPathFailed: false);
    }

    private bool TryStartNativeWglPresenter(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
        {
            return false;
        }

        _nativeWglPresenter?.Dispose();
        var presenter = new PreviewNativeWglPresenter(
            this,
            _backend,
            StartAngleFallbackFromNativeWgl,
            hwnd);
        if (!presenter.TryAttach())
        {
            presenter.Dispose();
            return false;
        }

        _nativeWglPresenter = presenter;
        _nativeHwnd = hwnd;
        return true;
    }

    /// <summary>Upload a 2D preview composite for HDR scRGB present (sRGB 8-bit RGBA).</summary>
    public void SetHdr2DCompositeRgba(byte[]? rgba, int width, int height) =>
        _backend.SetHdr2DCompositeRgba(rgba, width, height);

    public void SetPreferHdr2DPresent(bool prefer) => _backend.SetPreferHdr2DPresent(prefer);

    private void RaiseHdrProbeUpdated(in PreviewHdrDisplayInfo display, bool nativeWglActive, bool presentPathFailed)
    {
        // Avoid per-frame UI posts (each one re-pushes render settings and can stall recovery).
        if (_lastRaisedHdrNativeWglActive == nativeWglActive &&
            _lastRaisedHdrPresentPathFailed == presentPathFailed &&
            _lastRaisedHdrDisplaySupportsHdr == display.SupportsHdr &&
            Math.Abs(_lastRaisedHdrPeakNits - display.MaxLuminanceNits) < 0.5f)
        {
            return;
        }

        _lastRaisedHdrNativeWglActive = nativeWglActive;
        _lastRaisedHdrPresentPathFailed = presentPathFailed;
        _lastRaisedHdrDisplaySupportsHdr = display.SupportsHdr;
        _lastRaisedHdrPeakNits = display.MaxLuminanceNits;

        var handler = HdrProbeUpdated;
        if (handler is null)
        {
            return;
        }

        var copy = display;
        void Raise() => handler(copy, nativeWglActive, presentPathFailed);
        if (Dispatcher.UIThread.CheckAccess())
        {
            Raise();
        }
        else
        {
            Dispatcher.UIThread.Post(Raise, DispatcherPriority.Background);
        }
    }

    private void StartAngleFallbackFromNativeWgl()
    {
        _nativeWglPresenter?.Dispose();
        _nativeWglPresenter = null;
        _backend.SetNativeWglOverlay(null, null, null, 0);
        _nativeHost.IsVisible = false;
        _angleSurface.IsVisible = true;
        if (_angleGlInterface is { } gl)
        {
            StartAngleOpenGlPath(gl);
            RecoverPreviewFrame();
        }
    }

    private void OnAngleOpenGlDeinit(GlInterface gl)
    {
        _angleGlInterface = null;
        _backend.SetCompositionBridge(null);
        if (!_anglePathStarted)
        {
            return;
        }

        _anglePathStarted = false;
        _glInterface = _nativeWglPresenter?.GlInterface;
        _backend.GlDeinit(gl);
    }

    private void OnGpuInitProgressChanged(PreviewGpuInitProgress progress)
    {
        if (!progress.CoreReady || _gpuCoreReadyFrameRequested)
        {
            return;
        }

        _gpuCoreReadyFrameRequested = true;
        // GpuInitProgressChanged can fire from the WGL owner thread during sidecar bootstrap/render.
        Dispatcher.UIThread.Post(RecoverPreviewFrame, DispatcherPriority.Background);
    }

    private void OnAngleOpenGlRender(GlInterface gl, int fb)
    {
        if (_nativeWglPresenter is not null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var dt = TimeSpan.FromTicks(now - _lastTicks);
        _lastTicks = now;
        PushDebugFlyInput();
        _backend.RenderFrame(dt);
        RecordRenderFrame(dt);
        TryGetPreviewViewportInfo(out var w, out var h, out _, out _, out _);
        _backend.GlRender(gl, fb, w, h);
        Volatile.Write(ref _lastOpenGlRenderTicks, now);
        // Avalonia #17865: never call RequestNextFrameRendering synchronously from OnOpenGlRender,
        // and never Post at Render priority (still inside CommitCompositor). Defer to Background.
        QueueContinuousFrameIfNeeded();
    }

    private void QueueContinuousFrameIfNeeded()
    {
        if (!_backend.NeedsContinuousRendering)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            RequestContinuousFrameIfNeeded();
            return;
        }

        Dispatcher.UIThread.Post(RequestContinuousFrameIfNeeded, DispatcherPriority.Background);
    }

    private void RequestContinuousFrameIfNeeded()
    {
        if (_disposed || !_backend.NeedsContinuousRendering)
        {
            return;
        }

        RequestNextFrameRenderingCore();
    }

    /// <summary>
    /// Recovery path for present-idle / user input when Avalonia left <c>_updateQueued</c> stuck.
    /// </summary>
    private void RecoverPreviewFrame()
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                TryUnstickUpdateQueuedIfStale();
                RequestNextFrameRenderingCore();
            },
            DispatcherPriority.Background);
    }

    private void RequestNextFrameRenderingCore()
    {
        if (_disposed)
        {
            return;
        }

        if (_nativeWglPresenter is { } nativePresenter)
        {
            UpdateCachedPreviewViewportInfo();
            nativePresenter.RequestFrame();
            return;
        }

        _angleSurface.RequestFrame();
    }

    private void TryUnstickUpdateQueuedIfStale()
    {
        if (_nativeWglPresenter is not null)
        {
            return;
        }

        if (UpdateQueuedField?.GetValue(_angleSurface) is not true)
        {
            return;
        }

        var last = Volatile.Read(ref _lastOpenGlRenderTicks);
        var ageMs = last == 0
            ? double.PositiveInfinity
            : (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;
        if (ageMs < StuckOpenGlUpdateQueuedMs)
        {
            return;
        }

        UpdateQueuedField.SetValue(_angleSurface, false);
        var resets = Interlocked.Increment(ref _openGlUpdateQueuedResets);
        if (resets == 1 || resets % 10 == 0)
        {
            _backend.EmitPreviewDiagnostic(
                $"[3D preview] Cleared stuck OpenGlControlBase._updateQueued after {ageMs:0}ms without render (count={resets}).");
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OverlayDebugTextProperty ||
            change.Property == OverlayFpsTextProperty ||
            change.Property == OverlayCpuTextProperty ||
            change.Property == OverlayFpsVisibleProperty)
        {
            UpdateNativeWglOverlayBitmaps();
            return;
        }

        if (change.Property != BoundsProperty)
        {
            if (change.Property == IsVisibleProperty)
            {
                if (IsVisible)
                {
                    RecoverPreviewFrame();
                }
            }

            return;
        }

        TryGetPreviewViewportInfo(out var w, out var h, out _, out _, out _);
        Volatile.Write(ref _cachedPreviewPixelWidth, w);
        Volatile.Write(ref _cachedPreviewPixelHeight, h);
        _backend.Resize(w, h);
        UpdateNativeWglOverlayBitmaps();
        RecoverPreviewFrame();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = HandlePreviewKeyDown(e.Key);

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = HandlePreviewKeyUp(e.Key);

        base.OnKeyUp(e);
    }

    private bool HandlePreviewKeyDown(Key key)
    {
        if (key == _cameraResetKey)
        {
            _backend.ResetPreviewCameraToDefaults();
            RecoverPreviewFrame();
            return true;
        }

        var handled = true;
        switch (key)
        {
            case Key.W:
                _flyKeyW = true;
                break;
            case Key.A:
                _flyKeyA = true;
                break;
            case Key.S:
                _flyKeyS = true;
                break;
            case Key.D:
                _flyKeyD = true;
                break;
            case Key.Q:
                _flyKeyQ = true;
                break;
            case Key.E:
                _flyKeyE = true;
                break;
            case Key.LeftShift:
            case Key.RightShift:
                _flySpeedBoost = true;
                break;
            case Key.LeftCtrl:
            case Key.RightCtrl:
                _flySpeedSlow = true;
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            PushDebugFlyInput();
            RecoverPreviewFrame();
        }

        return handled;
    }

    private bool HandlePreviewKeyUp(Key key)
    {
        var handled = true;
        switch (key)
        {
            case Key.W:
                _flyKeyW = false;
                break;
            case Key.A:
                _flyKeyA = false;
                break;
            case Key.S:
                _flyKeyS = false;
                break;
            case Key.D:
                _flyKeyD = false;
                break;
            case Key.Q:
                _flyKeyQ = false;
                break;
            case Key.E:
                _flyKeyE = false;
                break;
            case Key.LeftShift:
            case Key.RightShift:
                _flySpeedBoost = false;
                break;
            case Key.LeftCtrl:
            case Key.RightCtrl:
                _flySpeedSlow = false;
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            PushDebugFlyInput();
            RecoverPreviewFrame();
        }

        return handled;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _cameraDrag = false;
        _debugFlyRmbLook = false;
        PushDebugFlyInput();
        _backend.SetUserCameraDragging(false);
    }

    private void PushDebugFlyInput() =>
        _backend.SetDebugFlyInput(_debugFlyRmbLook, _flyKeyW, _flyKeyA, _flyKeyS, _flyKeyD, _flyKeyQ, _flyKeyE,
            _flySpeedBoost, _flySpeedSlow);

    private void OnPreviewPointerEntered(object? sender, PointerEventArgs e)
    {
        Focus();
    }

    private void OnNativeHostPointerPressed(PreviewNativeWglMouseButton button, PreviewNativeWglPointerEvent e)
    {
        Focus();
        var pos = NativePointToLogical(e);
        if (button == PreviewNativeWglMouseButton.Right)
        {
            _debugFlyRmbLook = true;
            _cameraDragLast = pos;
            PushDebugFlyInput();
            _backend.SetUserCameraDragging(true);
            RecoverPreviewFrame();
            return;
        }

        if (button is not (PreviewNativeWglMouseButton.Left or PreviewNativeWglMouseButton.Middle))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var clickDx = pos.X - _lastLeftClickPos.X;
        var clickDy = pos.Y - _lastLeftClickPos.Y;
        if (button == PreviewNativeWglMouseButton.Left &&
            (now - _lastLeftClickUtc).TotalMilliseconds <= DoubleClickMs &&
            clickDx * clickDx + clickDy * clickDy <= DoubleClickMaxDist * DoubleClickMaxDist)
        {
            _backend.FocusOrbitOnSubject();
            _lastLeftClickUtc = DateTime.MinValue;
            RecoverPreviewFrame();
            return;
        }

        if (button == PreviewNativeWglMouseButton.Left)
        {
            _lastLeftClickUtc = now;
            _lastLeftClickPos = pos;
        }

        _cameraDrag = true;
        _cameraDragIsOrbit = e.Modifiers.HasFlag(PreviewNativeWglKeyModifiers.Alt);
        _cameraDragLast = pos;
        _backend.SetUserCameraDragging(true);
        RecoverPreviewFrame();
    }

    private void OnNativeHostPointerMoved(PreviewNativeWglPointerEvent e)
    {
        Focus();
        var pos = NativePointToLogical(e);
        if (_debugFlyRmbLook)
        {
            var dx = (float)(pos.X - _cameraDragLast.X);
            var dy = (float)(pos.Y - _cameraDragLast.Y);
            _cameraDragLast = pos;
            _backend.ApplyFlyLookPixels(dx, dy);
            RecoverPreviewFrame();
            return;
        }

        if (!_cameraDrag)
        {
            return;
        }

        var dx2 = (float)(pos.X - _cameraDragLast.X);
        var dy2 = (float)(pos.Y - _cameraDragLast.Y);
        _cameraDragLast = pos;
        if (_cameraDragIsOrbit)
        {
            _backend.ApplyCameraOrbitPixels(dx2, dy2);
        }
        else
        {
            _backend.ApplyCameraPanPixels(dx2, dy2);
        }

        RecoverPreviewFrame();
    }

    private void OnNativeHostPointerReleased(PreviewNativeWglMouseButton button, PreviewNativeWglPointerEvent e)
    {
        _ = e;
        if (button == PreviewNativeWglMouseButton.Right)
        {
            _debugFlyRmbLook = false;
            PushDebugFlyInput();
            _backend.SetUserCameraDragging(false);
            RecoverPreviewFrame();
            return;
        }

        if (button is not (PreviewNativeWglMouseButton.Left or PreviewNativeWglMouseButton.Middle))
        {
            return;
        }

        _cameraDrag = false;
        _backend.SetUserCameraDragging(false);
        RecoverPreviewFrame();
    }

    private void OnNativeHostPointerWheel(PreviewNativeWglPointerEvent e, int delta)
    {
        _ = e;
        Focus();
        if (delta == 0)
        {
            return;
        }

        _backend.ApplyCameraZoom(delta / 120.0f);
        RecoverPreviewFrame();
    }

    private void OnNativeHostKeyDown(int virtualKey)
    {
        if (TryMapVirtualKey(virtualKey, out var key))
        {
            _ = HandlePreviewKeyDown(key);
        }
    }

    private void OnNativeHostKeyUp(int virtualKey)
    {
        if (TryMapVirtualKey(virtualKey, out var key))
        {
            _ = HandlePreviewKeyUp(key);
        }
    }

    private void OnNativeHostInputLost()
    {
        _cameraDrag = false;
        _debugFlyRmbLook = false;
        _flyKeyW = _flyKeyA = _flyKeyS = _flyKeyD = _flyKeyQ = _flyKeyE = false;
        _flySpeedBoost = false;
        _flySpeedSlow = false;
        PushDebugFlyInput();
        _backend.SetUserCameraDragging(false);
        RecoverPreviewFrame();
    }

    private Point NativePointToLogical(PreviewNativeWglPointerEvent e)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0.0)
        {
            scale = 1.0;
        }

        return new Point(e.X / scale, e.Y / scale);
    }

    private static bool TryMapVirtualKey(int virtualKey, out Key key)
    {
        key = virtualKey switch
        {
            0x08 => Key.Back,
            0x1B => Key.Escape,
            0x20 => Key.Space,
            0x2E => Key.Delete,
            0x24 => Key.Home,
            0x41 => Key.A,
            0x44 => Key.D,
            0x45 => Key.E,
            0x51 => Key.Q,
            0x52 => Key.R,
            0x53 => Key.S,
            0x57 => Key.W,
            0x70 => Key.F1,
            0x71 => Key.F2,
            0x72 => Key.F3,
            0x73 => Key.F4,
            0x74 => Key.F5,
            0x75 => Key.F6,
            0x76 => Key.F7,
            0x77 => Key.F8,
            0x78 => Key.F9,
            0x79 => Key.F10,
            0x7A => Key.F11,
            0x7B => Key.F12,
            0xA0 => Key.LeftShift,
            0xA1 => Key.RightShift,
            0xA2 => Key.LeftCtrl,
            0xA3 => Key.RightCtrl,
            0x10 => Key.LeftShift,
            0x11 => Key.LeftCtrl,
            _ => Key.None
        };
        return key != Key.None;
    }

    private static bool IsOrbitPanDragStart(PointerPoint p, KeyModifiers keys, out bool orbit)
    {
        var props = p.Properties;
        var left = props.IsLeftButtonPressed;
        var middle = props.IsMiddleButtonPressed;
        if (!left && !middle)
        {
            orbit = false;
            return false;
        }

        orbit = keys.HasFlag(KeyModifiers.Alt);
        return true;
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed)
        {
            _debugFlyRmbLook = true;
            _cameraDragLast = e.GetPosition(this);
            PushDebugFlyInput();
            _backend.SetUserCameraDragging(true);
            e.Pointer.Capture(this);
            e.Handled = true;
            RecoverPreviewFrame();
            return;
        }

        if (!IsOrbitPanDragStart(pt, e.KeyModifiers, out var orbit))
        {
            return;
        }

        var pressPos = e.GetPosition(this);
        var now = DateTime.UtcNow;
        var clickDx = pressPos.X - _lastLeftClickPos.X;
        var clickDy = pressPos.Y - _lastLeftClickPos.Y;
        if (pt.Properties.IsLeftButtonPressed &&
            (now - _lastLeftClickUtc).TotalMilliseconds <= DoubleClickMs &&
            clickDx * clickDx + clickDy * clickDy <= DoubleClickMaxDist * DoubleClickMaxDist)
        {
            _backend.FocusOrbitOnSubject();
            _lastLeftClickUtc = DateTime.MinValue;
            e.Handled = true;
            RecoverPreviewFrame();
            return;
        }

        if (pt.Properties.IsLeftButtonPressed)
        {
            _lastLeftClickUtc = now;
            _lastLeftClickPos = pressPos;
        }

        _cameraDrag = true;
        _cameraDragIsOrbit = orbit;
        _cameraDragLast = e.GetPosition(this);
        _backend.SetUserCameraDragging(true);
        e.Pointer.Capture(this);
        e.Handled = true;
        RecoverPreviewFrame();
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_debugFlyRmbLook)
        {
            var cur = e.GetPosition(this);
            var dx = (float)(cur.X - _cameraDragLast.X);
            var dy = (float)(cur.Y - _cameraDragLast.Y);
            _cameraDragLast = cur;
            _backend.ApplyFlyLookPixels(dx, dy);
            e.Handled = true;
            RecoverPreviewFrame();
            return;
        }

        if (!_cameraDrag)
        {
            return;
        }

        var cur2 = e.GetPosition(this);
        var dx2 = (float)(cur2.X - _cameraDragLast.X);
        var dy2 = (float)(cur2.Y - _cameraDragLast.Y);
        _cameraDragLast = cur2;
        if (_cameraDragIsOrbit)
        {
            _backend.ApplyCameraOrbitPixels(dx2, dy2);
        }
        else
        {
            _backend.ApplyCameraPanPixels(dx2, dy2);
        }

        e.Handled = true;
        RecoverPreviewFrame();
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            _debugFlyRmbLook = false;
            PushDebugFlyInput();
            _backend.SetUserCameraDragging(false);
            e.Pointer.Capture(null);
            e.Handled = true;
            RecoverPreviewFrame();
            return;
        }

        if (e.InitialPressMouseButton is not (MouseButton.Left or MouseButton.Middle))
        {
            return;
        }

        _cameraDrag = false;
        _backend.SetUserCameraDragging(false);
        e.Pointer.Capture(null);
        e.Handled = true;
        RecoverPreviewFrame();
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var dy = e.Delta.Y;
        if (Math.Abs(dy) < double.Epsilon)
        {
            return;
        }

        var notches = (float)(dy / 120.0);
        if (Math.Abs(notches) < 0.08f)
        {
            notches = Math.Sign(notches);
        }

        _backend.ApplyCameraZoom(notches);
        e.Handled = true;
        RecoverPreviewFrame();
    }

    private void RecordRenderFrame(TimeSpan dt)
    {
        _fpsAccumSeconds += dt.TotalSeconds;
        _fpsFrameCount++;
        if (_fpsAccumSeconds < 0.5)
        {
            return;
        }

        Volatile.Write(ref _smoothedFps, _fpsFrameCount / _fpsAccumSeconds);
        _fpsAccumSeconds = 0;
        _fpsFrameCount = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nativeWglPresenter?.Dispose();
        _nativeWglPresenter = null;
        _hdrSwapchain?.Dispose();
        _hdrSwapchain = null;
        _nativeHost.NativeWindowCreated -= OnNativeHostWindowCreated;
        _nativeHost.NativeWindowDestroyed -= OnNativeHostWindowDestroyed;
        _nativeHost.NativeWindowCreationFailed -= StartAngleFallbackFromNativeWgl;
        _nativeHost.NativePointerPressed -= OnNativeHostPointerPressed;
        _nativeHost.NativePointerMoved -= OnNativeHostPointerMoved;
        _nativeHost.NativePointerReleased -= OnNativeHostPointerReleased;
        _nativeHost.NativePointerWheel -= OnNativeHostPointerWheel;
        _nativeHost.NativeKeyDown -= OnNativeHostKeyDown;
        _nativeHost.NativeKeyUp -= OnNativeHostKeyUp;
        _nativeHost.NativeInputLost -= OnNativeHostInputLost;
        _backend.GpuInitProgressChanged -= OnGpuInitProgressChanged;
        _backend.Dispose();
    }

    internal void OnNativeWglReady()
    {
        _glInterface = _nativeWglPresenter?.GlInterface;
        ApplyPresentationVsync();
        UpdateNativeWglOverlayBitmaps();
        if (_nativeHwnd != IntPtr.Zero)
        {
            InvalidateHdrDisplayProbeCache();
            RaiseHdrProbeUpdated(GetCachedHdrDisplayInfo(), nativeWglActive: true, _hdrPresentPathFailed);
        }

        RecoverPreviewFrame();
    }

    private void UpdateNativeWglOverlayBitmaps()
    {
        if (_nativeWglPresenter is null || _disposed)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateNativeWglOverlayBitmaps, DispatcherPriority.Background);
            return;
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0.0)
        {
            scale = 1.0;
        }

        var debug = RenderDebugOverlayBitmap(OverlayDebugText, scale);
        var fps = OverlayFpsVisible ? RenderFpsOverlayBitmap(OverlayFpsText, scale) : null;
        var cpu = OverlayFpsVisible ? RenderFpsOverlayBitmap(OverlayCpuText, scale) : null;
        _backend.SetNativeWglOverlay(debug, fps, cpu, Math.Max(1, (int)Math.Round(8.0 * scale)));
        // Continuous native frames already pick up the new overlay; avoid a UI→frame storm.
        if (!_backend.NeedsContinuousRendering)
        {
            RecoverPreviewFrame();
        }
    }

    private static PreviewNativeWglOverlayBitmap? RenderDebugOverlayBitmap(string? text, double renderScale)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var block = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        };
        return RenderOverlayVisualToBitmap(block, renderScale);
    }

    private static PreviewNativeWglOverlayBitmap? RenderFpsOverlayBitmap(string? text, double renderScale)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF))
            }
        };
        return RenderOverlayVisualToBitmap(border, renderScale);
    }

    private static PreviewNativeWglOverlayBitmap? RenderOverlayVisualToBitmap(Control visual, double renderScale)
    {
        visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = visual.DesiredSize;
        if (desired.Width <= 0.0 || desired.Height <= 0.0)
        {
            return null;
        }

        visual.Arrange(new Rect(desired));
        var width = Math.Max(1, (int)Math.Ceiling(desired.Width * renderScale));
        var height = Math.Max(1, (int)Math.Ceiling(desired.Height * renderScale));
        using var bitmap = new RenderTargetBitmap(
            new PixelSize(width, height),
            new Avalonia.Vector(96.0 * renderScale, 96.0 * renderScale));
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }

        return new PreviewNativeWglOverlayBitmap(width, height, pixels);
    }

    internal void RenderNativeWglFrame(PreviewDesktopWglBootstrap.ISwapBuffersContext context)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = TimeSpan.FromTicks(now - _lastTicks);
        _lastTicks = now;
        PushDebugFlyInput();
        _backend.RenderFrame(dt);
        RecordRenderFrame(dt);
        var w = Math.Max(1, Volatile.Read(ref _cachedPreviewPixelWidth));
        var h = Math.Max(1, Volatile.Read(ref _cachedPreviewPixelHeight));
        var presentedHdr = TryPresentNativeHdr(context, w, h);
        if (!presentedHdr)
        {
            _backend.GlRenderNativeWglPresenter(w, h);
            context.SwapBuffers();
        }

        Volatile.Write(ref _lastOpenGlRenderTicks, now);
    }

    private bool TryPresentNativeHdr(PreviewDesktopWglBootstrap.ISwapBuffersContext context, int width, int height)
    {
        if (!_backend.TryGetSilkGl(out var gl))
        {
            gl = null;
        }

        if (_hdrPresentPathFailed || !_backend.HdrPresentActive || _nativeHwnd == IntPtr.Zero)
        {
            PreviewHdrDisplayInfo display = PreviewHdrDisplayInfo.Unsupported;
            if (_nativeHwnd != IntPtr.Zero)
            {
                display = GetCachedHdrDisplayInfo();
            }

            ReleaseHdrSwapchain(gl);
            RaiseHdrProbeUpdated(display, nativeWglActive: _nativeWglPresenter is not null, _hdrPresentPathFailed);
            return false;
        }

        var displayInfo = GetCachedHdrDisplayInfo();
        RaiseHdrProbeUpdated(displayInfo, nativeWglActive: true, presentPathFailed: false);
        if (!displayInfo.SupportsHdr)
        {
            ReleaseHdrSwapchain(gl);
            return false;
        }

        if (gl is null)
        {
            return false;
        }

        try
        {
            _hdrSwapchain ??= new PreviewHdrDxgiSwapchain();

            if (!_hdrSwapchain.TryEnsure(_nativeHwnd, width, height, context.GlInterface, gl, out var ensureFailure))
            {
                // Present HWND is created asynchronously on the UI thread; retry next frame.
                if (string.Equals(
                        ensureFailure,
                        PreviewHdrDxgiSwapchain.PresentWindowPendingMessage,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                return FailHdrPresent(displayInfo, gl, "HDR DXGI present unavailable: " + (ensureFailure ?? "unknown"));
            }

            if (!_hdrSwapchain.TryBeginFrame(gl, out var beginFailure))
            {
                return FailHdrPresent(displayInfo, gl, "HDR frame begin failed: " + (beginFailure ?? "unknown"));
            }

            try
            {
                _backend.GlRenderNativeWglPresenter(width, height, _hdrSwapchain.Framebuffer);
                // DXGI is top-down; flip after overlays so the full frame (including HUD) is upright.
                // Orientation-only failure must not kill the HDR session.
                if (!_hdrSwapchain.TryFlipFramebufferY(gl) && !_hdrYFlipFailureLogged)
                {
                    _hdrYFlipFailureLogged = true;
                    _backend.EmitPreviewDiagnostic(
                        "[3D preview] HDR Y-flip failed (" +
                        (_hdrSwapchain.LastFailure ?? "unknown") +
                        "); presenting without flip.");
                }
                else if (!_hdrYFlipPathLogged &&
                         !string.Equals(_hdrSwapchain.YFlipResolvePath, "undecided", StringComparison.Ordinal))
                {
                    _hdrYFlipPathLogged = true;
                    _backend.EmitPreviewDiagnostic(
                        "[3D preview] HDR Y-flip resolve path: " + _hdrSwapchain.YFlipResolvePath + ".");
                }

                if (!_hdrTearingLogged)
                {
                    _hdrTearingLogged = true;
                    _backend.EmitPreviewDiagnostic(
                        "[3D preview] HDR DXGI tearing: " +
                        (_hdrSwapchain.AllowTearing ? "enabled" : "unavailable") +
                        ".");
                }

                if (!_hdrPresentResolvePathLogged)
                {
                    _hdrPresentResolvePathLogged = true;
                    _backend.EmitPreviewDiagnostic("[3D preview] HDR resolve path: shared-copy.");
                }
            }
            finally
            {
                _hdrSwapchain.EndFrame();
            }

            // Double-buffered resolve: Present waits on the previous buffer's fence (overlapped by
            // scene render), not a hard ClientWaitSync of the frame we just drew.
            if (!_hdrSwapchain.TryPresent(gl, _presentationVsyncEnabled, out var presentFailure))
            {
                return FailHdrPresent(displayInfo, gl, "HDR Present failed: " + (presentFailure ?? "unknown"));
            }

            return true;
        }
        catch (Exception ex)
        {
            return FailHdrPresent(
                displayInfo,
                gl,
                $"HDR present crashed ({ex.GetType().Name}: {ex.Message}); falling back to SDR for this session.");
        }
    }

    private void ReleaseHdrSwapchain(GL? gl)
    {
        var swapchain = _hdrSwapchain;
        if (swapchain is null)
        {
            return;
        }

        _hdrSwapchain = null;
        try
        {
            swapchain.Dispose(gl);
        }
        catch
        {
            // Best-effort: HWND must return to WGL even if interop teardown throws.
        }
    }

    private bool FailHdrPresent(in PreviewHdrDisplayInfo displayInfo, GL? gl, string detail)
    {
        _hdrPresentPathFailed = true;
        _backend.SuppressHdrPresentForSession();
        ReleaseHdrSwapchain(gl);
        RaiseHdrProbeUpdated(displayInfo, nativeWglActive: true, presentPathFailed: true);
        _backend.EmitPreviewDiagnostic("[3D preview] " + detail);
        return false;
    }

    /// <summary>Clears a prior HDR present-path latch so Auto can retry after settings changes.</summary>
    public void ClearHdrPresentFailureLatch()
    {
        _hdrPresentPathFailed = false;
        _hdrYFlipFailureLogged = false;
        _hdrYFlipPathLogged = false;
        _hdrTearingLogged = false;
        _hdrPresentResolvePathLogged = false;
        _backend.ClearHdrPresentSuppression();
        // Force the next probe raise even if display flags are unchanged.
        _lastRaisedHdrPresentPathFailed = true;
        InvalidateHdrDisplayProbeCache();
        var display = _nativeHwnd != IntPtr.Zero
            ? GetCachedHdrDisplayInfo()
            : PreviewHdrDisplayInfo.Unsupported;
        RaiseHdrProbeUpdated(display, nativeWglActive: _nativeWglPresenter is not null, presentPathFailed: false);
    }

    private void InvalidateHdrDisplayProbeCache() => Interlocked.Exchange(ref _cachedHdrProbeTicks, 0);

    private PreviewHdrDisplayInfo GetCachedHdrDisplayInfo()
    {
        var now = Stopwatch.GetTimestamp();
        var cachedAt = Volatile.Read(ref _cachedHdrProbeTicks);
        if (cachedAt != 0 && now - cachedAt < HdrProbeCacheTicks)
        {
            return _cachedHdrDisplayInfo;
        }

        var probed = _nativeHwnd != IntPtr.Zero
            ? PreviewHdrDisplayProbe.ProbeForWindow(_nativeHwnd)
            : PreviewHdrDisplayInfo.Unsupported;
        _cachedHdrDisplayInfo = probed;
        Volatile.Write(ref _cachedHdrProbeTicks, now);
        return probed;
    }

    internal void OnNativeWglFrameCompleted()
    {
        if (_disposed || _nativeWglPresenter is null)
        {
            return;
        }

        QueueContinuousFrameIfNeeded();
    }

    private void UpdateCachedPreviewViewportInfo()
    {
        if (!TryGetPreviewViewportInfo(out var w, out var h, out _, out _, out _))
        {
            return;
        }

        Volatile.Write(ref _cachedPreviewPixelWidth, w);
        Volatile.Write(ref _cachedPreviewPixelHeight, h);
        _backend.Resize(w, h);
    }

    private sealed class AngleOpenGlSurface : OpenGlControlBase, ICustomHitTest
    {
        private readonly GlPbrPreviewControl _owner;

        public AngleOpenGlSurface(GlPbrPreviewControl owner)
        {
            _owner = owner;
            ClipToBounds = true;
            Focusable = false;
            IsTabStop = false;
        }

        public bool HitTest(Point point) => _owner.HitTest(point);

        public void RequestFrame() => RequestNextFrameRendering();

        protected override void OnOpenGlInit(GlInterface gl) => _owner.OnAngleOpenGlInit(gl);

        protected override void OnOpenGlDeinit(GlInterface gl) => _owner.OnAngleOpenGlDeinit(gl);

        protected override void OnOpenGlRender(GlInterface gl, int fb) => _owner.OnAngleOpenGlRender(gl, fb);
    }
}
