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
        Bind(0);
        _gl.TexImage3D(TextureTarget.Texture3D, 0, InternalFormat.Rgba8, (uint)size, (uint)size, (uint)size, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        _gl.GenerateMipmap(TextureTarget.Texture3D);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteTexture(_id);
    }
}
