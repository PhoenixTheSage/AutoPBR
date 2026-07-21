using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private uint _hdr2DTexture;
    private int _hdr2DWidth;
    private int _hdr2DHeight;
    private bool _hdr2DHasImage;
    private bool _preferHdr2DPresent;

    /// <summary>When true and HDR is active, present the 2D composite instead of the 3D scene.</summary>
    internal void SetPreferHdr2DPresent(bool prefer) => _preferHdr2DPresent = prefer;

    internal void SetHdr2DCompositeRgba(byte[]? rgba, int width, int height)
    {
        if (rgba is null || width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            _hdr2DHasImage = false;
            return;
        }

        _hdr2DPendingRgba = rgba;
        _hdr2DPendingWidth = width;
        _hdr2DPendingHeight = height;
        _hdr2DUploadPending = true;
        _hdr2DHasImage = true;
    }

    private byte[]? _hdr2DPendingRgba;
    private int _hdr2DPendingWidth;
    private int _hdr2DPendingHeight;
    private bool _hdr2DUploadPending;

    private bool TryPresentHdr2DComposite(ref GlRenderFrame frame)
    {
        if (!_preferHdr2DPresent || !_hdr2DHasImage || !frame.Settings.HdrPresentActive)
        {
            return false;
        }

        if (_scenePresentProgram is not { IsValid: true } || _godRayQuadVao == 0 || _gl is null)
        {
            return false;
        }

        EnsureHdr2DTextureUploaded(frame.Gl);
        if (_hdr2DTexture == 0)
        {
            return false;
        }

        BindDefaultFramebuffer(ref frame);
        var gl = frame.Gl;
        var priorDepthTest = gl.IsEnabled(EnableCap.DepthTest);
        var priorBlend = gl.IsEnabled(EnableCap.Blend);
        var priorCullFace = gl.IsEnabled(EnableCap.CullFace);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.ScissorTest);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.BindVertexArray(_godRayQuadVao);
        _scenePresentProgram.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _hdr2DTexture);
        BindScenePresentUniforms(frame.Settings, sceneIsLinear: false);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.BindVertexArray(0);
        if (priorDepthTest)
        {
            gl.Enable(EnableCap.DepthTest);
        }

        if (priorBlend)
        {
            gl.Enable(EnableCap.Blend);
        }

        if (priorCullFace)
        {
            gl.Enable(EnableCap.CullFace);
        }

        return true;
    }

    private void EnsureHdr2DTextureUploaded(GL gl)
    {
        if (!_hdr2DUploadPending || _hdr2DPendingRgba is null)
        {
            return;
        }

        if (_hdr2DTexture == 0)
        {
            _hdr2DTexture = gl.GenTexture();
        }

        gl.BindTexture(TextureTarget.Texture2D, _hdr2DTexture);
        unsafe
        {
            fixed (byte* ptr = _hdr2DPendingRgba)
            {
                if (_hdr2DWidth != _hdr2DPendingWidth || _hdr2DHeight != _hdr2DPendingHeight)
                {
                    gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.Rgba8,
                        (uint)_hdr2DPendingWidth,
                        (uint)_hdr2DPendingHeight,
                        0,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        ptr);
                    _hdr2DWidth = _hdr2DPendingWidth;
                    _hdr2DHeight = _hdr2DPendingHeight;
                }
                else
                {
                    gl.TexSubImage2D(
                        TextureTarget.Texture2D,
                        0,
                        0,
                        0,
                        (uint)_hdr2DPendingWidth,
                        (uint)_hdr2DPendingHeight,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        ptr);
                }
            }
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        _hdr2DUploadPending = false;
    }

    private void DestroyHdr2DResources(GL? gl)
    {
        if (gl is not null && _hdr2DTexture != 0)
        {
            gl.DeleteTexture(_hdr2DTexture);
        }

        _hdr2DTexture = 0;
        _hdr2DWidth = 0;
        _hdr2DHeight = 0;
        _hdr2DHasImage = false;
        _hdr2DUploadPending = false;
        _hdr2DPendingRgba = null;
    }
}
