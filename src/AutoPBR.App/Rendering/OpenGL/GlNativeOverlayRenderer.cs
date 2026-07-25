using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Draws UI-rendered premultiplied BGRA overlay bitmaps into the native WGL backbuffer.</summary>
internal sealed class GlNativeOverlayRenderer : IDisposable
{
    private const string Vert330 = """
#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
out vec2 vUv;
void main()
{
    vUv = aUv;
    gl_Position = vec4(aPos, 0.0, 1.0);
}
""";

    private const string Frag330 = """
#version 330 core
uniform sampler2D uTex;
// 0 = SDR backbuffer. >0 = scRGB scale (paperWhiteNits / 80) so UI matches scene present encode.
uniform float uHdrScRgbScale;
in vec2 vUv;
out vec4 FragColor;
void main()
{
    vec4 s = texture(uTex, vUv);
    // Keep premultiplied alpha as uploaded. Only boost into paper-white scRGB for HDR;
    // unpremultiply/sRGB conversion made glyphs disappear on some Avalonia pixel layouts.
    FragColor = uHdrScRgbScale > 0.0 ? vec4(s.rgb * uHdrScRgbScale, s.a) : s;
}
""";

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly GlPersistentMappedUploadBuffer? _vertexUpload;
    private readonly int _texLoc;
    private readonly int _hdrScaleLoc;
    private readonly OverlayTexture _debugTexture;
    private readonly OverlayTexture _fpsTexture;
    private readonly OverlayTexture _cpuTexture;
    private uint _program;
    private bool _disposed;

    public GlNativeOverlayRenderer(GL gl, bool useOpenGlEs, bool preferPersistentUpload, out string? error)
    {
        _gl = gl;
        error = null;
        var vSrc = GlslSourceAdapter.Adapt(Vert330, ShaderType.VertexShader, useOpenGlEs);
        var fSrc = GlslSourceAdapter.Adapt(Frag330, ShaderType.FragmentShader, useOpenGlEs);
        var vs = Compile(ShaderType.VertexShader, vSrc, ref error);
        if (vs == 0)
        {
            _texLoc = -1;
            _hdrScaleLoc = -1;
            _vao = 0;
            _vertexUpload = null;
            _debugTexture = new OverlayTexture();
            _fpsTexture = new OverlayTexture();
            _cpuTexture = new OverlayTexture();
            return;
        }

        var fs = Compile(ShaderType.FragmentShader, fSrc, ref error);
        if (fs == 0)
        {
            _gl.DeleteShader(vs);
            _texLoc = -1;
            _hdrScaleLoc = -1;
            _vao = 0;
            _vertexUpload = null;
            _debugTexture = new OverlayTexture();
            _fpsTexture = new OverlayTexture();
            _cpuTexture = new OverlayTexture();
            return;
        }

        _program = _gl.CreateProgram();
        _gl.AttachShader(_program, vs);
        _gl.AttachShader(_program, fs);
        _gl.LinkProgram(_program);
        _gl.GetProgram(_program, GLEnum.LinkStatus, out var ok);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        if (ok == 0)
        {
            var linkLog = _gl.GetProgramInfoLog(_program);
            error = string.IsNullOrEmpty(error) ? linkLog : error + "\n" + linkLog;
            _gl.DeleteProgram(_program);
            _program = 0;
            _texLoc = -1;
            _hdrScaleLoc = -1;
            _vao = 0;
            _vertexUpload = null;
            _debugTexture = new OverlayTexture();
            _fpsTexture = new OverlayTexture();
            _cpuTexture = new OverlayTexture();
            return;
        }

        _texLoc = _gl.GetUniformLocation(_program, "uTex");
        _hdrScaleLoc = _gl.GetUniformLocation(_program, "uHdrScRgbScale");
        _vao = _gl.GenVertexArray();
        _vertexUpload = new GlPersistentMappedUploadBuffer(
            _gl,
            BufferTargetARB.ArrayBuffer,
            24 * sizeof(float),
            16,
            preferPersistentUpload);
        _gl.BindVertexArray(_vao);
        _vertexUpload.BindBuffer();
        unsafe
        {
            const int stride = 4 * sizeof(float);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        }

        _gl.BindVertexArray(0);
        _debugTexture = new OverlayTexture(gl);
        _fpsTexture = new OverlayTexture(gl);
        _cpuTexture = new OverlayTexture(gl);
    }

    public bool IsValid => _program != 0 && _vao != 0 && _vertexUpload?.Handle != 0;

    internal bool UsesPersistentVertexUpload => _vertexUpload?.UsesPersistentMapping == true;

    /// <param name="viewportWidth">Viewport width in pixels.</param>
    /// <param name="viewportHeight">Viewport height in pixels.</param>
    /// <param name="marginPixels">Screen margin in pixels for overlay placement.</param>
    /// <param name="debug">Optional debug overlay bitmap.</param>
    /// <param name="fps">Optional FPS overlay bitmap.</param>
    /// <param name="cpu">Optional CPU timing overlay bitmap.</param>
    /// <param name="hdrScRgbScale">
    /// Paper-white scRGB scale (<c>nits/80</c>) when compositing into an HDR linear target; 0 for SDR.
    /// </param>
    public void Draw(
        int viewportWidth,
        int viewportHeight,
        int marginPixels,
        PreviewNativeWglOverlayBitmap? debug,
        PreviewNativeWglOverlayBitmap? fps,
        PreviewNativeWglOverlayBitmap? cpu,
        float hdrScRgbScale = 0f)
    {
        if (!IsValid || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        var depthWasEnabled = _gl.IsEnabled(EnableCap.DepthTest);
        var blendWasEnabled = _gl.IsEnabled(EnableCap.Blend);
        var cullWasEnabled = _gl.IsEnabled(EnableCap.CullFace);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.UseProgram(_program);
        _gl.Uniform1(_texLoc, 0);
        if (_hdrScaleLoc >= 0)
        {
            _gl.Uniform1(_hdrScaleLoc, Math.Max(0f, hdrScRgbScale));
        }

        _gl.BindVertexArray(_vao);

        if (debug is not null)
        {
            DrawBitmap(_debugTexture, debug, marginPixels, marginPixels, viewportWidth, viewportHeight);
        }

        var rightStackY = marginPixels;
        if (fps is not null)
        {
            var x = Math.Max(marginPixels, viewportWidth - fps.Width - marginPixels);
            DrawBitmap(_fpsTexture, fps, x, rightStackY, viewportWidth, viewportHeight);
            rightStackY += fps.Height + Math.Max(4, marginPixels / 2);
        }

        if (cpu is not null)
        {
            var x = Math.Max(marginPixels, viewportWidth - cpu.Width - marginPixels);
            DrawBitmap(_cpuTexture, cpu, x, rightStackY, viewportWidth, viewportHeight);
        }

        _gl.BindVertexArray(0);
        if (depthWasEnabled)
        {
            _gl.Enable(EnableCap.DepthTest);
        }
        else
        {
            _gl.Disable(EnableCap.DepthTest);
        }

        if (blendWasEnabled)
        {
            _gl.Enable(EnableCap.Blend);
        }
        else
        {
            _gl.Disable(EnableCap.Blend);
        }

        if (cullWasEnabled)
        {
            _gl.Enable(EnableCap.CullFace);
        }
        else
        {
            _gl.Disable(EnableCap.CullFace);
        }
    }

    private void DrawBitmap(
        OverlayTexture texture,
        PreviewNativeWglOverlayBitmap bitmap,
        int x,
        int y,
        int viewportWidth,
        int viewportHeight)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.BgraPremultiplied.Length == 0)
        {
            return;
        }

        texture.Upload(bitmap);
        var x0 = PixelToNdcX(x, viewportWidth);
        var x1 = PixelToNdcX(x + bitmap.Width, viewportWidth);
        var y0 = PixelToNdcY(y, viewportHeight);
        var y1 = PixelToNdcY(y + bitmap.Height, viewportHeight);
        // Avalonia CopyPixels returns top-row-first data; OpenGL samples uploaded row 0 at v=0.
        Span<float> vertices =
        [
            x0, y0, 0f, 0f,
            x1, y0, 1f, 0f,
            x1, y1, 1f, 1f,
            x0, y0, 0f, 0f,
            x1, y1, 1f, 1f,
            x0, y1, 0f, 1f
        ];
        _vertexUpload!.Upload(MemoryMarshal.AsBytes(vertices));
        _vertexUpload.BindBuffer();
        ConfigureVertexPointers(_vertexUpload.ActiveOffset);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        _vertexUpload.MarkSubmitted();
    }

    private unsafe void ConfigureVertexPointers(nint byteOffset)
    {
        const int stride = 4 * sizeof(float);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)byteOffset);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(byteOffset + 2 * sizeof(float)));
    }

    private static float PixelToNdcX(int x, int width) => (x / (float)Math.Max(1, width)) * 2f - 1f;

    private static float PixelToNdcY(int y, int height) => 1f - (y / (float)Math.Max(1, height)) * 2f;

    private uint Compile(ShaderType type, string source, ref string? error)
    {
        var s = _gl.CreateShader(type);
        _gl.ShaderSource(s, source);
        _gl.CompileShader(s);
        _gl.GetShader(s, GLEnum.CompileStatus, out var ok);
        if (ok == 0)
        {
            var info = _gl.GetShaderInfoLog(s);
            var sb = new StringBuilder();
            sb.Append(type).Append(" compile failed: ").AppendLine(info);
            error = string.IsNullOrEmpty(error) ? sb.ToString() : error + "\n" + sb;
            _gl.DeleteShader(s);
            return 0;
        }

        return s;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debugTexture.Dispose();
        _fpsTexture.Dispose();
        _cpuTexture.Dispose();
        _vertexUpload?.Dispose();

        if (_vao != 0)
        {
            _gl.DeleteVertexArray(_vao);
        }

        if (_program != 0)
        {
            _gl.DeleteProgram(_program);
            _program = 0;
        }
    }

    private sealed class OverlayTexture : IDisposable
    {
        private readonly GL? _gl;
        private readonly uint _id;
        private int _width;
        private int _height;
        private ulong _fingerprint;
        private bool _hasUpload;

        public OverlayTexture()
        {
        }

        public OverlayTexture(GL gl)
        {
            _gl = gl;
            _id = gl.GenTexture();
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _id);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        }

        public void Upload(PreviewNativeWglOverlayBitmap bitmap)
        {
            if (_gl is null || _id == 0)
            {
                return;
            }

            var fingerprint = GlRgbaFingerprint.Compute(bitmap.BgraPremultiplied);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _id);
            if (_hasUpload &&
                _width == bitmap.Width &&
                _height == bitmap.Height &&
                _fingerprint == fingerprint)
            {
                return;
            }

            if (_hasUpload && _width == bitmap.Width && _height == bitmap.Height)
            {
                ReadOnlySpan<byte> pixels = bitmap.BgraPremultiplied;
                _gl.TexSubImage2D(
                    TextureTarget.Texture2D,
                    0,
                    0,
                    0,
                    (uint)bitmap.Width,
                    (uint)bitmap.Height,
                    PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    pixels);
            }
            else
            {
                ReadOnlySpan<byte> pixels = bitmap.BgraPremultiplied;
                _gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba8,
                    (uint)bitmap.Width,
                    (uint)bitmap.Height,
                    0,
                    PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    pixels);
            }

            _width = bitmap.Width;
            _height = bitmap.Height;
            _fingerprint = fingerprint;
            _hasUpload = true;
        }

        public void Dispose()
        {
            if (_gl is not null && _id != 0)
            {
                _gl.DeleteTexture(_id);
            }
        }
    }
}
