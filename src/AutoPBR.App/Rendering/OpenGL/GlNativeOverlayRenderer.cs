using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// ImGui-style native WGL HUD: samples a font atlas and draws panel/glyph quads.
/// Atlas upload and vertex rebuild are dirty-flagged; steady-state Overlay CPU stays near zero.
/// </summary>
internal sealed class GlNativeOverlayRenderer : IDisposable
{
    private const string Vert330 = """
#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;
out vec2 vUv;
out vec4 vColor;
void main()
{
    vUv = aUv;
    vColor = aColor;
    gl_Position = vec4(aPos, 0.0, 1.0);
}
""";

    private const string Frag330 = """
#version 330 core
uniform sampler2D uTex;
// 0 = SDR backbuffer. >0 = scRGB scale (paperWhiteNits / 80) so UI matches scene present encode.
uniform float uHdrScRgbScale;
in vec2 vUv;
in vec4 vColor;
out vec4 FragColor;
void main()
{
    vec4 t = texture(uTex, vUv);
    float coverage = t.a;
    vec4 premul = vec4(vColor.rgb * vColor.a * coverage, vColor.a * coverage);
    FragColor = uHdrScRgbScale > 0.0 ? vec4(premul.rgb * uHdrScRgbScale, premul.a) : premul;
}
""";

    /// <summary>
    /// Max verts for one Overlay draw (~2k quads). Expanded HUD fits well under this.
    /// </summary>
    private const int MaxVertexFloats = 4096 * GlOverlayTextLayout.VerticesPerQuad * GlOverlayTextLayout.FloatsPerVertex;

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly GlPersistentMappedUploadBuffer? _vertexUpload;
    private readonly int _texLoc;
    private readonly int _hdrScaleLoc;
    private readonly AtlasTexture _atlasTexture;
    private uint _program;
    private bool _disposed;

    private GlOverlayFontAtlas? _layoutAtlas;
    private string? _layoutDebug;
    private string? _layoutFps;
    private string? _layoutCpu;
    private int _layoutViewportW;
    private int _layoutViewportH;
    private int _layoutMargin;
    private float[]? _cachedVerts;
    private int _cachedVertexCount;

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
            _atlasTexture = new AtlasTexture();
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
            _atlasTexture = new AtlasTexture();
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
            _atlasTexture = new AtlasTexture();
            return;
        }

        _texLoc = _gl.GetUniformLocation(_program, "uTex");
        _hdrScaleLoc = _gl.GetUniformLocation(_program, "uHdrScRgbScale");
        _vao = _gl.GenVertexArray();
        _vertexUpload = new GlPersistentMappedUploadBuffer(
            _gl,
            BufferTargetARB.ArrayBuffer,
            MaxVertexFloats * sizeof(float),
            16,
            preferPersistentUpload);
        _gl.BindVertexArray(_vao);
        _vertexUpload.BindBuffer();
        unsafe
        {
            const int stride = GlOverlayTextLayout.FloatsPerVertex * sizeof(float);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        }

        _gl.BindVertexArray(0);
        _atlasTexture = new AtlasTexture(gl);
    }

    public bool IsValid => _program != 0 && _vao != 0 && _vertexUpload?.Handle != 0;

    internal bool UsesPersistentVertexUpload => _vertexUpload?.UsesPersistentMapping == true;

    /// <param name="hdrScRgbScale">
    /// Paper-white scRGB scale (<c>nits/80</c>) when compositing into an HDR linear target; 0 for SDR.
    /// </param>
    public void DrawTexts(
        int viewportWidth,
        int viewportHeight,
        int marginPixels,
        GlOverlayFontAtlas? atlas,
        string? debugText,
        string? fpsText,
        string? cpuText,
        float hdrScRgbScale = 0f)
    {
        if (!IsValid || viewportWidth <= 0 || viewportHeight <= 0 || atlas is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(debugText) &&
            string.IsNullOrWhiteSpace(fpsText) &&
            string.IsNullOrWhiteSpace(cpuText))
        {
            return;
        }

        EnsureLayout(
            atlas,
            debugText,
            fpsText,
            cpuText,
            viewportWidth,
            viewportHeight,
            marginPixels);

        if (_cachedVerts is null || _cachedVertexCount <= 0)
        {
            return;
        }

        var byteCount = _cachedVertexCount * GlOverlayTextLayout.FloatsPerVertex * sizeof(float);
        if (byteCount > MaxVertexFloats * sizeof(float))
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

        _atlasTexture.Upload(atlas);
        _gl.BindVertexArray(_vao);
        _vertexUpload!.Upload(MemoryMarshal.AsBytes(_cachedVerts.AsSpan(0, _cachedVertexCount * GlOverlayTextLayout.FloatsPerVertex)));
        _vertexUpload.BindBuffer();
        ConfigureVertexPointers(_vertexUpload.ActiveOffset);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_cachedVertexCount);
        _vertexUpload.MarkSubmitted();
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

    private void EnsureLayout(
        GlOverlayFontAtlas atlas,
        string? debugText,
        string? fpsText,
        string? cpuText,
        int viewportWidth,
        int viewportHeight,
        int marginPixels)
    {
        if (ReferenceEquals(_layoutAtlas, atlas) &&
            _layoutViewportW == viewportWidth &&
            _layoutViewportH == viewportHeight &&
            _layoutMargin == marginPixels &&
            string.Equals(_layoutDebug, debugText, StringComparison.Ordinal) &&
            string.Equals(_layoutFps, fpsText, StringComparison.Ordinal) &&
            string.Equals(_layoutCpu, cpuText, StringComparison.Ordinal) &&
            _cachedVerts is not null)
        {
            return;
        }

        _cachedVerts = GlOverlayTextLayout.Build(
            atlas,
            debugText,
            fpsText,
            cpuText,
            viewportWidth,
            viewportHeight,
            marginPixels,
            out _cachedVertexCount);
        _layoutAtlas = atlas;
        _layoutDebug = debugText;
        _layoutFps = fpsText;
        _layoutCpu = cpuText;
        _layoutViewportW = viewportWidth;
        _layoutViewportH = viewportHeight;
        _layoutMargin = marginPixels;
    }

    private unsafe void ConfigureVertexPointers(nint byteOffset)
    {
        const int stride = GlOverlayTextLayout.FloatsPerVertex * sizeof(float);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)byteOffset);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(byteOffset + 2 * sizeof(float)));
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(byteOffset + 4 * sizeof(float)));
    }

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
        _atlasTexture.Dispose();
        _vertexUpload?.Dispose();
        _cachedVerts = null;
        _layoutAtlas = null;

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

    private sealed class AtlasTexture : IDisposable
    {
        private readonly GL? _gl;
        private readonly uint _id;
        private GlOverlayFontAtlas? _uploaded;
        private bool _hasUpload;

        public AtlasTexture()
        {
        }

        public AtlasTexture(GL gl)
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

        public void Upload(GlOverlayFontAtlas atlas)
        {
            if (_gl is null || _id == 0)
            {
                return;
            }

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _id);
            if (_hasUpload && ReferenceEquals(_uploaded, atlas))
            {
                return;
            }

            ReadOnlySpan<byte> pixels = atlas.BgraPremultiplied;
            if (_hasUpload &&
                _uploaded is not null &&
                _uploaded.Width == atlas.Width &&
                _uploaded.Height == atlas.Height)
            {
                _gl.TexSubImage2D(
                    TextureTarget.Texture2D,
                    0,
                    0,
                    0,
                    (uint)atlas.Width,
                    (uint)atlas.Height,
                    PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    pixels);
            }
            else
            {
                _gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba8,
                    (uint)atlas.Width,
                    (uint)atlas.Height,
                    0,
                    PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    pixels);
            }

            _uploaded = atlas;
            _hasUpload = true;
        }

        public void Dispose()
        {
            _uploaded = null;
            if (_gl is not null && _id != 0)
            {
                _gl.DeleteTexture(_id);
            }
        }
    }
}
