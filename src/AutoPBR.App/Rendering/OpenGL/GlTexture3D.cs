using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed class GlTexture3D : IDisposable
{
    private readonly GL _gl;
    private readonly uint _id;
    private bool _disposed;

    public GlTexture3D(GL gl)
    {
        _gl = gl;
        _id = _gl.GenTexture();
        Bind(0);
        _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)GLEnum.Repeat);
    }

    public uint Id => _id;

    public void Bind(uint unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + (int)unit);
        _gl.BindTexture(TextureTarget.Texture3D, _id);
    }

    public void UploadRgba(int size, ReadOnlySpan<byte> rgba)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                "RGBA8 volume size must be positive.");
        }

        var expectedLength = checked(size * size * size * 4);
        if (rgba.Length != expectedLength)
        {
            throw new ArgumentException(
                $"RGBA8 volume payload is {rgba.Length} bytes; expected {expectedLength}.",
                nameof(rgba));
        }

        Bind(0);
        _gl.TexImage3D(TextureTarget.Texture3D, 0, InternalFormat.Rgba8, (uint)size, (uint)size, (uint)size, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        _gl.GenerateMipmap(TextureTarget.Texture3D);
    }

    public void UploadR8(int width, int height, int depth, ReadOnlySpan<byte> r8)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "R8 texture dimensions must all be positive.");
        }

        var expectedLength = checked(width * height * depth);
        if (r8.Length != expectedLength)
        {
            throw new ArgumentException(
                $"R8 texture payload is {r8.Length} bytes; expected {expectedLength}.",
                nameof(r8));
        }

        Bind(0);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureMinFilter,
            (int)GLEnum.Nearest);
        _gl.TexParameter(
            TextureTarget.Texture3D,
            TextureParameterName.TextureMagFilter,
            (int)GLEnum.Nearest);
        _gl.TexImage3D(
            TextureTarget.Texture3D,
            0,
            InternalFormat.R8,
            (uint)width,
            (uint)height,
            (uint)depth,
            0,
            PixelFormat.Red,
            PixelType.UnsignedByte,
            r8);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _gl.DeleteTexture(_id);
        }
        catch
        {
            // Context teardown and failed transactional uploads can make deletion
            // unavailable. The native context owns the remaining object lifetime.
        }
    }
}
