using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Sampleable depth-only FBO for the camera Hi-Z occlusion prepass (desktop GL).
/// Depth is written with color masked off and sampled to build the Hi-Z pyramid.
/// It is intentionally not copied into the scene FBO (shaded terrain depth often differs).
/// </summary>
internal sealed class GlDepthPrepassTarget(GL gl) : IDisposable
{
    private uint _fbo;
    private uint _depthTexture;
    private int _width;
    private int _height;
    private bool _disposed;

    private int _savedDrawFbo;
    private int _savedVpX;
    private int _savedVpY;
    private int _savedVpW;
    private int _savedVpH;

    public uint DepthTextureHandle => _depthTexture;
    public int Width => _width;
    public int Height => _height;
    public bool IsValid => _fbo != 0 && _depthTexture != 0;

    public bool EnsureSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_width == width && _height == height && IsValid)
        {
            return true;
        }

        DestroyGpuResources();
        _width = width;
        _height = height;

        _depthTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _depthTexture);
        unsafe
        {
            gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.DepthComponent24,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.DepthComponent,
                PixelType.UnsignedInt,
                (void*)0);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.None);

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D,
            _depthTexture,
            0);
        gl.DrawBuffer(DrawBufferMode.None);
        gl.ReadBuffer(ReadBufferMode.None);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            DestroyGpuResources();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Bind the depth-only FBO. Caller passes the scene draw FBO/viewport to restore — no glGet syncs.
    /// Color writes are forced off here and restored to full RGBA on <see cref="EndPrepass"/>.
    /// </summary>
    public void BeginPrepass(int restoreDrawFbo, int restoreVpX, int restoreVpY, int restoreVpW, int restoreVpH)
    {
        _savedDrawFbo = restoreDrawFbo;
        _savedVpX = restoreVpX;
        _savedVpY = restoreVpY;
        _savedVpW = Math.Max(1, restoreVpW);
        _savedVpH = Math.Max(1, restoreVpH);

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.DrawBuffer(DrawBufferMode.None);
        gl.Viewport(0, 0, (uint)_width, (uint)_height);
        gl.ColorMask(false, false, false, false);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.DepthFunc(GLEnum.Lequal);
        gl.Clear(ClearBufferMask.DepthBufferBit);
    }

    public void EndPrepass()
    {
        gl.ColorMask(true, true, true, true);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)Math.Max(0, _savedDrawFbo));
        gl.Viewport(_savedVpX, _savedVpY, (uint)_savedVpW, (uint)_savedVpH);
    }

    /// <summary>Copy prepass depth into the currently bound draw framebuffer's depth attachment.</summary>
    public void BlitDepthToDrawFramebuffer(int dstWidth, int dstHeight)
    {
        if (!IsValid)
        {
            return;
        }

        var drawFbo = gl.GetInteger(GetPName.DrawFramebufferBinding);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, drawFbo));
        gl.BlitFramebuffer(
            0, 0, _width, _height,
            0, 0, Math.Max(1, dstWidth), Math.Max(1, dstHeight),
            ClearBufferMask.DepthBufferBit,
            BlitFramebufferFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)Math.Max(0, drawFbo));
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

    private void DestroyGpuResources()
    {
        if (_fbo != 0)
        {
            gl.DeleteFramebuffer(_fbo);
            _fbo = 0;
        }

        if (_depthTexture != 0)
        {
            gl.DeleteTexture(_depthTexture);
            _depthTexture = 0;
        }

        _width = 0;
        _height = 0;
    }
}
