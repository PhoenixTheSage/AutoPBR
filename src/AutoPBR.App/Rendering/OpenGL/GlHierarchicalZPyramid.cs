using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Max-depth Hi-Z pyramid built from a sampleable depth texture for GPU occlusion culling.
/// </summary>
internal sealed class GlHierarchicalZPyramid(GL gl) : IDisposable
{
    private const int LocalSize = 8;
    private const uint TextureFetchBarrierBit = 0x00000008;
    private const uint ShaderImageAccessBarrierBit = 0x00000020;

    private uint _hizTexture;
    private int _width;
    private int _height;
    private int _levels;
    private bool _disposed;

    public bool IsValid => _hizTexture != 0 && _levels > 0;
    public int Width => _width;
    public int Height => _height;
    public int Levels => _levels;
    public int MaxLevel => Math.Max(0, _levels - 1);

    public bool EnsureSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var levels = 1;
        var w = width;
        var h = height;
        while (w > 1 || h > 1)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            levels++;
        }

        if (_width == width && _height == height && _levels == levels && IsValid)
        {
            return true;
        }

        DestroyTexture();
        _width = width;
        _height = height;
        _levels = levels;
        _hizTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _hizTexture);
        gl.TexStorage2D(TextureTarget.Texture2D, (uint)levels, SizedInternalFormat.R32f, (uint)width, (uint)height);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.NearestMipmapNearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, levels - 1);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return true;
    }

    public bool Build(GlShaderProgram program, uint depthTexture)
    {
        if (_disposed || !IsValid || !program.IsValid || depthTexture == 0)
        {
            return false;
        }

        program.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, depthTexture);
        SetUniform1(program, "uDepth", 0);

        // Mip 0: copy depth → R32F.
        SetUniform1(program, "uMode", 0);
        SetUniform2(program, "uDstSize", _width, _height);
        gl.BindImageTexture(1, _hizTexture, 0, false, 0, GLEnum.WriteOnly, GLEnum.R32f);
        Dispatch(_width, _height);
        gl.MemoryBarrier(ShaderImageAccessBarrierBit | TextureFetchBarrierBit);

        var srcW = _width;
        var srcH = _height;
        for (var level = 1; level < _levels; level++)
        {
            var dstW = Math.Max(1, srcW / 2);
            var dstH = Math.Max(1, srcH / 2);
            SetUniform1(program, "uMode", 1);
            SetUniform2(program, "uDstSize", dstW, dstH);
            gl.BindImageTexture(0, _hizTexture, level - 1, false, 0, GLEnum.ReadOnly, GLEnum.R32f);
            gl.BindImageTexture(1, _hizTexture, level, false, 0, GLEnum.WriteOnly, GLEnum.R32f);
            Dispatch(dstW, dstH);
            gl.MemoryBarrier(ShaderImageAccessBarrierBit | TextureFetchBarrierBit);
            srcW = dstW;
            srcH = dstH;
        }

        gl.BindImageTexture(0, 0, 0, false, 0, GLEnum.ReadOnly, GLEnum.R32f);
        gl.BindImageTexture(1, 0, 0, false, 0, GLEnum.WriteOnly, GLEnum.R32f);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return true;
    }

    public void Bind(TextureUnit unit)
    {
        gl.ActiveTexture(unit);
        gl.BindTexture(TextureTarget.Texture2D, _hizTexture);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyTexture();
    }

    private void Dispatch(int width, int height)
    {
        gl.DispatchCompute(
            (uint)((width + LocalSize - 1) / LocalSize),
            (uint)((height + LocalSize - 1) / LocalSize),
            1);
    }

    private void DestroyTexture()
    {
        if (_hizTexture != 0)
        {
            gl.DeleteTexture(_hizTexture);
            _hizTexture = 0;
        }

        _width = 0;
        _height = 0;
        _levels = 0;
    }

    private void SetUniform1(GlShaderProgram program, string name, int value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            gl.Uniform1(loc, value);
        }
    }

    private void SetUniform2(GlShaderProgram program, string name, int x, int y)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            gl.Uniform2(loc, x, y);
        }
    }
}
