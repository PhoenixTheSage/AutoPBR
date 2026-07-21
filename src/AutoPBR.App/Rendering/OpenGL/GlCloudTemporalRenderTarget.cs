using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Half-resolution cloud target with premultiplied radiance/opacity in attachment 0 and
/// packed representative distance/type metadata in attachment 1. RGBA8 keeps the MRT path
/// available on the GLES 3 fallback as well as desktop GL.
/// </summary>
internal sealed class GlCloudTemporalRenderTarget(GL gl) : IDisposable
{
    private uint _fbo;
    private uint _colorTexture;
    private uint _dataTexture;
    private int _width;
    private int _height;
    private bool _disposed;

    public uint ColorTextureHandle => _colorTexture;
    public uint DataTextureHandle => _dataTexture;
    public int Width => _width;
    public int Height => _height;
    public bool IsValid => _fbo != 0 && _colorTexture != 0 && _dataTexture != 0;

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
        _colorTexture = CreateTexture(width, height, linearFilter: true);
        // Packed two-channel distance must not be interpolated across byte carries.
        _dataTexture = CreateTexture(width, height, linearFilter: false);

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTexture, 0);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D, _dataTexture, 0);
        ConfigureBothAttachments();
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            DestroyGpuResources();
            return false;
        }

        return true;
    }

    public void BindDraw()
    {
        if (!IsValid)
        {
            return;
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        ConfigureBothAttachments();
        gl.Viewport(0, 0, (uint)_width, (uint)_height);
    }

    public bool CopyFrom(GlCloudTemporalRenderTarget source)
    {
        if (!IsValid || !source.IsValid || _width != source._width || _height != source._height)
        {
            return false;
        }

        var priorRead = gl.GetInteger(GetPName.ReadFramebufferBinding);
        var priorDraw = gl.GetInteger(GetPName.DrawFramebufferBinding);
        while (gl.GetError() != GLEnum.NoError)
        {
            // Attribute only errors produced by the history transfer below.
        }
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, source._fbo);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fbo);

        var ok = true;
        for (var attachment = 0; attachment < 2; attachment++)
        {
            gl.ReadBuffer((ReadBufferMode)((int)ReadBufferMode.ColorAttachment0 + attachment));
            ConfigureSingleDrawAttachment(attachment);
            gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
                ClearBufferMask.ColorBufferBit, GLEnum.Nearest);
            ok &= gl.GetError() == GLEnum.NoError;
        }

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)Math.Max(0, priorRead));
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, priorDraw));
        return ok;
    }

    private uint CreateTexture(int width, int height, bool linearFilter)
    {
        var texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
        }

        var filter = linearFilter ? GLEnum.Linear : GLEnum.Nearest;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)filter);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)filter);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return texture;
    }

    private unsafe void ConfigureBothAttachments()
    {
        var attachments = stackalloc DrawBufferMode[2];
        attachments[0] = DrawBufferMode.ColorAttachment0;
        attachments[1] = DrawBufferMode.ColorAttachment1;
        gl.DrawBuffers(2, attachments);
    }

    private unsafe void ConfigureSingleDrawAttachment(int attachment)
    {
        var buffer = (DrawBufferMode)((int)DrawBufferMode.ColorAttachment0 + attachment);
        gl.DrawBuffers(1, &buffer);
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

        if (_dataTexture != 0)
        {
            gl.DeleteTexture(_dataTexture);
            _dataTexture = 0;
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
