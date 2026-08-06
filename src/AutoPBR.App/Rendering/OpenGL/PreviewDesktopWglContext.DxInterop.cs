using System.Diagnostics;
using System.Text;

using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Platform;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed partial class PreviewDesktopWglContext
{
    private const string DxInteropOptInEnvironmentVariable = "AUTOPBR_PREVIEW_WGL_DX_INTEROP";
    private const uint KeyedMutexAcquireTimeoutMs = 32;
    private const uint AnglePresentMutexTimeoutMs = 16;
    /// <summary>Exclusive ownership key — double-buffer indices already separate producer/consumer.</summary>
    private const ulong SharedMutexKey = 0;
    private const int MutexTimeoutResetThreshold = 120;
    private const double PendingDxFenceFallbackMs = 500.0;

    private readonly PreviewDesktopWglDxRenderTarget[] _dxRenderTargets =
    [
        new PreviewDesktopWglDxRenderTarget(),
        new PreviewDesktopWglDxRenderTarget(),
    ];

    private PreviewAngleD3D11Presentation _dxPresentation;
    private bool _dxInteropReady;
    private bool _dxInteropExtensionMissing;
    private bool _dxInteropDisabled;
    private bool _dxInteropLogged;
    private bool _dxInteropProcsReady;
    private PreviewDesktopWglDxInteropFailure _lastInteropFailure = PreviewDesktopWglDxInteropFailure.None;
    private int _lastInteropDetail;
    private string? _lastInteropFailureDetail;
    private Action<string>? _dxDiagnosticLog;
    private PreviewDesktopWglDxInteropWatchdog? _dxWatchdog;
    private Action? _dxRequestPresentFrame;
    private string _dxWglPhase = "idle";
    private long _dxLastScheduleTicks;
    private long _dxLastPresentStartTicks;
    private long _dxLastPresentEndTicks;
    private long _dxLastWriteStartTicks;
    private long _dxLastWriteEndTicks;
    private long _dxLastCopyStartTicks;
    private long _dxLastCopyEndTicks;

    /// <summary>Last published ready buffer index, or -1.</summary>
    private int _dxFront = -1;

    /// <summary>Buffer index claimed for WGL write, or -1.</summary>
    private int _dxWriting = -1;

    /// <summary>Buffer index pinned for ANGLE blit, or -1.</summary>
    private int _dxReading = -1;

    private PixelSize _dxPipelineSize;
    private int _dxFirstFrameWaited;
    private long _dxAcquireTimeoutLogTicks;
    private long _dxPendingFenceTimeoutLogTicks;
    private int _dxMutexTimeoutStreak;
    private int _dxMutexResetRequested;
    private readonly nint[] _dxPendingFences = new nint[2];
    private readonly long[] _dxPendingFenceStartTicks = new long[2];
    private int _dxPendingMask;
    private int _dxPumpQueued;

    internal PreviewDesktopWglDxInteropFailure LastInteropFailure => _lastInteropFailure;

    /// <summary>Installs a diagnostic sink and starts the hang watchdog (safe to call repeatedly).</summary>
    internal void EnableDxInteropHangDiagnostics(Action<string> log, Action? requestPresentFrame = null)
    {
        _dxDiagnosticLog = log ?? throw new ArgumentNullException(nameof(log));
        if (requestPresentFrame is not null)
        {
            _dxRequestPresentFrame = requestPresentFrame;
        }

        if (_dxWatchdog is not null)
        {
            return;
        }

        _dxWatchdog = new PreviewDesktopWglDxInteropWatchdog(
            log,
            BuildDxInteropHangSnapshot,
            isWglWedged: () => IsOwnerThreadLikelyWedged,
            onWglWedged: () => DisableDxInterop("hang watchdog (WGL owner wedged in NV_DX/copy)"),
            onPresentIdle: () =>
            {
                EmitDxDiag("[3D preview] Present loop idle with healthy WGL — requesting a recovery frame.");
                RequestDxPresentFrame();
            });
        _dxWatchdog.NotePresentHeartbeat();
        _dxWatchdog.NoteWglHeartbeat();
        log("[3D preview] DX interop hang watchdog armed (present/WGL heartbeats + keyed-mutex wait probes).");
    }

    internal string LastInteropFailureSummary
    {
        get
        {
            if (!DxInteropOptInEnabled)
            {
                return $"NV_DX interop is disabled by default; set {DxInteropOptInEnvironmentVariable}=1 to opt into the experimental shared-texture path.";
            }

            var message = PreviewDesktopWglDxInteropDiagnostics.Describe(_lastInteropFailure, _lastInteropDetail);
            if (!string.IsNullOrWhiteSpace(_lastInteropFailureDetail))
            {
                return $"{message} ({_lastInteropFailureDetail})";
            }

            return message;
        }
    }

    internal bool DxInteropOptInEnabled => IsDxInteropOptInEnabled();

    internal bool CanAttemptDxInterop => DxInteropOptInEnabled && !_dxInteropExtensionMissing && !_dxInteropDisabled;

    /// <summary>True when the WGL owner thread has been inside one work item for too long (likely NV_DX hang).</summary>
    internal bool IsOwnerThreadLikelyWedged
    {
        get
        {
            var writing = Volatile.Read(ref _dxWriting);
            if (writing >= 0)
            {
                var copyStart = Volatile.Read(ref _dxLastCopyStartTicks);
                var copyEnd = Volatile.Read(ref _dxLastCopyEndTicks);
                if (copyStart != 0 && copyStart > copyEnd)
                {
                    var ageMs = (Stopwatch.GetTimestamp() - copyStart) * 1000.0 / Stopwatch.Frequency;
                    if (ageMs > 500)
                    {
                        return true;
                    }
                }
            }

            var started = PreviewDesktopWglOwnerThread.LastItemStartedTicks;
            if (started == 0)
            {
                return false;
            }

            var phase = PreviewDesktopWglOwnerThread.CurrentPhase;
            if (phase is "idle")
            {
                return false;
            }

            var age = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            return age > 2000;
        }
    }

    internal void DisableDxInterop(string reason)
    {
        if (_dxInteropDisabled)
        {
            return;
        }

        _dxInteropDisabled = true;
        EmitDxDiag($"[3D preview] DX interop disabled: {reason}");
    }

    internal bool TryRenderViaDxInterop(
        PreviewOpenGlCompositionBridge composition,
        GlInterface presentationGlInterface,
        int presentationFbo,
        int width,
        int height,
        Action<int> renderCore)
    {
        if (!CanAttemptDxInterop)
        {
            return false;
        }

        if (!composition.TryResolvePresentation(out _dxPresentation, out var deviceDetail))
        {
            NoteInteropFailure(PreviewDesktopWglDxInteropFailure.PresentationDeviceUnavailable, 0, deviceDetail);
            return false;
        }

        if (!_dxInteropProcsReady)
        {
            try
            {
                if (!Invoke(EnsureDxInteropProcsOnOwnerThread, TimeSpan.FromSeconds(2)))
                {
                    return false;
                }
            }
            catch (TimeoutException)
            {
                NoteInteropFailure(
                    PreviewDesktopWglDxInteropFailure.Exception,
                    0,
                    "Timed out loading WGL_NV_DX_interop procs on the owner thread.");
                return false;
            }

            _dxInteropProcsReady = true;
        }

        var size = new PixelSize(Math.Max(1, width), Math.Max(1, height));
        if (_dxPipelineSize != size)
        {
            if (!WaitForInFlightDxWrite(TimeSpan.FromMilliseconds(250)))
            {
                NoteInteropFailure(
                    PreviewDesktopWglDxInteropFailure.Exception,
                    0,
                    "Timed out waiting for in-flight WGL write before resize.");
                return false;
            }

            Volatile.Write(ref _dxFront, -1);
            Volatile.Write(ref _dxReading, -1);
            _dxPipelineSize = size;
            Interlocked.Exchange(ref _dxFirstFrameWaited, 0);
        }

        if (!composition.TryEnsureSharedExportPair(size, out var exports))
        {
            NoteInteropFailure(PreviewDesktopWglDxInteropFailure.SharedExportUnavailable, 0);
            return false;
        }

        // Soft recovery: drop front so the next successful WGL copy republishes. Never dispose
        // Avalonia exports while NV_DX still holds registrations — that hangs Unregister/Lock.
        if (Interlocked.Exchange(ref _dxMutexResetRequested, 0) != 0)
        {
            Volatile.Write(ref _dxFront, -1);
            Interlocked.Exchange(ref _dxMutexTimeoutStreak, 0);
            EmitDxDiag("[3D preview] Cleared DX interop front buffer after sustained keyed-mutex timeouts.");
        }

        ScheduleDxInteropPumpIfNeeded();

        Interlocked.Exchange(ref _dxLastPresentStartTicks, Stopwatch.GetTimestamp());
        _dxWatchdog?.NotePresentHeartbeat();

        var presented = false;
        var front = Volatile.Read(ref _dxFront);
        if (front >= 0)
        {
            presented = TryBlitFrontBuffer(presentationGlInterface, exports, front, presentationFbo, width, height);
        }
        else
        {
            // Never Invoke(full Genesis) on the present thread — that deadlocks with MicroCom/DXGI
            // when WGL registration or copy needs the ANGLE thread. Keep the FBO cleared until
            // the first async write publishes a front buffer.
            ClearPresentationFramebuffer(presentationGlInterface, presentationFbo, width, height);
            presented = true;
            if (Interlocked.CompareExchange(ref _dxFirstFrameWaited, 1, 0) == 0)
            {
                ScheduleDxInteropWrite(exports, width, height, renderCore);
                Interlocked.Exchange(ref _dxLastPresentEndTicks, Stopwatch.GetTimestamp());
                _dxWatchdog?.NotePresentHeartbeat();
                _dxInteropReady = true;
                return true;
            }
        }

        ScheduleDxInteropWrite(exports, width, height, renderCore);
        Interlocked.Exchange(ref _dxLastPresentEndTicks, Stopwatch.GetTimestamp());
        _dxWatchdog?.NotePresentHeartbeat();

        if (presented)
        {
            if (!_dxInteropLogged)
            {
                _dxInteropLogged = true;
                _dxInteropReady = true;
            }

            _lastInteropFailure = PreviewDesktopWglDxInteropFailure.None;
            _lastInteropDetail = 0;
            _lastInteropFailureDetail = null;
            return true;
        }

        return _dxInteropReady;
    }

    private void ScheduleDxInteropWrite(
        IGlExportableExternalImageTexture[] exports,
        int width,
        int height,
        Action<int> renderCore)
    {
        if (!TryClaimDxWriteBuffer(out var target))
        {
            return;
        }

        // Capture the shared handle on the ANGLE/present thread. Touching Avalonia MicroCom
        // export objects from the WGL owner thread deadlocks via SynchronizationContext.
        var sharedHandle = exports[target].GetHandle();
        var renderTarget = _dxRenderTargets[target];
        Interlocked.Exchange(ref _dxLastScheduleTicks, Stopwatch.GetTimestamp());
        PreviewDesktopWglOwnerThread.Post(
            () => ExecuteDxInteropWrite(sharedHandle, renderTarget, width, height, renderCore, target),
            phase: $"dx-write[{target}]");
    }

    private bool TryClaimDxWriteBuffer(out int target)
    {
        target = -1;
        var front = Volatile.Read(ref _dxFront);
        var reading = Volatile.Read(ref _dxReading);
        var candidate = front < 0 ? 0 : 1 - front;
        if (IsDxBufferPending(candidate))
        {
            ScheduleDxInteropPumpIfNeeded();
            return false;
        }

        if (candidate == reading)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _dxWriting, candidate, -1) != -1)
        {
            return false;
        }

        target = candidate;
        return true;
    }

    private bool TryBlitFrontBuffer(
        GlInterface presentationGlInterface,
        IGlExportableExternalImageTexture[] exports,
        int front,
        int presentationFbo,
        int width,
        int height)
    {
        // Never touch a buffer still marked writing (mutex may still be held for the short copy).
        if (Volatile.Read(ref _dxWriting) == front)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _dxReading, front, -1) != -1)
        {
            return false;
        }

        var export = exports[front];
        var acquired = false;
        try
        {
            // Present-thread only: timed AcquireSync on Avalonia's export mutex (never int.MaxValue).
            _dxWatchdog?.BeginAngleMutexWait(front);
            if (!PreviewAvaloniaExportKeyedMutex.TryAcquire(export, SharedMutexKey, AnglePresentMutexTimeoutMs))
            {
                _dxWatchdog?.EndAngleMutexWait();
                NoteAcquireTimeout("ANGLE present blit");
                return false;
            }

            acquired = true;
            _dxWatchdog?.EndAngleMutexWait();
            if (!PreviewOpenGlPresentationBlit.BlitExportToFramebuffer(
                    presentationGlInterface,
                    export.TextureId,
                    presentationFbo,
                    width,
                    height,
                    drainBeforeReturn: true))
            {
                NoteAcquireTimeout("ANGLE present blit drain");
                return false;
            }

            Interlocked.Exchange(ref _dxMutexTimeoutStreak, 0);
            return true;
        }
        catch (Exception ex)
        {
            _dxWatchdog?.EndAngleMutexWait();
            NoteInteropFailure(
                PreviewDesktopWglDxInteropFailure.Exception,
                PreviewWin32Error.GetLastErrorCode(),
                $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (acquired && !PreviewAvaloniaExportKeyedMutex.TryRelease(export, SharedMutexKey))
            {
                EmitDxDiag("[3D preview] ANGLE present ReleaseSync failed; mutex may stay held until next export cycle.");
            }

            Interlocked.Exchange(ref _dxReading, -1);
        }
    }

    private void ExecuteDxInteropWrite(
        IPlatformHandle sharedHandle,
        PreviewDesktopWglDxRenderTarget renderTarget,
        int width,
        int height,
        Action<int> renderCore,
        int targetIndex)
    {
        var acquired = false;
        Interlocked.Exchange(ref _dxLastWriteStartTicks, Stopwatch.GetTimestamp());
        Volatile.Write(ref _dxWglPhase, $"write-start[{targetIndex}]");
        _dxWatchdog?.NoteWglHeartbeat();
        try
        {
            using (BindOnOwnerThread())
            {
                PumpPendingDxReleasesCore();

                // 1) Long Genesis pass into a private WGL FBO — no keyed mutex / NV_DX held.
                Volatile.Write(ref _dxWglPhase, $"genesis[{targetIndex}]");
                _dxWatchdog?.NoteWglHeartbeat();
                EnsureRenderTargetCore(width, height);
                renderCore(RenderFbo);
                _dxWatchdog?.NoteWglHeartbeat();

                Volatile.Write(ref _dxWglPhase, $"register[{targetIndex}]");
                if (!renderTarget.TryEnsureRegistered(
                        _gl,
                        GlInterface,
                        _dxPresentation,
                        sharedHandle,
                        out var failure,
                        out var detail))
                {
                    NoteInteropFailure(failure, detail);
                    return;
                }

                var mutex = renderTarget.KeyedMutex;
                if (mutex is null)
                {
                    NoteInteropFailure(
                        PreviewDesktopWglDxInteropFailure.OpenSharedTextureFailed,
                        0,
                        "Native IDXGIKeyedMutex missing after interop registration.");
                    return;
                }

                // 2) Short ownership: Acquire → NV_DX Lock → blit → Finish → Unlock → Release.
                Volatile.Write(ref _dxWglPhase, $"mutex-acquire[{targetIndex}]");
                Interlocked.Exchange(ref _dxLastCopyStartTicks, Stopwatch.GetTimestamp());
                if (!mutex.TryAcquire(SharedMutexKey, KeyedMutexAcquireTimeoutMs))
                {
                    NoteAcquireTimeout("WGL shared copy");
                    return;
                }

                acquired = true;
                Volatile.Write(ref _dxWglPhase, $"nv-dx-copy[{targetIndex}]");
                if (!renderTarget.TryBegin(
                        _gl,
                        GlInterface,
                        _dxPresentation,
                        sharedHandle,
                        width,
                        height,
                        out failure,
                        out detail))
                {
                    NoteInteropFailure(failure, detail);
                    return;
                }

                try
                {
                    BlitPrivateColorToSharedFramebuffer(renderTarget.Framebuffer, width, height);
                    if (!PreviewGlCommandDrain.TryDrain(_gl, out var pendingFence))
                    {
                        StorePendingDxRelease(targetIndex, pendingFence);
                        acquired = false;
                        Volatile.Write(ref _dxWglPhase, $"pending-fence[{targetIndex}]");
                        RequestDxPresentFrame();
                        return;
                    }
                }
                finally
                {
                    if (!IsDxBufferPending(targetIndex))
                    {
                        renderTarget.End();
                    }
                }

                if (!mutex.TryRelease(SharedMutexKey))
                {
                    EmitDxDiag("[3D preview] WGL copy ReleaseSync failed; leaving acquired flag set for finally retry.");
                }
                else
                {
                    acquired = false;
                }

                Interlocked.Exchange(ref _dxLastCopyEndTicks, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _dxMutexTimeoutStreak, 0);

                Volatile.Write(ref _dxFront, targetIndex);
                Volatile.Write(ref _dxWglPhase, $"published[{targetIndex}]");
            }
        }
        catch (Exception ex)
        {
            NoteInteropFailure(
                PreviewDesktopWglDxInteropFailure.Exception,
                PreviewWin32Error.GetLastErrorCode(),
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (acquired)
            {
                renderTarget.KeyedMutex?.TryRelease(SharedMutexKey);
            }

            Interlocked.Exchange(ref _dxWriting, -1);
            Interlocked.Exchange(ref _dxLastWriteEndTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _dxWglPhase, "idle");
            _dxWatchdog?.NoteWglHeartbeat();
        }
    }

    private bool IsDxBufferPending(int targetIndex) =>
        targetIndex >= 0 &&
        (Volatile.Read(ref _dxPendingMask) & (1 << targetIndex)) != 0;

    private void StorePendingDxRelease(int targetIndex, nint fence)
    {
        if (targetIndex < 0 || targetIndex >= _dxPendingFences.Length || fence == 0)
        {
            return;
        }

        _dxPendingFences[targetIndex] = fence;
        _dxPendingFenceStartTicks[targetIndex] = Stopwatch.GetTimestamp();
        SetDxPendingBit(targetIndex);
        EmitDxDiag(
            $"[3D preview] DX interop copy fence exceeded {PreviewGlCommandDrain.DefaultTimeoutNanoseconds / 1_000_000}ms; deferring NV_DX unlock for buffer {targetIndex}.");
    }

    private void ScheduleDxInteropPumpIfNeeded()
    {
        if (Volatile.Read(ref _dxPendingMask) == 0 || !CanAttemptDxInterop)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _dxPumpQueued, 1, 0) != 0)
        {
            return;
        }

        PreviewDesktopWglOwnerThread.Post(
            () =>
            {
                try
                {
                    using (BindOnOwnerThread())
                    {
                        PumpPendingDxReleasesCore();
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _dxPumpQueued, 0);
                }

                if (Volatile.Read(ref _dxPendingMask) != 0 && CanAttemptDxInterop)
                {
                    RequestDxPresentFrame();
                }
            },
            phase: "dx-pump");
    }

    private void PumpPendingDxReleasesCore()
    {
        var mask = Volatile.Read(ref _dxPendingMask);
        if (mask == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < _dxPendingFences.Length; i++)
        {
            if ((mask & (1 << i)) == 0)
            {
                continue;
            }

            var fence = _dxPendingFences[i];
            if (fence == 0)
            {
                ClearDxPendingBit(i);
                continue;
            }

            if (!PreviewGlCommandDrain.IsFenceReady(_gl, fence))
            {
                var ageMs = (now - _dxPendingFenceStartTicks[i]) * 1000.0 / Stopwatch.Frequency;
                if (ageMs >= PendingDxFenceFallbackMs)
                {
                    AbandonDxInteropAfterPendingFenceTimeout(i, fence, ageMs);
                    return;
                }

                continue;
            }

            PreviewGlCommandDrain.DeleteFence(_gl, fence);
            _dxPendingFences[i] = 0;
            _dxPendingFenceStartTicks[i] = 0;
            _dxRenderTargets[i].End();
            if (_dxRenderTargets[i].KeyedMutex?.TryRelease(SharedMutexKey) != true)
            {
                EmitDxDiag("[3D preview] WGL deferred ReleaseSync failed; abandoning DX interop.");
                AbandonDxInteropResources("deferred keyed-mutex release failed");
                return;
            }

            ClearDxPendingBit(i);
            Interlocked.Exchange(ref _dxLastCopyEndTicks, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _dxMutexTimeoutStreak, 0);
            Volatile.Write(ref _dxFront, i);
            Volatile.Write(ref _dxWglPhase, $"published[{i}]");
            _dxWatchdog?.NoteWglHeartbeat();
            RequestDxPresentFrame();
        }
    }

    private void AbandonDxInteropAfterPendingFenceTimeout(int targetIndex, nint fence, double ageMs)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _dxPendingFenceTimeoutLogTicks) >= 2000)
        {
            Interlocked.Exchange(ref _dxPendingFenceTimeoutLogTicks, now);
            EmitDxDiag(
                $"[3D preview] DX interop copy fence for buffer {targetIndex} still unsignaled after {ageMs:0}ms; abandoning GPU interop before wglDXLockObjectsNV can wedge. {BuildDxInteropHangSnapshot()}");
        }

        // Do not unlock/unregister a texture whose GL fence never completed. That can hang
        // inside the driver. Leak this experimental interop resource for process lifetime
        // and let the stable WGL->PBO presentation path take over.
        PreviewGlCommandDrain.DeleteFence(_gl, fence);
        _dxPendingFences[targetIndex] = 0;
        _dxPendingFenceStartTicks[targetIndex] = 0;
        _dxRenderTargets[targetIndex].AbandonInteropResources();
        ClearDxPendingBit(targetIndex);
        AbandonDxInteropResources("copy fence did not signal");
    }

    private void AbandonDxInteropResources(string reason)
    {
        for (var i = 0; i < _dxPendingFences.Length; i++)
        {
            if (_dxPendingFences[i] != 0)
            {
                PreviewGlCommandDrain.DeleteFence(_gl, _dxPendingFences[i]);
                _dxPendingFences[i] = 0;
            }

            _dxPendingFenceStartTicks[i] = 0;
            if (IsDxBufferPending(i))
            {
                _dxRenderTargets[i].AbandonInteropResources();
            }
        }

        _dxInteropDisabled = true;
        Volatile.Write(ref _dxFront, -1);
        Volatile.Write(ref _dxReading, -1);
        Interlocked.Exchange(ref _dxWriting, -1);
        Interlocked.Exchange(ref _dxPendingMask, 0);
        EmitDxDiag($"[3D preview] DX interop disabled: {reason}; falling back to WGL async PBO presentation.");
        RequestDxPresentFrame();
    }

    private void RequestDxPresentFrame()
    {
        try
        {
            _dxRequestPresentFrame?.Invoke();
        }
        catch
        {
            // ignore recovery callback failures
        }
    }

    private void SetDxPendingBit(int targetIndex)
    {
        var bit = 1 << targetIndex;
        int oldValue;
        int newValue;
        do
        {
            oldValue = Volatile.Read(ref _dxPendingMask);
            newValue = oldValue | bit;
        }
        while (Interlocked.CompareExchange(ref _dxPendingMask, newValue, oldValue) != oldValue);
    }

    private void ClearDxPendingBit(int targetIndex)
    {
        var bit = ~(1 << targetIndex);
        int oldValue;
        int newValue;
        do
        {
            oldValue = Volatile.Read(ref _dxPendingMask);
            newValue = oldValue & bit;
        }
        while (Interlocked.CompareExchange(ref _dxPendingMask, newValue, oldValue) != oldValue);
    }

    private void EmitDxDiag(string message)
    {
        try
        {
            var log = _dxDiagnosticLog;
            if (log is not null)
            {
                // Typically OpenGlPreviewBackend.EmitDiagnostic — categorical files + UI filter.
                log(message);
                return;
            }

            // Early init before the preview sink is wired: persist, Debug only if user-relevant.
            if (Services.LogService.WritePreviewDiagnostic(message))
            {
                Debug.WriteLine(message);
            }
        }
        catch
        {
            Debug.WriteLine(message);
        }
    }

    private string BuildDxInteropHangSnapshot()
    {
        var now = Stopwatch.GetTimestamp();
        var freq = Stopwatch.Frequency;
        static string Age(long then, long nowTicks, long frequency) =>
            then == 0 ? "never" : $"{(nowTicks - then) * 1000.0 / frequency:0}ms ago";

        var sb = new StringBuilder(256);
        sb.Append("snapshot={");
        sb.Append($"front={Volatile.Read(ref _dxFront)}, ");
        sb.Append($"writing={Volatile.Read(ref _dxWriting)}, ");
        sb.Append($"reading={Volatile.Read(ref _dxReading)}, ");
        sb.Append($"pendingMask={Volatile.Read(ref _dxPendingMask)}, ");
        sb.Append($"wglPhase={Volatile.Read(ref _dxWglPhase)}, ");
        sb.Append($"ownerPhase={PreviewDesktopWglOwnerThread.CurrentPhase}, ");
        sb.Append($"ownerPending={PreviewDesktopWglOwnerThread.PendingCount}, ");
        sb.Append($"schedule={Age(Volatile.Read(ref _dxLastScheduleTicks), now, freq)}, ");
        sb.Append($"presentStart={Age(Volatile.Read(ref _dxLastPresentStartTicks), now, freq)}, ");
        sb.Append($"presentEnd={Age(Volatile.Read(ref _dxLastPresentEndTicks), now, freq)}, ");
        sb.Append($"writeStart={Age(Volatile.Read(ref _dxLastWriteStartTicks), now, freq)}, ");
        sb.Append($"writeEnd={Age(Volatile.Read(ref _dxLastWriteEndTicks), now, freq)}, ");
        sb.Append($"copyStart={Age(Volatile.Read(ref _dxLastCopyStartTicks), now, freq)}, ");
        sb.Append($"copyEnd={Age(Volatile.Read(ref _dxLastCopyEndTicks), now, freq)}, ");
        sb.Append($"lastFailure={LastInteropFailureSummary}");
        sb.Append('}');
        return sb.ToString();
    }

    private void BlitPrivateColorToSharedFramebuffer(int sharedFramebuffer, int width, int height)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _renderFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)sharedFramebuffer);
        _gl.BlitFramebuffer(
            0,
            0,
            width,
            height,
            0,
            0,
            width,
            height,
            ClearBufferMask.ColorBufferBit,
            GLEnum.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private bool WaitForInFlightDxWrite(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Volatile.Read(ref _dxWriting) >= 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }

    private void NoteAcquireTimeout(string stage)
    {
        var streak = Interlocked.Increment(ref _dxMutexTimeoutStreak);
        if (streak >= MutexTimeoutResetThreshold)
        {
            Interlocked.Exchange(ref _dxMutexTimeoutStreak, 0);
            Interlocked.Exchange(ref _dxMutexResetRequested, 1);
        }

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _dxAcquireTimeoutLogTicks) < 2000)
        {
            return;
        }

        Interlocked.Exchange(ref _dxAcquireTimeoutLogTicks, now);
        _lastInteropFailureDetail = $"IDXGIKeyedMutex.AcquireSync timed out during {stage} (streak={streak})";
        EmitDxDiag($"[3D preview] {_lastInteropFailureDetail}. {BuildDxInteropHangSnapshot()}");
    }

    private static void ClearPresentationFramebuffer(GlInterface glInterface, int framebuffer, int width, int height)
    {
        var esGl = GL.GetApi(glInterface.GetProcAddress);
        esGl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)framebuffer);
        esGl.Viewport(0, 0, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
        esGl.ClearColor(0.01f, 0.012f, 0.02f, 1f);
        esGl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    private void NoteInteropFailure(PreviewDesktopWglDxInteropFailure failure, int detail, string? detailText = null)
    {
        _lastInteropFailure = failure;
        _lastInteropDetail = detail;
        _lastInteropFailureDetail = detailText;
        if (PreviewDesktopWglDxInteropDiagnostics.IsPermanentFailure(failure))
        {
            _dxInteropDisabled = true;
        }
    }

    private bool EnsureDxInteropProcsOnOwnerThread()
    {
        using (BindOnOwnerThread())
        {
            if (PreviewDesktopWglDxInterop.EnsureProcs(GlInterface))
            {
                return true;
            }

            _lastInteropFailure = PreviewDesktopWglDxInteropFailure.ExtensionUnavailable;
            _lastInteropDetail = PreviewWin32Error.GetLastErrorCode();
            _dxInteropExtensionMissing = true;
            _dxInteropDisabled = true;
            return false;
        }
    }

    partial void DestroySidecarGpuResources()
    {
        WaitForInFlightDxWrite(TimeSpan.FromMilliseconds(500));
        _dxWatchdog?.Dispose();
        _dxWatchdog = null;
        _dxDiagnosticLog = null;
        using (BindOnOwnerThread())
        {
            ReleaseOrAbandonPendingDxInteropForShutdown();
            foreach (var target in _dxRenderTargets)
            {
                target.DestroyGlResources(_gl);
            }
        }

        foreach (var target in _dxRenderTargets)
        {
            target.Dispose();
        }

        PreviewOpenGlPresentationBlit.Reset();
        _dxInteropReady = false;
        _dxInteropExtensionMissing = false;
        _dxInteropDisabled = false;
        _dxInteropLogged = false;
        _dxInteropProcsReady = false;
        _dxFront = -1;
        _dxWriting = -1;
        _dxReading = -1;
        _dxPipelineSize = default;
        _dxFirstFrameWaited = 0;
        _dxWglPhase = "idle";
        _dxRequestPresentFrame = null;
        _dxPendingFenceTimeoutLogTicks = 0;
        for (var i = 0; i < _dxPendingFences.Length; i++)
        {
            if (_dxPendingFences[i] != 0)
            {
                PreviewGlCommandDrain.DeleteFence(_gl, _dxPendingFences[i]);
                _dxPendingFences[i] = 0;
            }

            _dxPendingFenceStartTicks[i] = 0;
        }

        _dxPendingMask = 0;
        _dxPumpQueued = 0;
        _lastInteropFailure = PreviewDesktopWglDxInteropFailure.None;
        _lastInteropDetail = 0;
        _lastInteropFailureDetail = null;
    }

    private void ReleaseOrAbandonPendingDxInteropForShutdown()
    {
        var mask = Volatile.Read(ref _dxPendingMask);
        if (mask == 0)
        {
            return;
        }

        for (var i = 0; i < _dxPendingFences.Length; i++)
        {
            if ((mask & (1 << i)) == 0)
            {
                continue;
            }

            var fence = _dxPendingFences[i];
            if (fence != 0 && PreviewGlCommandDrain.IsFenceReady(_gl, fence))
            {
                PreviewGlCommandDrain.DeleteFence(_gl, fence);
                _dxPendingFences[i] = 0;
                _dxRenderTargets[i].End();
                _dxRenderTargets[i].KeyedMutex?.TryRelease(SharedMutexKey);
            }
            else
            {
                if (fence != 0)
                {
                    PreviewGlCommandDrain.DeleteFence(_gl, fence);
                    _dxPendingFences[i] = 0;
                }

                _dxRenderTargets[i].AbandonInteropResources();
            }

            _dxPendingFenceStartTicks[i] = 0;
        }

        Interlocked.Exchange(ref _dxPendingMask, 0);
    }

    private static bool IsDxInteropOptInEnabled()
    {
        var value = Environment.GetEnvironmentVariable(DxInteropOptInEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    internal bool UsesDxInteropPresentation => _dxInteropReady;
}
