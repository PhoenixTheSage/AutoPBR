using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Offscreen color + TAA signal + optional view-normal + depth target for Genesis post effects.
/// Scene renders here, then presents to the default FBO.
/// </summary>
internal sealed class GlSceneCaptureTarget(GL gl, bool useOpenGlEs, bool useFloatColor = false) : IDisposable
{
    private uint _fbo;
    private uint _colorTexture;
    private uint _taaSignalTexture;
    private uint _viewNormalTexture;
    private uint _depthTexture;
    private int _width;
    private int _height;
    private bool _useFloatColor = useFloatColor;
    private bool _includeViewNormals = true;
    private bool _disposed;

    public uint FramebufferHandle => _fbo;
    public uint DepthTextureHandle => _depthTexture;
    public uint TaaSignalTextureHandle => _taaSignalTexture;
    public uint ViewNormalTextureHandle => _viewNormalTexture;
    public bool HasViewNormals => _viewNormalTexture != 0;
    public int Width => _width;
    public int Height => _height;
    public bool IsValid =>
        _fbo != 0 &&
        _colorTexture != 0 &&
        _taaSignalTexture != 0 &&
        _depthTexture != 0;

    public bool EnsureSize(int width, int height, bool? useFloatColor = null, bool requireViewNormals = false)
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

        if (_width == width &&
            _height == height &&
            IsValid &&
            (!requireViewNormals || HasViewNormals))
        {
            return true;
        }

        // Prefer view-normal MRT for screen-space AO; fall back to color+TAA only when not required.
        if (TryCreate(width, height, includeViewNormals: true))
        {
            return true;
        }

        if (requireViewNormals)
        {
            return false;
        }

        return TryCreate(width, height, includeViewNormals: false);
    }

    private bool TryCreate(int width, int height, bool includeViewNormals)
    {
        DestroyGpuResources();
        _width = width;
        _height = height;
        _includeViewNormals = includeViewNormals;

        _colorTexture = CreateColorTexture(width, height, linearFilter: true);
        _taaSignalTexture = CreateColorTexture(width, height, linearFilter: false);
        if (includeViewNormals)
        {
            _viewNormalTexture = CreateColorTexture(width, height, linearFilter: false);
        }

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

        SetNearestClamp(_depthTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.None);

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTexture, 0);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D, _taaSignalTexture, 0);
        if (includeViewNormals && _viewNormalTexture != 0)
        {
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2,
                TextureTarget.Texture2D, _viewNormalTexture, 0);
        }

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

    private uint CreateColorTexture(int width, int height, bool linearFilter)
    {
        var tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        unsafe
        {
            if (_useFloatColor && linearFilter)
            {
                // R11G11B10F: half the bandwidth of RGBA16F for HDR linear scene capture.
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

        var filter = linearFilter ? (int)GLEnum.Linear : (int)GLEnum.Nearest;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return tex;
    }

    private void SetNearestClamp(uint texture)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
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
        // TaaSignal.A holds the foliage/cutout mask for shaft attenuation. Clear it to 0 so
        // sky/empty pixels do not look like foliage (shared ClearColor leaves A=1 otherwise).
        ClearFoliageMaskAttachment();
        ConfigureSceneAttachments();
    }

    public void ClearFoliageMaskAttachment()
    {
        if (!IsValid || _taaSignalTexture == 0)
        {
            return;
        }

        var priorClear = new float[4];
        gl.GetFloat(GetPName.ColorClearValue, priorClear);
        unsafe
        {
            var onlySignal = DrawBufferMode.ColorAttachment1;
            gl.DrawBuffers(1, &onlySignal);
        }

        gl.ClearColor(0f, 0f, 0f, 0f);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        gl.ClearColor(priorClear[0], priorClear[1], priorClear[2], priorClear[3]);
        ConfigureSceneAttachments();
    }

    public void SetDrawColorOnly()
    {
        if (!IsValid)
        {
            return;
        }

        unsafe
        {
            var onlyColor = DrawBufferMode.ColorAttachment0;
            gl.DrawBuffers(1, &onlyColor);
        }
    }

    public void SetDrawSceneAttachments()
    {
        if (!IsValid)
        {
            return;
        }

        ConfigureSceneAttachments();
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

        // Stale errors must not trigger a Y-flip retry: that inverted color vs depth/AO/TAA
        // signal and produced a hard horizontal lighting split that flipped with SSAA parity.
        while (gl.GetError() != GLEnum.NoError)
        {
        }

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, defaultFbo));
        ConfigureDefaultDrawBuffer(defaultFbo);
        // Blit dest is in window coordinates of the draw FBO (viewport does not apply).
        gl.BlitFramebuffer(
            0, 0, _width, _height,
            vpX, vpY, vpX + destW, vpY + destH,
            ClearBufferMask.ColorBufferBit, GLEnum.Linear);
        var err = gl.GetError();

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)Math.Max(0, priorRead));
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, priorDraw));
        return err == GLEnum.NoError;
    }

    private void ConfigureSceneAttachments()
    {
        unsafe
        {
            if (_includeViewNormals && _viewNormalTexture != 0)
            {
                Span<DrawBufferMode> bufs =
                [
                    DrawBufferMode.ColorAttachment0,
                    DrawBufferMode.ColorAttachment1,
                    DrawBufferMode.ColorAttachment2
                ];
                fixed (DrawBufferMode* ptr = bufs)
                {
                    gl.DrawBuffers((uint)bufs.Length, ptr);
                }
            }
            else
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

        if (_viewNormalTexture != 0)
        {
            gl.DeleteTexture(_viewNormalTexture);
            _viewNormalTexture = 0;
        }

        if (_depthTexture != 0)
        {
            gl.DeleteTexture(_depthTexture);
            _depthTexture = 0;
        }

        _width = 0;
        _height = 0;
        _includeViewNormals = false;
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
