using System.Text;

using JetBrains.Annotations;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Unlit lines (MVP + vertex color) for grid and axis overlay in the preview.</summary>
internal sealed class GlLineShaderProgram : IDisposable
{
    private const string Vert330 = """
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec4 aColor;
uniform mat4 uMvp;
uniform vec4 uColorMul;
out vec4 vColor;
void main()
{
    vColor = aColor * uColorMul;
    gl_Position = uMvp * vec4(aPos, 1.0);
}
""";

    private const string Frag330 = """
#version 330 core
in vec4 vColor;
out vec4 FragColor;
void main()
{
    FragColor = vColor;
}
""";

    private readonly GL _gl;
    private uint _program;
    private bool _disposed;

    public GlLineShaderProgram(GL gl, bool useOpenGlEs, out string? error)
    {
        _gl = gl;
        error = null;
        _program = 0;
        var vSrc = GlslSourceAdapter.Adapt(Vert330, ShaderType.VertexShader, useOpenGlEs);
        var fSrc = GlslSourceAdapter.Adapt(Frag330, ShaderType.FragmentShader, useOpenGlEs);
        var vs = Compile(ShaderType.VertexShader, vSrc, ref error);
        if (vs == 0)
        {
            return;
        }

        var fs = Compile(ShaderType.FragmentShader, fSrc, ref error);
        if (fs == 0)
        {
            _gl.DeleteShader(vs);
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
        }
    }

    [UsedImplicitly]
    public uint Program => _program;
    public bool IsValid => _program != 0;

    public void Use() => _gl.UseProgram(_program);

    public int GetUniformLocation(string name) => _gl.GetUniformLocation(_program, name);

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
        if (_program != 0)
        {
            _gl.DeleteProgram(_program);
            _program = 0;
        }
    }
}
