using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Offscreen color + depth target for Genesis god rays: scene renders here, then presents to the default FBO.
/// </summary>
internal sealed class GlSceneCaptureTarget(GL gl, bool useOpenGlEs, bool useFloatColor = false) : IDisposable
{
    private uint _fbo;
    private uint _colorTexture;
    private uint _taaSignalTexture;
    private uint _depthTexture;
    private int _width;
    private int _height;
    private bool _useFloatColor = useFloatColor;
    private bool _disposed;

    public uint FramebufferHandle => _fbo;
    public uint DepthTextureHandle => _depthTexture;
    public uint TaaSignalTextureHandle => _taaSignalTexture;
    public int Width => _width;
    public int Height => _height;
    public bool IsValid => _fbo != 0 && _colorTexture != 0 && _taaSignalTexture != 0 && _depthTexture != 0;
    public bool UsesFloatColor => _useFloatColor;

    public bool EnsureSize(int width, int height, bool? useFloatColor = null)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (useFloatColor is { } floatColor)
        {
            if (_useFloatColor != floatColor && IsValid)
            {
                DestroyGpuResources();
            }

            _useFloatColor = floatColor;
        }

        if (_width == width && _height == height && IsValid)
        {
            return true;
        }

        DestroyGpuResources();
        _width = width;
        _height = height;

        _colorTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
        unsafe
        {
            if (_useFloatColor)
            {
                // R11G11B10F: half the bandwidth of RGBA16F for HDR linear scene capture.
                // Private HDR present FBO / DXGI shared buffers remain RGBA16F.
                gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.R11fG11fB10f,
                    (uint)width,
                    (uint)height,
                    0,
                    PixelFormat.Rgb,
                    PixelType.UnsignedInt10f11f11fRev,
                    (void*)0);
            }
            else
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
            }
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _taaSignalTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _taaSignalTexture);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _depthTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _depthTexture);
        unsafe
        {
            if (useOpenGlEs)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Depth24Stencil8, (uint)width, (uint)height, 0,
                    PixelFormat.DepthStencil, PixelType.UnsignedInt248, (void*)0);
            }
            else
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)width, (uint)height, 0,
                    PixelFormat.DepthComponent, PixelType.UnsignedInt, (void*)0);
            }
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.None);

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTexture, 0);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D, _taaSignalTexture, 0);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _depthTexture, 0);
        ConfigureSceneAttachments();
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            DestroyGpuResources();
            return false;
        }

        return IsValid;
    }

    public void BindDraw(int width, int height)
    {
        if (!IsValid)
        {
            return;
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        ConfigureSceneAttachments();
        gl.Viewport(0, 0, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    /// <summary>Bind capture FBO for compositing into the existing color buffer (no clear).</summary>
    public void BindComposite(int width, int height)
    {
        if (!IsValid)
        {
            return;
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        ConfigureColorAttachment0();
        gl.Viewport(0, 0, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
    }

    public uint ColorTextureHandle => _colorTexture;
    public bool BlitColorToDefault(int defaultFbo, int vpX, int vpY, int destW, int destH)
    {
        if (!IsValid)
        {
            return false;
        }

        destW = Math.Max(1, destW);
        destH = Math.Max(1, destH);

        var priorRead = gl.GetInteger(GetPName.ReadFramebufferBinding);
        var priorDraw = gl.GetInteger(GetPName.DrawFramebufferBinding);

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, defaultFbo));
        gl.Viewport(vpX, vpY, (uint)destW, (uint)destH);
        ConfigureDefaultDrawBuffer(defaultFbo);
        gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, destW, destH,
            ClearBufferMask.ColorBufferBit, GLEnum.Linear);
        var err = gl.GetError();
        if (err != GLEnum.NoError)
        {
            gl.BlitFramebuffer(0, 0, _width, _height, 0, destH, destW, 0,
                ClearBufferMask.ColorBufferBit, GLEnum.Linear);
            err = gl.GetError();
        }

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)Math.Max(0, priorRead));
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, priorDraw));
        return err == GLEnum.NoError;
    }

    private void ConfigureSceneAttachments()
    {
        unsafe
        {
            Span<DrawBufferMode> bufs =
            [
                DrawBufferMode.ColorAttachment0,
                DrawBufferMode.ColorAttachment1
            ];
            fixed (DrawBufferMode* ptr = bufs)
            {
                gl.DrawBuffers((uint)bufs.Length, ptr);
            }
        }
    }

    private void ConfigureColorAttachment0()
    {
        if (useOpenGlEs)
        {
            unsafe
            {
                var buf = DrawBufferMode.ColorAttachment0;
                gl.DrawBuffers(1, &buf);
            }
        }
        else
        {
            gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        }
    }

    private void ConfigureDefaultDrawBuffer(int defaultFbo)
    {
        if (useOpenGlEs)
        {
            unsafe
            {
                var buf = defaultFbo == 0 ? DrawBufferMode.Back : DrawBufferMode.ColorAttachment0;
                gl.DrawBuffers(1, &buf);
            }
        }
        else
        {
            gl.DrawBuffer(defaultFbo == 0 ? DrawBufferMode.Back : DrawBufferMode.ColorAttachment0);
        }
    }

    private void DestroyGpuResources()
    {
        if (_fbo != 0)
        {
            gl.DeleteFramebuffer(_fbo);
            _fbo = 0;
        }

        if (_colorTexture != 0)
        {
            gl.DeleteTexture(_colorTexture);
            _colorTexture = 0;
        }

        if (_taaSignalTexture != 0)
        {
            gl.DeleteTexture(_taaSignalTexture);
            _taaSignalTexture = 0;
        }

        if (_depthTexture != 0)
        {
            gl.DeleteTexture(_depthTexture);
            _depthTexture = 0;
        }

        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyGpuResources();
    }
}
