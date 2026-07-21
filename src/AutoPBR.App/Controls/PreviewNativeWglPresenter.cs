using AutoPBR.App.Rendering.OpenGL;

using Avalonia.OpenGL;
using Avalonia.Threading;

namespace AutoPBR.App.Controls;

/// <summary>Direct GPU presentation path: Genesis renders into a native child HWND and WGL swaps it.</summary>
internal sealed class PreviewNativeWglPresenter : IDisposable
{
    private readonly GlPbrPreviewControl _owner;
    private readonly OpenGlPreviewBackend _backend;
    private readonly Action _requestFallback;
    private readonly IntPtr _hwnd;
    private PreviewDesktopWglBootstrap.ISwapBuffersContext? _context;
    private int _frameQueued;
    private bool _ready;
    private bool _disposed;
    private bool _glInitialized;

    public PreviewNativeWglPresenter(
        GlPbrPreviewControl owner,
        OpenGlPreviewBackend backend,
        Action requestFallback,
        IntPtr hwnd)
    {
        _owner = owner;
        _backend = backend;
        _requestFallback = requestFallback;
        _hwnd = hwnd;
    }

    public bool IsReady => _ready && !_disposed;

    public GlInterface? GlInterface => _context?.GlInterface;

    public bool TryAttach()
    {
        if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero)
        {
            return false;
        }

        StartWglInit();
        return true;
    }

    public void RequestFrame()
    {
        if (!IsReady || Interlocked.Exchange(ref _frameQueued, 1) != 0)
        {
            return;
        }

        // Always enqueue. Post() would invoke inline on the owner thread and recurse forever when
        // continuous rendering re-requests the next frame from RenderFrameOnOwnerThread.
        PreviewDesktopWglOwnerThread.PostDeferred(RenderFrameOnOwnerThread, phase: "native-wgl-render");
    }

    public void ConfigureVsync(bool enabled)
    {
        var context = _context;
        if (context is null || _disposed)
        {
            return;
        }

        PreviewDesktopWglOwnerThread.Post(
            () =>
            {
                if (_disposed)
                {
                    return;
                }

                using (context.MakeCurrent())
                {
                    _backend.ConfigurePresentationVsync(
                        context.GlInterface,
                        enabled,
                        PreviewDisplayRefreshRate.TryGetForWindow(_hwnd));
                }
            },
            phase: "native-wgl-vsync");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ready = false;
        Interlocked.Exchange(ref _frameQueued, 1);
        var context = _context;
        _context = null;
        if (context is null)
        {
            return;
        }

        try
        {
            PreviewDesktopWglOwnerThread.Run(
                () =>
                {
                    if (_glInitialized)
                    {
                        using (context.MakeCurrent())
                        {
                            _backend.GlDeinit(context.GlInterface);
                        }
                    }

                    context.Dispose();
                },
                TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _backend.EmitPreviewDiagnostic($"[3D preview] Native WGL teardown failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void StartWglInit()
    {
        var hwnd = _hwnd;
        PreviewDesktopWglOwnerThread.Post(
            () =>
            {
                var profiles = new[]
                {
                    new GlVersion(GlProfileType.OpenGL, 4, 6),
                    new GlVersion(GlProfileType.OpenGL, 4, 0),
                    new GlVersion(GlProfileType.OpenGL, 3, 3),
                };

                var context = PreviewDesktopWglBootstrap.TryCreateContextForWindow(
                    hwnd,
                    profiles,
                    _backend.EmitPreviewDiagnostic);
                if (context is null)
                {
                    Dispatcher.UIThread.Post(NotifyFallback, DispatcherPriority.Background);
                    return;
                }

                if (_disposed)
                {
                    context.Dispose();
                    return;
                }

                _context = context;
                using (context.MakeCurrent())
                {
                    _backend.GlInitNativeWglPresenter(context.GlInterface);
                    _glInitialized = true;
                }

                _ready = true;
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        _backend.EmitPreviewDiagnostic("[3D preview] Native WGL child presentation active; frames swap directly on the GPU.");
                        ConfigureVsync(_owner.PresentationVsyncEnabled);
                        _owner.OnNativeWglReady();
                        RequestFrame();
                    },
                    DispatcherPriority.Background);
            },
            phase: "native-wgl-init");
    }

    private void RenderFrameOnOwnerThread()
    {
        Interlocked.Exchange(ref _frameQueued, 0);
        var context = _context;
        if (context is null || !_ready || _disposed)
        {
            return;
        }

        try
        {
            using (context.MakeCurrent())
            {
                _owner.RenderNativeWglFrame(context);
            }
        }
        catch (Exception ex)
        {
            _backend.EmitPreviewDiagnostic(
                $"[3D preview] Native WGL frame failed ({ex.GetType().Name}: {ex.Message}).");
            // Avoid a tight crash loop from continuous RequestFrame after a native fault.
            return;
        }

        if (_disposed)
        {
            return;
        }

        if (_backend.NeedsContinuousRendering)
        {
            // Stay on the owner thread (per-frame UI hops starve Avalonia). PostDeferred avoids
            // stack overflow from inline Post-on-owner re-entry.
            RequestFrame();
            return;
        }

        Dispatcher.UIThread.Post(_owner.OnNativeWglFrameCompleted, DispatcherPriority.Background);
    }

    private void NotifyFallback()
    {
        if (_disposed)
        {
            return;
        }

        _backend.EmitPreviewDiagnostic("[3D preview] Native WGL child presentation unavailable; falling back to ANGLE/sidecar path.");
        _requestFallback();
    }
}
