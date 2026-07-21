using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Single directional shadow map (Genesis Shadows Phase 2).
/// FBO + depth-only Texture2D configured for hardware comparison sampling
/// (sampler2DShadow / GL_TEXTURE_COMPARE_MODE = GL_COMPARE_REF_TO_TEXTURE).
///
/// Phase 3 (CSM) extension point: this type holds a single map; cascades will introduce
/// either an array texture or per-cascade GlShadowMapTarget instances + a cascade selection
/// step in <see cref="OpenGlPreviewBackend"/>. Keeping the resolution/bias surface area minimal
/// here so cascade plumbing is additive.
/// </summary>
internal sealed class GlShadowMapTarget : IDisposable
{
    private readonly GL _gl;
    private readonly bool _useOpenGlEs;
    private uint _fbo;
    private uint _depthTexture;
    private readonly int _resolution;
    private bool _disposed;

    /// <summary>Saved viewport restored by <see cref="EndShadowPass"/>.</summary>
    private int _savedVpX;
    private int _savedVpY;
    private int _savedVpW;
    private int _savedVpH;
    private int _savedDrawFbo;

    /// <summary>Saved color mask (sRGB-state agnostic) so the main pass can write color again.</summary>
    private bool _savedColorWriteR;
    private bool _savedColorWriteG;
    private bool _savedColorWriteB;
    private bool _savedColorWriteA;

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

    public void BeginShadowPass(float polygonOffsetFactor = 1.25f, float polygonOffsetUnits = 2.5f)
    {
        _savedDrawFbo = _gl.GetInteger(GetPName.DrawFramebufferBinding);

        // Snapshot main viewport so EndShadowPass restores cleanly.
        var vp = new int[4];
        _gl.GetInteger(GetPName.Viewport, vp);
        _savedVpX = vp[0];
        _savedVpY = vp[1];
        _savedVpW = vp[2];
        _savedVpH = vp[3];

        // Snapshot color mask so EndShadowPass restores it (the main pass may not reset all four channels).
        var cm = new bool[4];
        _gl.GetBoolean(GetPName.ColorWritemask, cm);
        _savedColorWriteR = cm[0];
        _savedColorWriteG = cm[1];
        _savedColorWriteB = cm[2];
        _savedColorWriteA = cm[3];

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
        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.ColorMask(_savedColorWriteR, _savedColorWriteG, _savedColorWriteB, _savedColorWriteA);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)Math.Max(0, _savedDrawFbo));
        _gl.Viewport(_savedVpX, _savedVpY, (uint)_savedVpW, (uint)_savedVpH);
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
