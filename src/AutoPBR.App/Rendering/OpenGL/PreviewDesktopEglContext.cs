using Avalonia;
using Avalonia.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Linux EGL desktop OpenGL sidecar; presents via async PBO into the Avalonia composition FBO.</summary>
internal sealed class PreviewDesktopEglContext : IPreviewDesktopGlSidecar
{
    private readonly PreviewDesktopEglBootstrap.EglDesktopContext _native;
    private readonly GL _gl;
    private readonly string _versionString;
    private uint _renderFbo;
    private uint _colorTexture;
    private uint _depthRenderbuffer;
    private PixelSize _renderTargetSize;
    private PreviewDesktopWglAsyncPboReadback? _asyncPboReadback;
    private readonly object _asyncPresentGate = new();
    private byte[]? _asyncPresentPixels;
    private PixelSize _asyncPresentSize;
    private int _asyncFallbackFrameQueued;

    private PreviewDesktopEglContext(
        PreviewDesktopEglBootstrap.EglDesktopContext native,
        GL gl,
        string versionString)
    {
        _native = native;
        _gl = gl;
        _versionString = versionString;
        GlInterface = native.GlInterface;
    }

    public GlInterface GlInterface { get; }

    public GL Gl => _gl;

    public string VersionString => _versionString;

    public bool CanAttemptDxInterop => false;

    public bool DxInteropOptInEnabled => false;

    public string LastInteropFailureSummary => "DX interop is Windows-only.";

    public bool UsesAsyncPboReadback => _asyncPboReadback?.UsesAsyncPath == true;

    public bool IsOwnerThreadLikelyWedged => false;

    public bool UsesDxInteropPresentation => false;

    public void EnableDxInteropHangDiagnostics(Action<string> log, Action? requestPresentFrame)
    {
        _ = log;
        _ = requestPresentFrame;
    }

    public bool TryRenderViaDxInterop(
        PreviewOpenGlCompositionBridge compositionBridge,
        GlInterface presentationGl,
        int framebuffer,
        int pixelWidth,
        int pixelHeight,
        Action<int> renderCore) =>
        false;

    public void Invoke(Action work)
    {
        if (PreviewDesktopEglOwnerThread.IsOwnerThread)
        {
            work();
            return;
        }

        PreviewDesktopEglOwnerThread.Run(work);
    }

    public IDisposable BindOnOwnerThread()
    {
        _native.MakeCurrent();
        return new MakeCurrentScope();
    }

    public static bool TryProbeSupported()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            return PreviewDesktopEglOwnerThread.Run(PreviewDesktopEglBootstrap.TryProbe, TimeSpan.FromSeconds(5));
        }
        catch
        {
            return false;
        }
    }

    public static PreviewDesktopEglContext? TryCreate(Action<string>? log)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        if (!PreviewDesktopEglOwnerThread.IsOwnerThread)
        {
            return PreviewDesktopEglOwnerThread.Run(() => TryCreate(log), TimeSpan.FromSeconds(30));
        }

        var profiles = new[]
        {
            new GlVersion(GlProfileType.OpenGL, 4, 6),
            new GlVersion(GlProfileType.OpenGL, 4, 0),
            new GlVersion(GlProfileType.OpenGL, 3, 3),
        };
        var native = PreviewDesktopEglBootstrap.TryCreate(profiles, log);
        if (native is null)
        {
            return null;
        }

        try
        {
            native.MakeCurrent();
            var gl = GL.GetApi(PreviewDesktopEglBootstrap.GetProcAddress);
            string version;
            unsafe
            {
                var p = gl.GetString(StringName.Version);
                version = p is null ? "(unknown)" : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)p) ?? "(unknown)";
            }

            log?.Invoke("[3D preview] EGL desktop sidecar active: " + version);
            return new PreviewDesktopEglContext(native, gl, version);
        }
        catch (Exception ex)
        {
            log?.Invoke("[3D preview] EGL sidecar wrap failed: " + ex.Message);
            native.Dispose();
            return null;
        }
    }

    public void ScheduleAsyncPboFrame(
        int width,
        int height,
        Action<int> renderCore,
        bool forceSyncPresent,
        Action requestPresentFrame)
    {
        if (Interlocked.CompareExchange(ref _asyncFallbackFrameQueued, 1, 0) != 0)
        {
            return;
        }

        PreviewDesktopEglOwnerThread.PostDeferred(() =>
        {
            try
            {
                using (BindOnOwnerThread())
                {
                    EnsureRenderTargetCore(width, height);
                    renderCore((int)_renderFbo);
                    var pixels = CollectColorPixelsCore(width, height, forceSyncPresent);
                    if (pixels is not null)
                    {
                        lock (_asyncPresentGate)
                        {
                            _asyncPresentPixels = pixels;
                            _asyncPresentSize = new PixelSize(width, height);
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _asyncFallbackFrameQueued, 0);
                requestPresentFrame();
            }
        }, phase: "egl-async-pbo-render");
    }

    public unsafe bool TryCopyLatestColorToEsFbo(
        GlInterface esGlInterface,
        int destFbo,
        int width,
        int height)
    {
        byte[]? pixels;
        lock (_asyncPresentGate)
        {
            if (_asyncPresentPixels is null || _asyncPresentSize != new PixelSize(width, height))
            {
                return false;
            }

            pixels = _asyncPresentPixels;
        }

        var esGl = GL.GetApi(esGlInterface.GetProcAddress);
        esGl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)destFbo);
        esGl.GetFramebufferAttachmentParameter(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            FramebufferAttachmentParameterName.ObjectName,
            out int texObj);
        esGl.BindTexture(TextureTarget.Texture2D, (uint)texObj);
        fixed (byte* p = pixels)
        {
            esGl.TexSubImage2D(
                TextureTarget.Texture2D,
                0,
                0,
                0,
                (uint)width,
                (uint)height,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                p);
        }

        return true;
    }

    private unsafe void EnsureRenderTargetCore(int width, int height)
    {
        var size = new PixelSize(Math.Max(1, width), Math.Max(1, height));
        if (_renderFbo != 0 && _renderTargetSize == size)
        {
            return;
        }

        DestroyRenderTarget();
        _renderTargetSize = size;
        _gl.GenFramebuffers(1, out _renderFbo);
        _gl.GenTextures(1, out _colorTexture);
        _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
        _gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            InternalFormat.Rgba8,
            (uint)size.Width,
            (uint)size.Height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.GenRenderbuffers(1, out _depthRenderbuffer);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRenderbuffer);
        _gl.RenderbufferStorage(
            RenderbufferTarget.Renderbuffer,
            InternalFormat.Depth24Stencil8,
            (uint)size.Width,
            (uint)size.Height);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _renderFbo);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _colorTexture,
            0);
        _gl.FramebufferRenderbuffer(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthStencilAttachment,
            RenderbufferTarget.Renderbuffer,
            _depthRenderbuffer);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            DestroyRenderTarget();
            throw new InvalidOperationException($"EGL preview FBO incomplete: {status}.");
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private byte[]? CollectColorPixelsCore(int width, int height, bool forceSyncPresent)
    {
        _asyncPboReadback ??= new PreviewDesktopWglAsyncPboReadback(_gl);
        return _asyncPboReadback.TryCollect(_renderFbo, width, height, out var pixels, forceSyncPresent)
            ? pixels.ToArray()
            : null;
    }

    private void DestroyRenderTarget()
    {
        if (_depthRenderbuffer != 0)
        {
            _gl.DeleteRenderbuffer(_depthRenderbuffer);
            _depthRenderbuffer = 0;
        }

        if (_colorTexture != 0)
        {
            _gl.DeleteTexture(_colorTexture);
            _colorTexture = 0;
        }

        if (_renderFbo != 0)
        {
            _gl.DeleteFramebuffer(_renderFbo);
            _renderFbo = 0;
        }

        _renderTargetSize = default;
    }

    public void Dispose()
    {
        void Core()
        {
            lock (_asyncPresentGate)
            {
                _asyncPresentPixels = null;
            }

            _asyncPboReadback?.Dispose();
            _asyncPboReadback = null;
            try
            {
                using (BindOnOwnerThread())
                {
                    DestroyRenderTarget();
                }
            }
            catch
            {
                //
            }

            _native.Dispose();
        }

        if (PreviewDesktopEglOwnerThread.IsOwnerThread)
        {
            Core();
            return;
        }

        try
        {
            PreviewDesktopEglOwnerThread.Run(Core);
        }
        catch
        {
            //
        }
    }

    private sealed class MakeCurrentScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
