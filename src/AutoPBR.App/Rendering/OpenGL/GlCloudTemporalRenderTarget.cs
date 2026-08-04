using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Cloud target with premultiplied radiance/opacity in attachment 0 and representative
/// distance/type metadata in attachment 1. High/Cinematic desktop profiles may add
/// luminance moments in attachment 2. The packed RGBA8 GLES path remains two attachments.
/// </summary>
internal sealed class GlCloudTemporalRenderTarget : IDisposable
{
    private readonly GL _gl;
    private uint _fbo;
    private uint _colorTexture;
    private uint _dataTexture;
    private uint _momentTexture;
    private int _width;
    private int _height;
    private bool _disposed;

    public GlCloudTemporalRenderTarget(GL gl)
        : this(gl, GlCloudRenderFormatProfile.Compatibility)
    {
    }

    public GlCloudTemporalRenderTarget(GL gl, GlCloudRenderFormatProfile profile)
    {
        _gl = gl;
        Profile = profile;
    }

    public GlCloudRenderFormatProfile Profile { get; }
    public uint ColorTextureHandle => _colorTexture;
    public uint DataTextureHandle => _dataTexture;
    public uint MomentTextureHandle => _momentTexture;
    public int Width => _width;
    public int Height => _height;
    public int AttachmentCount => Profile.UsesTemporalMoments ? 3 : 2;
    public bool IsValid =>
        _fbo != 0 &&
        _colorTexture != 0 &&
        _dataTexture != 0 &&
        (!Profile.UsesTemporalMoments || _momentTexture != 0);

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
        _colorTexture = CreateTexture(
            width,
            height,
            Profile.ColorInternalFormat,
            Profile.ColorPixelFormat,
            Profile.ColorPixelType,
            linearFilter: true);
        // Packed two-channel distance must not be interpolated across byte carries.
        // Direct distance/type metadata also remains nearest-filtered so layer identity and
        // representative depth never bleed across a cloud boundary.
        _dataTexture = CreateTexture(
            width,
            height,
            Profile.DataInternalFormat,
            Profile.DataPixelFormat,
            Profile.DataPixelType,
            linearFilter: false);
        if (Profile.UsesTemporalMoments)
        {
            _momentTexture = CreateTexture(
                width,
                height,
                Profile.MomentInternalFormat,
                Profile.MomentPixelFormat,
                Profile.MomentPixelType,
                linearFilter: true);
        }

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTexture, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D, _dataTexture, 0);
        if (Profile.UsesTemporalMoments)
        {
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment2,
                TextureTarget.Texture2D,
                _momentTexture,
                0);
        }

        ConfigureAllAttachments();
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            DestroyGpuResources();
            return false;
        }

        return true;
    }

    public void BindDraw(bool includeMoments = true)
    {
        if (!IsValid)
        {
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        if (Profile.UsesTemporalMoments && !includeMoments)
        {
            ConfigureCloudAttachments();
        }
        else
        {
            ConfigureAllAttachments();
        }
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
    }

    /// <summary>
    /// Clears attachments to the profile's empty-cloud representation. Direct metadata uses a
    /// negative type sentinel; moments use a negative first moment as their invalid sentinel.
    /// </summary>
    public void Clear()
    {
        if (!IsValid)
        {
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        ConfigureSingleDrawAttachment(0);
        _gl.ClearColor(0f, 0f, 0f, 0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        ConfigureSingleDrawAttachment(1);
        _gl.ClearColor(0f, Profile.UsesDirectMetadata ? -1f : 0f, 0f, 0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        if (Profile.UsesTemporalMoments)
        {
            ConfigureSingleDrawAttachment(2);
            _gl.ClearColor(-1f, 0f, 0f, 0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        _gl.ClearColor(0f, 0f, 0f, 0f);
        ConfigureAllAttachments();
    }

    /// <summary>
    /// Copies color + metadata history. Moment attachment failures are non-fatal: color/data
    /// history remains valid and moments are reset to the invalid sentinel so temporal can
    /// fall back to neighborhood clipping instead of wiping the entire CQ1 history.
    /// </summary>
    public bool CopyFrom(GlCloudTemporalRenderTarget source)
    {
        if (!IsValid || !source.IsValid || Profile != source.Profile ||
            _width != source._width || _height != source._height)
        {
            return false;
        }

        var priorRead = _gl.GetInteger(GetPName.ReadFramebufferBinding);
        var priorDraw = _gl.GetInteger(GetPName.DrawFramebufferBinding);
        while (_gl.GetError() != GLEnum.NoError)
        {
            // Attribute only errors produced by the history transfer below.
        }
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, source._fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fbo);

        var ok = true;
        for (var attachment = 0; attachment < AttachmentCount; attachment++)
        {
            _gl.ReadBuffer((ReadBufferMode)((int)ReadBufferMode.ColorAttachment0 + attachment));
            ConfigureSingleDrawAttachment(attachment);
            _gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
                ClearBufferMask.ColorBufferBit, GLEnum.Nearest);
            if (_gl.GetError() == GLEnum.NoError)
            {
                continue;
            }

            // Attachment 0/1 are required. Attachment 2 (moments) must not discard the
            // accepted color/data history or confidence stays near 1/8 forever.
            if (attachment < 2)
            {
                ok = false;
                break;
            }

            ConfigureSingleDrawAttachment(2);
            _gl.ClearColor(-1f, 0f, 0f, 0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            _gl.ClearColor(0f, 0f, 0f, 0f);
            _ = _gl.GetError();
        }

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)Math.Max(0, priorRead));
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)Math.Max(0, priorDraw));
        return ok;
    }

    private uint CreateTexture(
        int width,
        int height,
        InternalFormat internalFormat,
        PixelFormat pixelFormat,
        PixelType pixelType,
        bool linearFilter)
    {
        var texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, (uint)width, (uint)height, 0,
                pixelFormat, pixelType, (void*)0);
        }

        var filter = linearFilter ? GLEnum.Linear : GLEnum.Nearest;
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return texture;
    }

    private unsafe void ConfigureAllAttachments()
    {
        var attachments = stackalloc DrawBufferMode[3];
        attachments[0] = DrawBufferMode.ColorAttachment0;
        attachments[1] = DrawBufferMode.ColorAttachment1;
        attachments[2] = DrawBufferMode.ColorAttachment2;
        _gl.DrawBuffers((uint)AttachmentCount, attachments);
    }

    private unsafe void ConfigureCloudAttachments()
    {
        var attachments = stackalloc DrawBufferMode[2];
        attachments[0] = DrawBufferMode.ColorAttachment0;
        attachments[1] = DrawBufferMode.ColorAttachment1;
        _gl.DrawBuffers(2, attachments);
    }

    private unsafe void ConfigureSingleDrawAttachment(int attachment)
    {
        var buffer = (DrawBufferMode)((int)DrawBufferMode.ColorAttachment0 + attachment);
        _gl.DrawBuffers(1, &buffer);
    }

    private void DestroyGpuResources()
    {
        if (_fbo != 0)
        {
            _gl.DeleteFramebuffer(_fbo);
            _fbo = 0;
        }

        if (_colorTexture != 0)
        {
            _gl.DeleteTexture(_colorTexture);
            _colorTexture = 0;
        }

        if (_dataTexture != 0)
        {
            _gl.DeleteTexture(_dataTexture);
            _dataTexture = 0;
        }

        if (_momentTexture != 0)
        {
            _gl.DeleteTexture(_momentTexture);
            _momentTexture = 0;
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
