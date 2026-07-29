using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;
using System.Numerics;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CQ3.5 transactional R16F publication target. The inactive texture is rendered first and
/// becomes visible to terrain/fog consumers only after a complete, error-free draw.
/// </summary>
internal sealed class GlCloudGroundTransmittanceTarget : IDisposable
{
    private readonly GL _gl;
    private readonly uint[] _textures = new uint[2];
    private uint _framebuffer;
    private int _publishedIndex;
    private int _writeIndex;
    private bool _disposed;

    private GlCloudGroundTransmittanceTarget(
        GL gl,
        in PreviewCloudGroundTransmittanceProfile profile)
    {
        _gl = gl;
        Profile = profile;
    }

    public PreviewCloudGroundTransmittanceProfile Profile { get; }
    public bool IsAllocated =>
        _textures[0] != 0 &&
        _textures[1] != 0 &&
        _framebuffer != 0;
    public bool IsPublished { get; private set; }
    public int GenerationId { get; private set; }
    public int SourceNearGenerationId { get; private set; }
    public int SourceFarGenerationId { get; private set; }
    public uint TextureHandle =>
        IsPublished ? _textures[_publishedIndex] : 0;
    public PreviewCloudLightCascadeTransform Transform { get; private set; }
    public Vector3 PublishedWindOffset { get; private set; }

    public static bool TryCreate(
        GL gl,
        in PreviewCloudGroundTransmittanceProfile profile,
        out GlCloudGroundTransmittanceTarget? target,
        out string diagnostic)
    {
        target = null;
        if (!profile.IsEnabled)
        {
            diagnostic = "profile-disabled";
            return false;
        }

        var candidate = new GlCloudGroundTransmittanceTarget(gl, profile);
        if (!candidate.TryAllocate(out diagnostic))
        {
            candidate.Dispose();
            return false;
        }

        target = candidate;
        return true;
    }

    public bool TryBindWrite(out string diagnostic)
    {
        if (!IsAllocated)
        {
            diagnostic = "unallocated";
            return false;
        }

        _writeIndex = IsPublished ? 1 - _publishedIndex : 0;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _textures[_writeIndex],
            0);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _gl.Viewport(0, 0, (uint)Profile.Width, (uint)Profile.Height);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        diagnostic = status == GLEnum.FramebufferComplete
            ? "ready"
            : "framebuffer-" + status;
        return status == GLEnum.FramebufferComplete;
    }

    public void Commit(
        in PreviewCloudLightCascadeTransform transform,
        int sourceNearGenerationId,
        int sourceFarGenerationId)
    {
        Commit(
            transform,
            Vector3.Zero,
            sourceNearGenerationId,
            sourceFarGenerationId);
    }

    public void Commit(
        in PreviewCloudLightCascadeTransform transform,
        Vector3 publishedWindOffset,
        int sourceNearGenerationId,
        int sourceFarGenerationId)
    {
        Transform = transform;
        PublishedWindOffset = publishedWindOffset;
        SourceNearGenerationId = sourceNearGenerationId;
        SourceFarGenerationId = sourceFarGenerationId;
        _publishedIndex = _writeIndex;
        IsPublished = true;
        GenerationId++;
    }

    public PreviewCloudLightCascadeTransform GetSamplingTransform(
        Vector3 currentWindOffset,
        float windPeriod)
    {
        var windDelta = PreviewCloudLightWindReprojection.WrappedDelta(
            currentWindOffset,
            PublishedWindOffset,
            windPeriod);
        return PreviewCloudLightWindReprojection.Apply(Transform, windDelta);
    }

    public bool IsCurrent(GlCloudLightFroxelCache? cache)
    {
        if (!IsPublished ||
            cache is not { IsValid: true } ||
            !cache.Far.IsGenerated ||
            SourceFarGenerationId != cache.Far.GenerationId)
        {
            return false;
        }

        // Publication is intentionally tied to the lower-rate far refresh.
        // Cinematic may update the near cache between publications; retain the
        // last complete field until the far source changes instead of dropping
        // terrain/fog cloud shadowing for those intervening frames.
        return true;
    }

    public bool TryRead(Span<float> values, out string diagnostic)
    {
        var required = Profile.Width * Profile.Height;
        if (!IsPublished || values.Length < required)
        {
            diagnostic = "invalid-readback-request";
            return false;
        }

        var priorRead = _gl.GetInteger(GetPName.ReadFramebufferBinding);
        var priorDraw = _gl.GetInteger(GetPName.DrawFramebufferBinding);
        try
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                TextureHandle,
                0);
            _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            unsafe
            {
                fixed (float* pointer = values)
                {
                    _gl.ReadPixels(
                        0,
                        0,
                        (uint)Profile.Width,
                        (uint)Profile.Height,
                        PixelFormat.Red,
                        PixelType.Float,
                        pointer);
                }
            }

            var error = _gl.GetError();
            diagnostic = error == GLEnum.NoError
                ? "readback-ok"
                : "readback-" + error;
            return error == GLEnum.NoError;
        }
        finally
        {
            _gl.BindFramebuffer(
                FramebufferTarget.ReadFramebuffer,
                (uint)Math.Max(0, priorRead));
            _gl.BindFramebuffer(
                FramebufferTarget.DrawFramebuffer,
                (uint)Math.Max(0, priorDraw));
        }
    }

    public string FormatDiagnostic() =>
        $"groundTransmittance={(IsPublished ? "published" : "pending")}/" +
        $"{Profile.FormatDiagnostic()}/generation={GenerationId}";

    private bool TryAllocate(out string diagnostic)
    {
        var priorReadFramebuffer = _gl.GetInteger(GetPName.ReadFramebufferBinding);
        var priorDrawFramebuffer = _gl.GetInteger(GetPName.DrawFramebufferBinding);
        var priorActiveTexture = _gl.GetInteger(GetPName.ActiveTexture);
        var priorTexture2D = _gl.GetInteger(GetPName.TextureBinding2D);
        var priorClearColor = new float[4];
        _gl.GetFloat(GetPName.ColorClearValue, priorClearColor);
        var priorColorMask = new bool[4];
        _gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);
        FlushErrors();
        try
        {
            for (var index = 0; index < _textures.Length; index++)
            {
                _textures[index] = _gl.GenTexture();
                _gl.BindTexture(TextureTarget.Texture2D, _textures[index]);
                unsafe
                {
                    _gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.R16f,
                        (uint)Profile.Width,
                        (uint)Profile.Height,
                        0,
                        PixelFormat.Red,
                        PixelType.HalfFloat,
                        (void*)0);
                }

                _gl.TexParameter(
                    TextureTarget.Texture2D,
                    TextureParameterName.TextureMinFilter,
                    (int)GLEnum.Linear);
                _gl.TexParameter(
                    TextureTarget.Texture2D,
                    TextureParameterName.TextureMagFilter,
                    (int)GLEnum.Linear);
                _gl.TexParameter(
                    TextureTarget.Texture2D,
                    TextureParameterName.TextureWrapS,
                    (int)GLEnum.ClampToEdge);
                _gl.TexParameter(
                    TextureTarget.Texture2D,
                    TextureParameterName.TextureWrapT,
                    (int)GLEnum.ClampToEdge);
            }

            _framebuffer = _gl.GenFramebuffer();
            for (var index = 0; index < _textures.Length; index++)
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
                _gl.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D,
                    _textures[index],
                    0);
                _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
                var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (status != GLEnum.FramebufferComplete)
                {
                    diagnostic = $"texture-{index}-framebuffer-{status}";
                    return false;
                }

                _gl.ColorMask(true, true, true, true);
                _gl.ClearColor(1f, 1f, 1f, 1f);
                _gl.Clear(ClearBufferMask.ColorBufferBit);
            }

            var error = _gl.GetError();
            diagnostic = error == GLEnum.NoError
                ? "allocated-r16f-ping-pong"
                : "allocation-" + error;
            return error == GLEnum.NoError;
        }
        catch (Exception ex)
        {
            diagnostic = $"{ex.GetType().Name}:{ex.Message}";
            return false;
        }
        finally
        {
            _gl.BindTexture(
                TextureTarget.Texture2D,
                (uint)Math.Max(0, priorTexture2D));
            _gl.ActiveTexture((TextureUnit)priorActiveTexture);
            _gl.BindFramebuffer(
                FramebufferTarget.ReadFramebuffer,
                (uint)Math.Max(0, priorReadFramebuffer));
            _gl.BindFramebuffer(
                FramebufferTarget.DrawFramebuffer,
                (uint)Math.Max(0, priorDrawFramebuffer));
            _gl.ClearColor(
                priorClearColor[0],
                priorClearColor[1],
                priorClearColor[2],
                priorClearColor[3]);
            _gl.ColorMask(
                priorColorMask[0],
                priorColorMask[1],
                priorColorMask[2],
                priorColorMask[3]);
        }
    }

    private void FlushErrors()
    {
        for (var index = 0;
             index < 16 && _gl.GetError() != GLEnum.NoError;
             index++)
        {
        }
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
            if (_framebuffer != 0)
            {
                _gl.DeleteFramebuffer(_framebuffer);
                _framebuffer = 0;
            }

            for (var index = 0; index < _textures.Length; index++)
            {
                if (_textures[index] == 0)
                {
                    continue;
                }

                _gl.DeleteTexture(_textures[index]);
                _textures[index] = 0;
            }
        }
        catch (Exception)
        {
            // Context may already be gone during preview teardown.
        }

        IsPublished = false;
    }
}
