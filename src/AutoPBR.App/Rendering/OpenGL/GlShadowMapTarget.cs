using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Explicit GL state restored after a shadow depth pass. Callers must supply this instead of
/// querying the driver (<c>glGet*</c>) so cascade slices stay zero-sync.
/// </summary>
internal readonly struct GlShadowPassRestoreState
{
    public readonly int DrawFbo;
    public readonly int ViewportX;
    public readonly int ViewportY;
    public readonly int ViewportWidth;
    public readonly int ViewportHeight;
    public readonly bool ColorWriteR;
    public readonly bool ColorWriteG;
    public readonly bool ColorWriteB;
    public readonly bool ColorWriteA;

    public GlShadowPassRestoreState(
        int drawFbo,
        int viewportX,
        int viewportY,
        int viewportWidth,
        int viewportHeight,
        bool colorWriteR = true,
        bool colorWriteG = true,
        bool colorWriteB = true,
        bool colorWriteA = true)
    {
        DrawFbo = Math.Max(0, drawFbo);
        ViewportX = viewportX;
        ViewportY = viewportY;
        ViewportWidth = Math.Max(1, viewportWidth);
        ViewportHeight = Math.Max(1, viewportHeight);
        ColorWriteR = colorWriteR;
        ColorWriteG = colorWriteG;
        ColorWriteB = colorWriteB;
        ColorWriteA = colorWriteA;
    }

    public static GlShadowPassRestoreState FromFrame(in GlRenderFrame frame) =>
        new(
            frame.DefaultFbo,
            frame.VpX,
            frame.VpY,
            frame.Vw,
            frame.Vh);
}

/// <summary>
/// Single directional shadow map (Genesis Shadows Phase 2).
/// FBO + depth-only Texture2D configured for hardware comparison sampling
/// (sampler2DShadow / GL_TEXTURE_COMPARE_MODE = GL_COMPARE_REF_TO_TEXTURE).
/// Cascades use separate <see cref="GlShadowMapTarget"/> instances at LOD resolutions.
/// </summary>
internal sealed class GlShadowMapTarget : IDisposable
{
    private readonly GL _gl;
    private readonly bool _useOpenGlEs;
    private uint _fbo;
    private uint _depthTexture;
    private readonly int _resolution;
    private bool _disposed;

    private GlShadowPassRestoreState _restore;
    private bool _passActive;

    public GlShadowMapTarget(GL gl, int resolution, bool useOpenGlEs)
    {
        _gl = gl;
        _useOpenGlEs = useOpenGlEs;
        _resolution = ClampResolution(resolution);

        _depthTexture = _gl.GenTexture();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _depthTexture);

        unsafe
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.DepthComponent24,
                (uint)_resolution,
                (uint)_resolution,
                0,
                PixelFormat.DepthComponent,
                PixelType.UnsignedInt,
                (void*)0);
        }

        // Hardware shadow comparison: sampler2DShadow returns 0..1 PCF-filtered visibility.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
            (int)GLEnum.CompareRefToTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc,
            (int)GLEnum.Lequal);

        // Linear-on-compare gives a 2x2 hardware PCF tap; we'll stack 9 taps in shader for 3x3.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        // ES 3.0 lacks CLAMP_TO_BORDER. Use CLAMP_TO_EDGE and rely on a manual border check in shader
        // (worldToShadowUv returns a flag so out-of-frustum samples evaluate as fully lit).
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _depthTexture, 0);

        // No color attachments: depth-only target. Desktop GL uses glDrawBuffer(GL_NONE); GLES 3.0 /
        // ANGLE do not implement glDrawBuffer — use glDrawBuffers(1, { GL_NONE }) instead. Calling
        // DrawBuffer on ES queues GL_INVALID_OPERATION (often surfaced later on glGetError).
        ConfigureNoColorAttachments();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void ConfigureNoColorAttachments()
    {
        if (_useOpenGlEs)
        {
            unsafe
            {
                var buf = GLEnum.None;
                _gl.DrawBuffers(1, &buf);
            }
        }
        else
        {
            _gl.DrawBuffer(DrawBufferMode.None);
        }

        _gl.ReadBuffer(ReadBufferMode.None);
    }

    public uint DepthTextureHandle => _depthTexture;
    public int Resolution => _resolution;

    /// <summary>
    /// Binds this depth FBO and clears. Caller supplies restore state — no <c>glGet*</c>.
    /// </summary>
    public void BeginShadowPass(
        in GlShadowPassRestoreState restore,
        float polygonOffsetFactor = 1.25f,
        float polygonOffsetUnits = 2.5f)
    {
        _restore = restore;
        _passActive = true;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        ConfigureNoColorAttachments();
        _gl.Viewport(0, 0, (uint)_resolution, (uint)_resolution);
        _gl.ColorMask(false, false, false, false);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.DepthFunc(GLEnum.Lequal);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        // Keep offset modest: large terrain-fitted frustums amplify units into world-space peter-panning.
        _gl.PolygonOffset(MathF.Max(polygonOffsetFactor, 0f), MathF.Max(polygonOffsetUnits, 0f));
        _gl.Clear(ClearBufferMask.DepthBufferBit);
    }

    public void EndShadowPass()
    {
        if (!_passActive)
        {
            return;
        }

        _passActive = false;
        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.ColorMask(
            _restore.ColorWriteR,
            _restore.ColorWriteG,
            _restore.ColorWriteB,
            _restore.ColorWriteA);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)_restore.DrawFbo);
        _gl.Viewport(
            _restore.ViewportX,
            _restore.ViewportY,
            (uint)_restore.ViewportWidth,
            (uint)_restore.ViewportHeight);
    }

    private static int ClampResolution(int requested)
    {
        if (requested < 256)
        {
            return 256;
        }

        if (requested > 4096)
        {
            return 4096;
        }

        return requested;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_fbo != 0)
        {
            _gl.DeleteFramebuffer(_fbo);
            _fbo = 0;
        }

        if (_depthTexture != 0)
        {
            _gl.DeleteTexture(_depthTexture);
            _depthTexture = 0;
        }
    }
}
