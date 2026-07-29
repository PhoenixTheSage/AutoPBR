using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Native desktop OpenGL context used only for 3D preview rendering while Avalonia keeps ANGLE for compositor pacing.
/// </summary>
internal sealed partial class PreviewDesktopWglContext : IDisposable
{
    private readonly IGlContext _context;
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

    private PreviewDesktopWglContext(IGlContext context, GL gl, string versionString)
    {
        _context = context;
        _gl = gl;
        _versionString = versionString;
    }

    internal string VersionString => _versionString;

    internal void Invoke(Action work) => Invoke(work, timeout: null);

    internal void Invoke(Action work, TimeSpan? timeout)
    {
        if (PreviewDesktopWglOwnerThread.IsOwnerThread)
        {
            work();
            return;
        }

        PreviewDesktopWglOwnerThread.Run(work, timeout);
    }

    internal T Invoke<T>(Func<T> work) => Invoke(work, timeout: null);

    internal T Invoke<T>(Func<T> work, TimeSpan? timeout) =>
        PreviewDesktopWglOwnerThread.IsOwnerThread ? work() : PreviewDesktopWglOwnerThread.Run(work, timeout);

    public GlInterface GlInterface => _context.GlInterface;

    internal GL Gl => _gl;

    public int RenderFbo => (int)_renderFbo;

    public static PreviewDesktopWglContext? TryCreate(
        IReadOnlyList<GlVersion> profiles,
        IntPtr presentationD3d11Device,
        Action<string>? log,
        bool probePresentationAdapter = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!PreviewDesktopWglOwnerThread.IsOwnerThread)
        {
            return PreviewDesktopWglOwnerThread.Run(() =>
                TryCreate(profiles, presentationD3d11Device, log, probePresentationAdapter));
        }

        try
        {
            var context = PreviewDesktopWglGpuAffinity.TryCreateSidecarContext(
                profiles,
                presentationD3d11Device,
                log,
                probePresentationAdapter);
            if (context is null)
            {
                return null;
            }

            return TryCreateFromContext(context);
        }
        catch
        {
            return null;
        }
    }

    internal static PreviewDesktopWglContext? TryCreateFromContext(IGlContext context)
    {
        if (!PreviewDesktopWglOwnerThread.IsOwnerThread)
        {
            return PreviewDesktopWglOwnerThread.Run(() => TryCreateFromContext(context));
        }

        using (context.MakeCurrent())
        {
            var gl = GL.GetApi(context.GlInterface.GetProcAddress);
            var probeFb = gl.GenFramebuffer();
            var probeTex = gl.GenTexture();
            var probeRb = gl.GenRenderbuffer();
            gl.DeleteFramebuffer(probeFb);
            gl.DeleteTexture(probeTex);
            gl.DeleteRenderbuffer(probeRb);
            var versionString = ReadGlVersionString(gl);
            return new PreviewDesktopWglContext(context, gl, versionString);
        }
    }

    private static string ReadGlVersionString(GL gl)
    {
        unsafe
        {
            var p = gl.GetString(StringName.Version);
            return p is null ? "(unknown)" : Marshal.PtrToStringUTF8((nint)p) ?? "(unknown)";
        }
    }

    internal IDisposable BindOnOwnerThread()
    {
        if (!PreviewDesktopWglOwnerThread.IsOwnerThread)
        {
            throw new InvalidOperationException("WGL sidecar Bind() must run on the desktop WGL owner thread.");
        }

        return _context.MakeCurrent();
    }

    public static PreviewDesktopWglContext? TryCreate(IReadOnlyList<GlVersion> profiles) =>
        TryCreate(profiles, IntPtr.Zero, null);

    public void EnsureRenderTarget(int width, int height) =>
        Invoke(() =>
        {
            using (BindOnOwnerThread())
            {
                EnsureRenderTargetCore(width, height);
            }
        });

    internal void EnsureRenderTargetCore(int width, int height)
    {
        var size = new PixelSize(Math.Max(1, width), Math.Max(1, height));
        if (size == _renderTargetSize && _renderFbo != 0)
        {
            return;
        }

        DestroyRenderTarget();
        _renderTargetSize = size;

        _colorTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)size.Width, (uint)size.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);

        _depthRenderbuffer = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRenderbuffer);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)size.Width,
            (uint)size.Height);

        _renderFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _renderFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTexture, 0);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depthRenderbuffer);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            DestroyRenderTarget();
            throw new InvalidOperationException($"Desktop WGL preview FBO incomplete: {status}.");
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public unsafe void CopyColorToEsFbo(GlInterface esGlInterface, int destFbo, int width, int height, bool forceSyncPresent = false)
    {
        if (_renderFbo == 0)
        {
            return;
        }

        var pixels = Invoke(() => CollectColorPixels(width, height, forceSyncPresent), TimeSpan.FromSeconds(2));
        if (pixels is null || pixels.Length == 0)
        {
            return;
        }

        var esGl = GL.GetApi(esGlInterface.GetProcAddress);
        esGl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)destFbo);
        esGl.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            FramebufferAttachmentParameterName.ObjectName, out int texObj);
        esGl.BindTexture(TextureTarget.Texture2D, (uint)texObj);
        fixed (byte* p = pixels)
        {
            esGl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height, PixelFormat.Rgba,
                PixelType.UnsignedByte, p);
        }
    }

    public void ScheduleAsyncPboFrame(
        int width,
        int height,
        Action<int> renderCore,
        bool forceSyncPresent,
        Action requestPresentFrame)
    {
        if (Interlocked.CompareExchange(
                ref _asyncFallbackFrameQueued,
                1,
                0) != 0)
        {
            return;
        }

        PreviewDesktopWglOwnerThread.PostDeferred(() =>
        {
            try
            {
                using (BindOnOwnerThread())
                {
                    EnsureRenderTargetCore(width, height);
                    renderCore(RenderFbo);
                    var pixels = CollectColorPixelsCore(
                        width,
                        height,
                        forceSyncPresent);
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
        }, phase: "async-pbo-render");
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
            if (_asyncPresentPixels is null ||
                _asyncPresentSize != new PixelSize(width, height))
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

    private byte[]? CollectColorPixels(int width, int height, bool forceSyncPresent)
    {
        using (BindOnOwnerThread())
        {
            return CollectColorPixelsCore(
                width,
                height,
                forceSyncPresent);
        }
    }

    private byte[]? CollectColorPixelsCore(
        int width,
        int height,
        bool forceSyncPresent)
    {
        _asyncPboReadback ??= new PreviewDesktopWglAsyncPboReadback(_gl);
        return _asyncPboReadback.TryCollect(
                _renderFbo,
                width,
                height,
                out var pixels,
                forceSyncPresent)
            ? pixels.ToArray()
            : null;
    }

    internal bool UsesAsyncPboReadback => _asyncPboReadback?.UsesAsyncPath == true;

    public void Dispose()
    {
        if (PreviewDesktopWglOwnerThread.IsOwnerThread)
        {
            DisposeCore();
            return;
        }

        try
        {
            PreviewDesktopWglOwnerThread.Run(DisposeCore);
        }
        catch
        {
            //
        }
    }

    private void DisposeCore()
    {
        lock (_asyncPresentGate)
        {
            _asyncPresentPixels = null;
            _asyncPresentSize = default;
        }

        DestroySidecarGpuResources();
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

        _context.Dispose();
    }

    partial void DestroySidecarGpuResources();

    private void DestroyRenderTarget()
    {
        _asyncPboReadback?.Dispose();
        _asyncPboReadback = null;

        if (_renderFbo != 0)
        {
            _gl.DeleteFramebuffer(_renderFbo);
            _renderFbo = 0;
        }

        if (_colorTexture != 0)
        {
            _gl.DeleteTexture(_colorTexture);
            _colorTexture = 0;
        }

        if (_depthRenderbuffer != 0)
        {
            _gl.DeleteRenderbuffer(_depthRenderbuffer);
            _depthRenderbuffer = 0;
        }

        _renderTargetSize = default;
    }
}
