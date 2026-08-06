using System.Threading.Tasks;

using AutoPBR.App.Lang;
using AutoPBR.App.Rendering.Scene;

using Avalonia.OpenGL;
using Avalonia.Threading;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private PreviewDesktopWglContext? _desktopWglSidecar;
    private PreviewOpenGlCompositionBridge? _compositionBridge;
    private GlInterface? _presentationGlInterface;
    private bool _pendingDesktopWglSidecar;
    private int _desktopWglSidecarInitState;
    private bool _dxInteropFallbackLogged;
    private bool _dxInteropSuccessLogged;
    private bool _asyncPboReadbackLogged;
    private bool _forceSyncSidecarPresent;
    private bool _sidecarAdapterMatchAttempted;
    private int _sidecarBootstrapWorkerState;
    private bool _nativeWglPresenterActive;

    private const int SidecarInitIdle = 0;
    private const int SidecarInitRunning = 1;
    private const int SidecarInitDone = 2;

    internal bool UsesDesktopWglSidecar => _desktopWglSidecar is not null;

    internal bool IsSidecarAdapterMatchComplete => _sidecarAdapterMatchAttempted;

    internal bool IsAwaitingDesktopWglSidecar
    {
        get
        {
            lock (_sync)
            {
                return _pendingDesktopWglSidecar;
            }
        }
    }

    internal void SetCompositionBridge(PreviewOpenGlCompositionBridge? bridge)
    {
        lock (_sync)
        {
            _compositionBridge?.Dispose();
            _compositionBridge = bridge;
            _dxInteropFallbackLogged = false;
            _dxInteropSuccessLogged = false;
            _sidecarAdapterMatchAttempted = false;
        }
    }

    internal void BeginGlInit(GlInterface presentationGlInterface)
    {
        lock (_sync)
        {
            _lastError = null;
            _presentationGlInterface = presentationGlInterface;
            _gpuInitStopwatch.Restart();
            PreviewShaderPrewarm.EnsureStarted();
            PreviewBundledGpuAssetPrewarm.EnsureStarted();

            if (PreviewOpenGlSession.RequestedDesktopGl4)
            {
                _pendingDesktopWglSidecar = true;
                _desktopWglSidecarInitState = SidecarInitIdle;
                _useOpenGlEs = false;
                RaiseGpuInitProgress(PreviewGpuInitPhases.Preparing, _settings);
                return;
            }

            FinishGlInitLocked(presentationGlInterface, sidecar: null);
        }
    }

    internal void BeginNativeWglPresenterGlInit(GlInterface nativeGlInterface)
    {
        lock (_sync)
        {
            _lastError = null;
            _presentationGlInterface = nativeGlInterface;
            _gpuInitStopwatch.Restart();
            PreviewShaderPrewarm.EnsureStarted();
            PreviewBundledGpuAssetPrewarm.EnsureStarted();
            _pendingDesktopWglSidecar = false;
            _desktopWglSidecarInitState = SidecarInitDone;
            _nativeWglPresenterActive = true;
            _useOpenGlEs = false;
            FinishGlInitLocked(nativeGlInterface, sidecar: null);
        }
    }

    internal void ScheduleDesktopWglSidecarInit(Action requestNextFrame)
    {
        if (!PreviewOpenGlSession.RequestedDesktopGl4)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _desktopWglSidecarInitState, SidecarInitRunning, SidecarInitIdle) != SidecarInitIdle)
        {
            return;
        }

        PreviewOpenGlCompositionBridge? compositionBridge;
        lock (_sync)
        {
            compositionBridge = _compositionBridge;
        }

        EmitDiagnostic("[3D preview] Creating desktop WGL sidecar on the dedicated WGL owner thread...");

        Task.Run(() =>
            {
                try
                {
                    return PreviewDesktopWglOwnerThread.Run(
                        () => CreateDesktopWglSidecarForInit(compositionBridge),
                        TimeSpan.FromSeconds(60));
                }
                catch (Exception ex)
                {
                    EmitDiagnostic("[3D preview] Desktop WGL sidecar init failed: " + ex.Message);
                    return null;
                }
            })
            .ContinueWith(
                task =>
                {
                    PreviewDesktopWglContext? sidecar = null;
                    if (task.IsCompletedSuccessfully)
                    {
                        sidecar = task.Result;
                    }

                    void Complete()
                    {
                        ApplyCompletedDesktopWglSidecar(sidecar);
                        requestNextFrame();
                    }

                    if (Dispatcher.UIThread.CheckAccess())
                    {
                        Complete();
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(Complete, DispatcherPriority.Background);
                    }
                },
                TaskScheduler.Default);
    }

    private void ApplyCompletedDesktopWglSidecar(PreviewDesktopWglContext? sidecar)
    {
        Interlocked.Exchange(ref _desktopWglSidecarInitState, SidecarInitDone);

        GlInterface presentationGlInterface;
        lock (_sync)
        {
            if (!_pendingDesktopWglSidecar)
            {
                return;
            }

            _pendingDesktopWglSidecar = false;
            presentationGlInterface = _presentationGlInterface ??
                                      throw new InvalidOperationException("Presentation GL interface missing.");
            if (sidecar is not null)
            {
                _desktopWglSidecar = sidecar;
            }

            FinishGlInitLocked(sidecar?.GlInterface ?? presentationGlInterface, sidecar);
            if (sidecar is not null)
            {
                StartSidecarGpuBootstrapWorker(sidecar);
            }
        }
    }

    private void StartSidecarGpuBootstrapWorker(PreviewDesktopWglContext sidecar)
    {
        if (Interlocked.CompareExchange(ref _sidecarBootstrapWorkerState, 1, 0) != 0)
        {
            return;
        }

        Task.Run(() =>
        {
            var priorPriority = Thread.CurrentThread.Priority;
            try
            {
                // Keep bootstrap from contending with interactive apps / the UI thread.
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                SidecarGpuBootstrapLoop(sidecar);
            }
            catch (Exception ex)
            {
                EmitDiagnostic($"[3D preview] Sidecar GPU bootstrap failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Thread.CurrentThread.Priority = priorPriority;
                GlShaderCompileYield.SetEnabled(false);
                Interlocked.Exchange(ref _sidecarBootstrapWorkerState, 0);
            }
        });
    }

    private void SidecarGpuBootstrapLoop(PreviewDesktopWglContext sidecar)
    {
        // Soft slices: smaller GPU compile bursts + longer sleeps so the OS / other processes
        // can schedule. Progress is raised after every slice so the overlay keeps moving.
        const double sliceBudgetMs = 24.0;
        const int sliceSleepMs = 12;

        while (true)
        {
            var shouldContinue = false;
            lock (_sync)
            {
                shouldContinue = _gpuBootstrap is { IsComplete: false };
            }

            if (!shouldContinue)
            {
                return;
            }

            sidecar.Invoke(() =>
            {
                using (sidecar.BindOnOwnerThread())
                {
                    var ownerPrior = Thread.CurrentThread.Priority;
                    try
                    {
                        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                        GlShaderCompileYield.SetEnabled(true);
                        AdvanceSidecarGpuBootstrapSlice(sliceBudgetMs);
                    }
                    finally
                    {
                        Thread.CurrentThread.Priority = ownerPrior;
                    }
                }
            });

            Thread.Sleep(sliceSleepMs);
        }
    }

    private void AdvanceSidecarGpuBootstrapSlice(double maxMilliseconds = 24.0)
    {
        lock (_sync)
        {
            if (_gpuBootstrap is not { IsComplete: false })
            {
                return;
            }

            var settings = _settings;
            _gpuBootstrap.Advance(this, maxMilliseconds);
            var bootstrap = _gpuBootstrap;
            if (bootstrap is null)
            {
                return;
            }

            var phase = bootstrap.IsComplete ? PreviewGpuInitPhases.CoreReady : bootstrap.Phase;
            RaiseGpuInitProgress(phase, settings);
            if (!bootstrap.IsComplete && !_gpuBootstrapAborted)
            {
                return;
            }

            if (bootstrap.IsComplete)
            {
                GlShaderCompileYield.SetEnabled(false);
                _forceSyncSidecarPresent = true;
                // Ensure an idle stage exists as soon as core GPU resources (incl. terrain) are up,
                // even if the VM has not pushed a subject yet.
                if (_scene is null)
                {
                    _scene = PreviewStageSceneFactory.CreateIdle(_settings);
                    _meshDirty = true;
                }
            }

            _gpuBootstrap = null;
            _gpuBootstrapAborted = false;
        }
    }

    private PreviewDesktopWglContext? CreateDesktopWglSidecarForInit(PreviewOpenGlCompositionBridge? composition)
    {
        var profiles = new[]
        {
            new GlVersion(GlProfileType.OpenGL, 4, 6),
            new GlVersion(GlProfileType.OpenGL, 4, 0),
            new GlVersion(GlProfileType.OpenGL, 3, 3),
        };

        var sidecar = PreviewDesktopWglContext.TryCreate(
            profiles,
            IntPtr.Zero,
            EmitDiagnostic,
            probePresentationAdapter: false);
        if (sidecar is null)
        {
            EmitDiagnostic(
                "[3D preview] Desktop OpenGL 4.x sidecar unavailable; preview stays on OpenGL ES (ANGLE). Restart with WGL drivers enabled or disable Engine → OpenGL 4.x.");
            return null;
        }

        EmitDiagnostic("[3D preview] Desktop OpenGL 4.x sidecar active; presentation uses ANGLE compositor pacing.");
        if (sidecar.DxInteropOptInEnabled)
        {
            sidecar.EnableDxInteropHangDiagnostics(EmitDiagnostic);
        }
        else
        {
            EmitDiagnostic("[3D preview] D3D11/WGL shared-texture interop disabled; using stable async PBO presentation.");
        }

        if (composition is not null && sidecar.DxInteropOptInEnabled)
        {
            sidecar = TryMatchSidecarToPresentationAdapter(composition, sidecar);
            _sidecarAdapterMatchAttempted = true;
            sidecar.EnableDxInteropHangDiagnostics(EmitDiagnostic);
        }
        else
        {
            _sidecarAdapterMatchAttempted = true;
        }

        return sidecar;
    }

    private PreviewDesktopWglContext TryMatchSidecarToPresentationAdapter(
        PreviewOpenGlCompositionBridge composition,
        PreviewDesktopWglContext sidecar)
    {
        if (!composition.TryResolvePresentationDevice(out var presentationDevice, out var deviceDetail))
        {
            EmitDiagnostic("[3D preview] ANGLE D3D11 device not resolved; sidecar adapter matching skipped.");
            return sidecar;
        }

        EmitDiagnostic($"[3D preview] Resolved ANGLE D3D11 device via {deviceDetail}.");

        using (sidecar.BindOnOwnerThread())
        {
            PreviewDesktopWglDxInterop.ResetProcCache();
            if (PreviewDesktopWglGpuAffinity.ContextCanOpenPresentationDevice(sidecar.GlInterface, presentationDevice))
            {
                EmitDiagnostic("[3D preview] Sidecar WGL matched ANGLE D3D11 adapter on default GPU.");
                return sidecar;
            }
        }

        EmitDiagnostic("[3D preview] Probing WGL_NV_gpu_affinity to match ANGLE D3D11 adapter...");
        var profiles = new[]
        {
            new GlVersion(GlProfileType.OpenGL, 4, 6),
            new GlVersion(GlProfileType.OpenGL, 4, 0),
            new GlVersion(GlProfileType.OpenGL, 3, 3),
        };

        var matchedContext = PreviewDesktopWglGpuAffinity.TryCreateSidecarContext(
            profiles,
            presentationDevice,
            EmitDiagnostic,
            probePresentationAdapter: true);
        if (matchedContext is null)
        {
            return sidecar;
        }

        PreviewDesktopWglContext? matchedSidecar;
        try
        {
            matchedSidecar = PreviewDesktopWglContext.TryCreateFromContext(matchedContext);
        }
        catch
        {
            matchedContext.Dispose();
            return sidecar;
        }

        if (matchedSidecar is null)
        {
            matchedContext.Dispose();
            return sidecar;
        }

        sidecar.Dispose();
        return matchedSidecar;
    }

    private void DestroyDesktopWglSidecar()
    {
        _desktopWglSidecar?.Dispose();
        _desktopWglSidecar = null;
        _compositionBridge?.Dispose();
        _compositionBridge = null;
        _presentationGlInterface = null;
        _pendingDesktopWglSidecar = false;
        Interlocked.Exchange(ref _desktopWglSidecarInitState, SidecarInitIdle);
        _dxInteropFallbackLogged = false;
        _dxInteropSuccessLogged = false;
        _asyncPboReadbackLogged = false;
        _sidecarAdapterMatchAttempted = false;
        _nativeWglPresenterActive = false;
        Interlocked.Exchange(ref _sidecarBootstrapWorkerState, 0);
    }
}
