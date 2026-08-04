using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Single color attachment FBO for god-ray half-res / history / present buffers.</summary>
internal sealed class GlColorRenderTarget(GL gl, bool useOpenGlEs, bool useFloatColor = false) : IDisposable
{
    private uint _fbo;
    private uint _colorTexture;
    private int _width;
    private int _height;
    private bool _useFloatColor = useFloatColor;
    private bool _floatPreserveAlpha;
    private bool _disposed;

    public uint ColorTextureHandle => _colorTexture;
    public int Width => _width;
    public int Height => _height;
    public bool IsValid => _fbo != 0 && _colorTexture != 0;

    public bool EnsureSize(int width, int height, bool? useFloatColor = null, bool floatPreserveAlpha = false)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (useFloatColor is { } floatColor)
        {
            if ((_useFloatColor != floatColor || _floatPreserveAlpha != floatPreserveAlpha) && IsValid)
            {
                DestroyGpuResources();
            }

            _useFloatColor = floatColor;
            _floatPreserveAlpha = floatPreserveAlpha;
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
            if (_useFloatColor && _floatPreserveAlpha)
            {
                // RGBA16F: linear HDR shafts need alpha for transmittance (scene*T + inscatter).
                gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba16f,
                    (uint)width,
                    (uint)height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.HalfFloat,
                    (void*)0);
            }
            else if (_useFloatColor)
            {
                // R11G11B10F: half the bandwidth of RGBA16F for HDR capture/TAA intermediates.
                // Final scRGB present targets stay RGBA16F (need alpha for HUD compositing).
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

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTexture, 0);
        ConfigureColorAttachment();
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

    public void BindDraw()
    {
        if (!IsValid)
        {
            return;
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        ConfigureColorAttachment();
        gl.Viewport(0, 0, (uint)_width, (uint)_height);
    }

    public bool CopyColorFrom(GlColorRenderTarget source)
    {
        if (!IsValid || !source.IsValid || _width != source._width || _height != source._height)
        {
            return false;
        }

        var priorRead = gl.GetInteger(GetPName.ReadFramebufferBinding);
        var priorDraw = gl.GetInteger(GetPName.DrawFramebufferBinding);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, source._fbo);
        gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fbo);
        ConfigureDrawFramebufferColorAttachment(_fbo);
        gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
            ClearBufferMask.ColorBufferBit, GLEnum.Nearest);
        var err = gl.GetError();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)Math.Max(0, priorRead));
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, priorDraw));
        return err == GLEnum.NoError;
    }

    public bool CopyColorFromFramebuffer(uint readFramebuffer, int width, int height, int srcX = 0, int srcY = 0)
    {
        if (!IsValid || width != _width || height != _height || srcX < 0 || srcY < 0)
        {
            return false;
        }

        var priorRead = gl.GetInteger(GetPName.ReadFramebufferBinding);
        var priorDraw = gl.GetInteger(GetPName.DrawFramebufferBinding);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
        gl.ReadBuffer(readFramebuffer == 0 ? ReadBufferMode.Back : ReadBufferMode.ColorAttachment0);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fbo);
        ConfigureDrawFramebufferColorAttachment(_fbo);
        gl.BlitFramebuffer(
            srcX, srcY, srcX + width, srcY + height,
            0, 0, width, height,
            ClearBufferMask.ColorBufferBit, GLEnum.Nearest);
        var err = gl.GetError();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)Math.Max(0, priorRead));
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, priorDraw));
        return err == GLEnum.NoError;
    }

    public bool BlitColorToFramebuffer(uint drawFramebuffer, int x, int y, int width, int height)
    {
        if (!IsValid || width != _width || height != _height)
        {
            return false;
        }

        var priorRead = gl.GetInteger(GetPName.ReadFramebufferBinding);
        var priorDraw = gl.GetInteger(GetPName.DrawFramebufferBinding);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFramebuffer);
        ConfigureDrawFramebufferColorAttachment(drawFramebuffer);
        gl.BlitFramebuffer(0, 0, width, height, x, y, x + width, y + height,
            ClearBufferMask.ColorBufferBit, GLEnum.Nearest);
        var err = gl.GetError();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)Math.Max(0, priorRead));
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, priorDraw));
        return err == GLEnum.NoError;
    }

    public byte[]? TryReadRgb8(int x, int y, int width, int height, out GLEnum error)
    {
        if (!IsValid ||
            x < 0 || y < 0 ||
            width <= 0 || height <= 0 ||
            x + width > _width || y + height > _height)
        {
            error = GLEnum.InvalidValue;
            return null;
        }

        var priorRead = gl.GetInteger(GetPName.ReadFramebufferBinding);
        try
        {
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            return GlFramebufferReadback.TryReadRgb8(gl, x, y, width, height, out error);
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)Math.Max(0, priorRead));
        }
    }

    private void ConfigureColorAttachment()
    {
        ConfigureDrawFramebufferColorAttachment(_fbo);
    }

    private void ConfigureDrawFramebufferColorAttachment(uint framebuffer)
    {
        if (useOpenGlEs)
        {
            unsafe
            {
                var buf = framebuffer == 0 ? DrawBufferMode.Back : DrawBufferMode.ColorAttachment0;
                gl.DrawBuffers(1, &buf);
            }
        }
        else
        {
            gl.DrawBuffer(framebuffer == 0 ? DrawBufferMode.Back : DrawBufferMode.ColorAttachment0);
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
