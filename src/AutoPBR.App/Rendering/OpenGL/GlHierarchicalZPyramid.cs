using System.Numerics;

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
    private const uint ShaderStorageBarrierBit = 0x00002000;
    private const uint BufferUpdateBarrierBit = 0x00000200;

    private uint _hizTexture;
    private int _width;
    private int _height;
    private int _levels;
    private uint _sphereBuffer;
    private uint _visibilityBuffer;
    private int _sphereCapacityBytes;
    private int _visibilityCapacityBytes;
    private float[] _sphereScratch = [];
    private uint[] _visibilityScratch = [];
    private bool _disposed;

    public uint TextureHandle => _hizTexture;
    public int Width => _width;
    public int Height => _height;
    public int Levels => _levels;
    public int MaxLevel => Math.Max(0, _levels - 1);
    public bool IsValid => _hizTexture != 0 && _levels > 0;

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

    /// <summary>
    /// GPU-tests spheres against the Hi-Z pyramid and readbacks visibility (1=visible, 0=occluded).
    /// </summary>
    public bool TestSpheres(
        GlShaderProgram program,
        ReadOnlySpan<Vector4> centerRadius,
        Matrix4x4 viewProj,
        Span<uint> visibilityOut)
    {
        if (_disposed ||
            !IsValid ||
            !program.IsValid ||
            centerRadius.Length == 0 ||
            visibilityOut.Length < centerRadius.Length)
        {
            return false;
        }

        var count = centerRadius.Length;
        if (_sphereScratch.Length < count * 4)
        {
            _sphereScratch = new float[Math.Max(count * 4, 64)];
        }

        for (var i = 0; i < count; i++)
        {
            var s = centerRadius[i];
            _sphereScratch[i * 4] = s.X;
            _sphereScratch[i * 4 + 1] = s.Y;
            _sphereScratch[i * 4 + 2] = s.Z;
            _sphereScratch[i * 4 + 3] = s.W;
        }

        EnsureBuffer(ref _sphereBuffer, ref _sphereCapacityBytes, count * 4 * sizeof(float));
        EnsureBuffer(ref _visibilityBuffer, ref _visibilityCapacityBytes, count * sizeof(uint));
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _sphereBuffer);
        gl.BufferSubData<float>(BufferTargetARB.ShaderStorageBuffer, 0, _sphereScratch.AsSpan(0, count * 4));
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _sphereBuffer);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, _visibilityBuffer);

        program.Use();
        Bind(TextureUnit.Texture0);
        SetUniform1(program, "uHiZ", 0);
        SetUniformMatrix(program, "uViewProj", viewProj);
        SetUniform2(program, "uHiZSize", _width, _height);
        SetUniform1(program, "uHiZMaxLevel", MaxLevel);
        SetUniform1(program, "uSphereCount", (uint)count);
        SetUniform1(program, "uDepthEpsilon", PreviewHierarchicalZMath.DepthEpsilon);
        gl.DispatchCompute((uint)((count + 63) / 64), 1, 1);
        gl.MemoryBarrier(ShaderStorageBarrierBit | BufferUpdateBarrierBit);

        if (_visibilityScratch.Length < count)
        {
            _visibilityScratch = new uint[Math.Max(count, 64)];
        }

        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _visibilityBuffer);
        gl.GetBufferSubData<uint>(BufferTargetARB.ShaderStorageBuffer, 0, _visibilityScratch.AsSpan(0, count));
        _visibilityScratch.AsSpan(0, count).CopyTo(visibilityOut);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, 0);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, 0);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyTexture();
        if (_sphereBuffer != 0)
        {
            gl.DeleteBuffer(_sphereBuffer);
            _sphereBuffer = 0;
        }

        if (_visibilityBuffer != 0)
        {
            gl.DeleteBuffer(_visibilityBuffer);
            _visibilityBuffer = 0;
        }
    }

    private void Dispatch(int width, int height)
    {
        gl.DispatchCompute(
            (uint)((width + LocalSize - 1) / LocalSize),
            (uint)((height + LocalSize - 1) / LocalSize),
            1);
    }

    private void EnsureBuffer(ref uint buffer, ref int capacityBytes, int requiredBytes)
    {
        buffer = buffer == 0 ? gl.GenBuffer() : buffer;
        if (requiredBytes <= capacityBytes)
        {
            return;
        }

        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        unsafe
        {
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)requiredBytes, null, BufferUsageARB.DynamicDraw);
        }

        capacityBytes = requiredBytes;
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
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

    private void SetUniform1(GlShaderProgram program, string name, uint value)
    {
        var loc = program.GetUniformLocation(name);
        if (loc >= 0)
        {
            gl.Uniform1(loc, value);
        }
    }

    private void SetUniform1(GlShaderProgram program, string name, float value)
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

    private void SetUniformMatrix(GlShaderProgram program, string name, Matrix4x4 matrix)
    {
        var loc = program.GetUniformLocation(name);
        if (loc < 0)
        {
            return;
        }

        // Match OpenGlPreviewBackend matrix upload: row-stored Numerics → transpose → column-major GLSL.
        var mt = Matrix4x4.Transpose(matrix);
        gl.UniformMatrix4(loc, 1, false, in mt.M11);
    }
}
