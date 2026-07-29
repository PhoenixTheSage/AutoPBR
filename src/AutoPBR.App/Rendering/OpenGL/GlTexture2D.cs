using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed class GlTexture2D : IDisposable
{
    private readonly GL _gl;
    private readonly uint _id;
    private readonly bool _mipmapped;
    private bool _disposed;
    private int _cachedWidth;
    private int _cachedHeight;
    private bool _cachedNearest;
    private ulong _cachedFingerprint;
    private bool _hasCache;

    public GlTexture2D(GL gl, bool nearestFilter = true, bool mipmapped = false)
    {
        _gl = gl;
        _mipmapped = mipmapped;
        _id = _gl.GenTexture();
        Bind(0);
        ApplyFilter(nearestFilter);
        UploadRgba(1, 1, [255, 255, 255, 255], nearestFilter);
    }

    public uint Id => _id;

    public void Bind(uint unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + (int)unit);
        _gl.BindTexture(TextureTarget.Texture2D, _id);
    }

    private void ApplyFilter(bool nearestFilter)
    {
        var mag = nearestFilter ? (int)GLEnum.Nearest : (int)GLEnum.Linear;
        var min = _mipmapped
            ? (nearestFilter ? (int)GLEnum.NearestMipmapNearest : (int)GLEnum.LinearMipmapLinear)
            : (nearestFilter ? (int)GLEnum.Nearest : (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, min);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, mag);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
    }

    public void UploadRgba(int width, int height, ReadOnlySpan<byte> rgba, bool nearestFilter = true)
    {
        UploadRgbaIfChanged(width, height, rgba, nearestFilter);
    }

    public bool UploadRgbaIfChanged(int width, int height, ReadOnlySpan<byte> rgba, bool nearestFilter = true)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "RGBA8 texture dimensions must be positive.");
        }

        var expectedLength = checked(width * height * 4);
        if (rgba.Length != expectedLength)
        {
            throw new ArgumentException(
                $"RGBA8 texture payload is {rgba.Length} bytes; expected {expectedLength}.",
                nameof(rgba));
        }

        var fingerprint = GlRgbaFingerprint.Compute(rgba);
        if (_hasCache &&
            _cachedWidth == width &&
            _cachedHeight == height &&
            _cachedNearest == nearestFilter &&
            _cachedFingerprint == fingerprint)
        {
            return false;
        }

        Bind(0);
        if (!_hasCache || _cachedNearest != nearestFilter)
        {
            ApplyFilter(nearestFilter);
        }

        if (_hasCache && _cachedWidth == width && _cachedHeight == height)
        {
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height, PixelFormat.Rgba,
                PixelType.UnsignedByte, rgba);
        }
        else
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, rgba);
        }

        _cachedWidth = width;
        _cachedHeight = height;
        _cachedNearest = nearestFilter;
        _cachedFingerprint = fingerprint;
        _hasCache = true;
        if (_mipmapped)
        {
            _gl.GenerateMipmap(TextureTarget.Texture2D);
        }

        return true;
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
        catch (Exception)
        {
            // Context may already be destroyed during teardown.
        }
    }
}
