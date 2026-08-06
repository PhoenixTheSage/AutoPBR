using System.Collections.Generic;
using System.Text;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Central shader compile path: prepared-source cache, disk binary cache, parallel compile.</summary>
internal sealed class GlShaderCompileContext
{
    private readonly GL _gl;
    private readonly bool _useOpenGlEs;
    private readonly string _cacheIdentity;
    private readonly GlProgramBinaryCache _binaryCache;
    private readonly GlParallelShaderCompile _parallelCompile;

    public GlShaderCompileContext(GL gl, bool useOpenGlEs, string vendor, string renderer)
    {
        _gl = gl;
        _useOpenGlEs = useOpenGlEs;
        _cacheIdentity = $"{vendor}|{renderer}|{(useOpenGlEs ? "es" : "gl")}";
        _binaryCache = new GlProgramBinaryCache(_cacheIdentity);
        _parallelCompile = new GlParallelShaderCompile(gl);
    }

    /// <summary>Cap KHR parallel shader compiler threads when the extension is present.</summary>
    public bool LimitCompilerThreads(uint threads) => _parallelCompile.LimitCompilerThreads(threads);

    public GlShaderProgram CreateProgram(string vertexFile, string fragmentFile, out string? error,
        string? debugLabel = null, IReadOnlyDictionary<string, int>? defines = null)
    {
        return CreateProgram(vertexFile, tessControlFile: null, tessEvaluationFile: null, fragmentFile, out error, debugLabel, defines);
    }

    public GlShaderProgram CreateComputeProgram(
        string computeFile,
        out string? error,
        string? debugLabel = null,
        IReadOnlyDictionary<string, int>? defines = null)
    {
        error = null;
        var label = string.IsNullOrWhiteSpace(debugLabel) ? computeFile : debugLabel;
        if (_useOpenGlEs)
        {
            error = "Compute shaders require the desktop OpenGL preview path.";
            return new GlShaderProgram(_gl, 0);
        }

        var stages = new[] { (computeFile, ShaderType.ComputeShader) };
        var programKey = GlslPreparedSourceCache.ComputeProgramKey(_useOpenGlEs, _cacheIdentity, stages, defines);
        if (_binaryCache.TryLoad(programKey, out var binaryFormat, out var binaryBytes) &&
            TryLinkFromBinary(binaryBytes, binaryFormat, label, ref error, out var fromCache))
        {
            return fromCache;
        }

        string cSrc;
        try
        {
            cSrc = GlslPreparedSourceCache.GetOrPrepare(computeFile, ShaderType.ComputeShader, _useOpenGlEs, defines);
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            return new GlShaderProgram(_gl, 0);
        }

        var cs = CompileShader(ShaderType.ComputeShader, cSrc, label, ref error);
        if (cs == 0)
        {
            return new GlShaderProgram(_gl, 0);
        }

        var program = _gl.CreateProgram();
        _gl.AttachShader(program, cs);
        _gl.LinkProgram(program);
        GlShaderCompileYield.AfterCompile();
        _gl.GetProgram(program, GLEnum.LinkStatus, out var ok);
        _gl.DeleteShader(cs);
        if (ok == 0)
        {
            var linkLog = _gl.GetProgramInfoLog(program);
            error = string.IsNullOrEmpty(error) ? linkLog : error + "\n" + linkLog;
            _gl.DeleteProgram(program);
            return new GlShaderProgram(_gl, 0);
        }

        TryStoreProgramBinary(program, programKey);
        return new GlShaderProgram(_gl, program);
    }

    public GlShaderProgram CreateProgram(
        string vertexFile,
        string? tessControlFile,
        string? tessEvaluationFile,
        string fragmentFile,
        out string? error,
        string? debugLabel = null,
        IReadOnlyDictionary<string, int>? defines = null)
    {
        error = null;
        var label = string.IsNullOrWhiteSpace(debugLabel) ? fragmentFile : debugLabel;
        var hasTessellation = !string.IsNullOrWhiteSpace(tessControlFile) && !string.IsNullOrWhiteSpace(tessEvaluationFile);
        if (hasTessellation && _useOpenGlEs)
        {
            error = "Tessellation shaders require the desktop OpenGL preview path.";
            return new GlShaderProgram(_gl, 0);
        }

        var stages = hasTessellation
            ? new[]
            {
                (vertexFile, ShaderType.VertexShader),
                (tessControlFile!, ShaderType.TessControlShader),
                (tessEvaluationFile!, ShaderType.TessEvaluationShader),
                (fragmentFile, ShaderType.FragmentShader)
            }
            : new[]
            {
                (vertexFile, ShaderType.VertexShader),
                (fragmentFile, ShaderType.FragmentShader)
            };
        var programKey = GlslPreparedSourceCache.ComputeProgramKey(_useOpenGlEs, _cacheIdentity, stages, defines);

        if (_binaryCache.TryLoad(programKey, out var binaryFormat, out var binaryBytes) &&
            TryLinkFromBinary(binaryBytes, binaryFormat, label, ref error, out var fromCache))
        {
            return fromCache;
        }

        string vSrc;
        string? tcSrc = null;
        string? teSrc = null;
        string fSrc;
        try
        {
            vSrc = GlslPreparedSourceCache.GetOrPrepare(vertexFile, ShaderType.VertexShader, _useOpenGlEs, defines);
            if (hasTessellation)
            {
                tcSrc = GlslPreparedSourceCache.GetOrPrepare(tessControlFile!, ShaderType.TessControlShader, _useOpenGlEs, defines);
                teSrc = GlslPreparedSourceCache.GetOrPrepare(tessEvaluationFile!, ShaderType.TessEvaluationShader, _useOpenGlEs, defines);
            }

            fSrc = GlslPreparedSourceCache.GetOrPrepare(fragmentFile, ShaderType.FragmentShader, _useOpenGlEs, defines);
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            return new GlShaderProgram(_gl, 0);
        }

        var vs = CompileShader(ShaderType.VertexShader, vSrc, label, ref error);
        if (vs == 0)
        {
            return new GlShaderProgram(_gl, 0);
        }

        var tcs = 0u;
        var tes = 0u;
        if (hasTessellation)
        {
            tcs = CompileShader(ShaderType.TessControlShader, tcSrc!, label, ref error);
            if (tcs == 0)
            {
                _gl.DeleteShader(vs);
                return new GlShaderProgram(_gl, 0);
            }

            tes = CompileShader(ShaderType.TessEvaluationShader, teSrc!, label, ref error);
            if (tes == 0)
            {
                _gl.DeleteShader(vs);
                _gl.DeleteShader(tcs);
                return new GlShaderProgram(_gl, 0);
            }
        }

        var fs = CompileShader(ShaderType.FragmentShader, fSrc, label, ref error);
        if (fs == 0)
        {
            _gl.DeleteShader(vs);
            if (tcs != 0)
            {
                _gl.DeleteShader(tcs);
            }

            if (tes != 0)
            {
                _gl.DeleteShader(tes);
            }

            return new GlShaderProgram(_gl, 0);
        }

        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        if (hasTessellation)
        {
            _gl.AttachShader(program, tcs);
            _gl.AttachShader(program, tes);
        }

        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        GlShaderCompileYield.AfterCompile();
        _gl.GetProgram(program, GLEnum.LinkStatus, out var ok);
        _gl.DeleteShader(vs);
        if (tcs != 0)
        {
            _gl.DeleteShader(tcs);
        }

        if (tes != 0)
        {
            _gl.DeleteShader(tes);
        }

        _gl.DeleteShader(fs);
        if (ok == 0)
        {
            var linkLog = _gl.GetProgramInfoLog(program);
            error = string.IsNullOrEmpty(error) ? linkLog : error + "\n" + linkLog;
            _gl.DeleteProgram(program);
            return new GlShaderProgram(_gl, 0);
        }

        TryStoreProgramBinary(program, programKey);
        return new GlShaderProgram(_gl, program);
    }

    private bool TryLinkFromBinary(byte[] binary, uint format, string label, ref string? error,
        out GlShaderProgram program)
    {
        program = new GlShaderProgram(_gl, 0);
        var handle = _gl.CreateProgram();
        unsafe
        {
            fixed (byte* p = binary)
            {
                _gl.ProgramBinary(handle, (GLEnum)format, p, (uint)binary.Length);
            }
        }

        _gl.GetProgram(handle, GLEnum.LinkStatus, out var ok);
        if (ok == 0)
        {
            var linkLog = _gl.GetProgramInfoLog(handle);
            error = $"ProgramBinary reload failed ({label}): {linkLog}";
            _gl.DeleteProgram(handle);
            return false;
        }

        program = new GlShaderProgram(_gl, handle);
        return true;
    }

    private void TryStoreProgramBinary(uint program, string programKey)
    {
        _gl.GetProgram(program, GLEnum.ProgramBinaryLength, out var length);
        if (length <= 0)
        {
            return;
        }

        unsafe
        {
            Span<byte> buffer = length <= 8192 ? stackalloc byte[length] : new byte[length];
            fixed (byte* p = buffer)
            {
                _gl.GetProgramBinary(program, (uint)length, out var written, out var format, p);
                if (written > 0)
                {
                    _binaryCache.TryStore(programKey, (uint)format, buffer[..(int)written]);
                }
            }
        }
    }

    private uint CompileShader(ShaderType type, string source, string debugLabel, ref string? error)
    {
        var s = _gl.CreateShader(type);
        _gl.ShaderSource(s, source);
        _gl.CompileShader(s);
        _parallelCompile.WaitForShader(s);
        GlShaderCompileYield.AfterCompile();
        _gl.GetShader(s, GLEnum.CompileStatus, out var ok);
        if (ok == 0)
        {
            var info = _gl.GetShaderInfoLog(s);
            var sb = new StringBuilder();
            sb.Append(type).Append(" compile failed (").Append(debugLabel).Append("): ").AppendLine(info);
            GlShaderProgram.AppendShaderContext(sb, source, info);
            GlShaderProgram.AppendShaderFingerprint(sb, source);
            GlShaderProgram.AppendShaderDumpHint(sb, type, debugLabel, source);
            error = string.IsNullOrEmpty(error) ? sb.ToString() : error + "\n" + sb;
            _gl.DeleteShader(s);
            return 0;
        }

        return s;
    }
}
