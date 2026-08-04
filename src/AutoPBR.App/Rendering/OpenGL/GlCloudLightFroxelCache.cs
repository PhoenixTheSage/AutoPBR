using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CQ3-owned light-aligned cloud resources. These textures are deliberately independent from
/// the camera fog/god-ray froxel targets.
/// </summary>
internal sealed class GlCloudLightFroxelCache : IDisposable
{
    private bool _disposed;

    private GlCloudLightFroxelCache(
        PreviewCloudLightingCacheProfile profile,
        GlCloudLightCascadeTarget near,
        GlCloudLightCascadeTarget far)
    {
        Profile = profile;
        Near = near;
        Far = far;
    }

    public PreviewCloudLightingCacheProfile Profile { get; }
    public GlCloudLightCascadeTarget Near { get; }
    public GlCloudLightCascadeTarget Far { get; }
    public bool IsValid => Near.IsAllocated && Far.IsAllocated;
    public bool IsReferenceReady => Near.IsGenerated && Far.IsGenerated;

    public static bool TryCreate(
        GL gl,
        in PreviewCloudLightingCacheProfile profile,
        out GlCloudLightFroxelCache? cache,
        out string diagnostic)
    {
        cache = null;
        if (!profile.IsEnabled)
        {
            diagnostic = "profile-disabled";
            return false;
        }

        GlCloudLightCascadeTarget? near = null;
        GlCloudLightCascadeTarget? far = null;
        try
        {
            if (!GlCloudLightCascadeTarget.TryCreate(gl, profile.Near, out near, out var nearError))
            {
                diagnostic = "near-" + nearError;
                return false;
            }

            if (!GlCloudLightCascadeTarget.TryCreate(gl, profile.Far, out far, out var farError))
            {
                diagnostic = "far-" + farError;
                return false;
            }

            cache = new GlCloudLightFroxelCache(profile, near!, far!);
            near = null;
            far = null;
            diagnostic =
                $"allocated-cq3/{profile.Format};near={profile.Near.FormatDimensions()};" +
                $"far={profile.Far.FormatDimensions()};prefixScratch=ping-pong-rg32f";
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = $"{ex.GetType().Name}:{ex.Message}";
            return false;
        }
        finally
        {
            near?.Dispose();
            far?.Dispose();
        }
    }

    public string FormatDiagnostic() =>
        $"resources={(IsValid ? "allocated-cq3" : "invalid")};" +
        $"near={Near.FormatDiagnostic()};far={Far.FormatDiagnostic()};" +
        $"referenceReady={(IsReferenceReady ? "ready" : "pending")}";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Near.Dispose();
        Far.Dispose();
    }
}

internal sealed class GlCloudLightCascadeTarget : IDisposable
{
    private readonly GL _gl;
    private uint _arrayTexture;
    private uint _prefixTexture0;
    private uint _prefixTexture1;
    private uint _framebuffer;
    private bool _disposed;

    private GlCloudLightCascadeTarget(GL gl, PreviewCloudLightCascadeProfile profile)
    {
        _gl = gl;
        Profile = profile;
    }

    public PreviewCloudLightCascadeProfile Profile { get; }
    public uint ArrayTextureHandle => _arrayTexture;
    public bool IsAllocated =>
        _arrayTexture != 0 &&
        _prefixTexture0 != 0 &&
        _prefixTexture1 != 0 &&
        _framebuffer != 0;
    public bool IsGenerated { get; private set; }
    public int GenerationId { get; private set; }
    public int DensityIdentity { get; private set; }
    public int LastGenerationFrame { get; private set; } = -1;
    public System.Numerics.Vector3 GenerationWindOffset { get; private set; }
    public PreviewCloudLightCascadeTransform Transform { get; private set; }

    public static bool TryCreate(
        GL gl,
        in PreviewCloudLightCascadeProfile profile,
        out GlCloudLightCascadeTarget? target,
        out string diagnostic)
    {
        target = null;
        if (!profile.IsEnabled)
        {
            diagnostic = "profile-disabled";
            return false;
        }

        var candidate = new GlCloudLightCascadeTarget(gl, profile);
        if (!candidate.TryAllocate(out diagnostic))
        {
            candidate.Dispose();
            return false;
        }

        target = candidate;
        return true;
    }

    public bool TryBindGenerationLayer(
        int layer,
        out uint previousPrefixTexture,
        out string diagnostic)
    {
        previousPrefixTexture = 0;
        if (!IsAllocated || layer < 0 || layer >= Profile.Depth)
        {
            diagnostic = "invalid-layer-or-unallocated";
            return false;
        }

        var destinationPrefix = (layer & 1) == 0 ? _prefixTexture0 : _prefixTexture1;
        previousPrefixTexture = layer == 0
            ? 0
            : ((layer & 1) == 0 ? _prefixTexture1 : _prefixTexture0);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.FramebufferTextureLayer(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            _arrayTexture,
            0,
            layer);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D,
            destinationPrefix,
            0);
        unsafe
        {
            var attachments = stackalloc DrawBufferMode[]
            {
                DrawBufferMode.ColorAttachment0,
                DrawBufferMode.ColorAttachment1,
            };
            _gl.DrawBuffers(2, attachments);
        }

        _gl.Viewport(0, 0, (uint)Profile.Width, (uint)Profile.Height);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            diagnostic = "framebuffer-" + status;
            return false;
        }

        diagnostic = "ready";
        return true;
    }

    public void CommitGeneration(in PreviewCloudLightCascadeTransform transform)
    {
        CommitGeneration(transform, 0, System.Numerics.Vector3.Zero, 0);
    }

    public void CommitGeneration(
        in PreviewCloudLightCascadeTransform transform,
        int generationFrame,
        System.Numerics.Vector3 generationWindOffset,
        int densityIdentity = 0)
    {
        Transform = transform;
        IsGenerated = true;
        LastGenerationFrame = Math.Max(0, generationFrame);
        GenerationWindOffset = generationWindOffset;
        DensityIdentity = densityIdentity;
        GenerationId++;
    }

    public void InvalidateGeneration()
    {
        IsGenerated = false;
    }

    public int AgeAt(int frameIndex) =>
        IsGenerated && LastGenerationFrame >= 0
            ? Math.Max(0, frameIndex - LastGenerationFrame)
            : int.MaxValue;

    public PreviewCloudLightCascadeTransform GetSamplingTransform(
        System.Numerics.Vector3 currentWindOffset,
        float windPeriod)
    {
        if (!IsGenerated)
        {
            return Transform;
        }

        var windDelta = PreviewCloudLightWindReprojection.WrappedDelta(
            currentWindOffset,
            GenerationWindOffset,
            windPeriod);
        return PreviewCloudLightWindReprojection.Apply(Transform, windDelta);
    }

    public bool TryReadLayer(int layer, Span<float> rg, out string diagnostic)
    {
        var required = Profile.Width * Profile.Height * 2;
        if (!IsAllocated ||
            layer < 0 ||
            layer >= Profile.Depth ||
            rg.Length < required)
        {
            diagnostic = "invalid-readback-request";
            return false;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.FramebufferTextureLayer(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            _arrayTexture,
            0,
            layer);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        unsafe
        {
            fixed (float* pointer = rg)
            {
                _gl.ReadPixels(
                    0,
                    0,
                    (uint)Profile.Width,
                    (uint)Profile.Height,
                    PixelFormat.RG,
                    PixelType.Float,
                    pointer);
            }
        }

        var error = _gl.GetError();
        diagnostic = error == GLEnum.NoError ? "readback-ok" : "readback-" + error;
        return error == GLEnum.NoError;
    }

    public string FormatDiagnostic() =>
        $"{Profile.FormatDimensions()}/allocated={IsAllocated}/generated={IsGenerated}/" +
        $"generation={GenerationId}/densityIdentity={DensityIdentity:X8}/" +
        $"lastFrame={LastGenerationFrame}";

    private bool TryAllocate(out string diagnostic)
    {
        FlushErrors();
        try
        {
            _arrayTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2DArray, _arrayTexture);
            unsafe
            {
                _gl.TexImage3D(
                    TextureTarget.Texture2DArray,
                    0,
                    InternalFormat.RG16f,
                    (uint)Profile.Width,
                    (uint)Profile.Height,
                    (uint)Profile.Depth,
                    0,
                    PixelFormat.RG,
                    PixelType.HalfFloat,
                    (void*)0);
            }

            ConfigureArrayTexture();
            _prefixTexture0 = AllocatePrefixTexture();
            _prefixTexture1 = AllocatePrefixTexture();
            _framebuffer = _gl.GenFramebuffer();

            var allocationError = _gl.GetError();
            if (allocationError != GLEnum.NoError)
            {
                diagnostic = "allocation-" + allocationError;
                return false;
            }

            if (!TryClearAndValidate(out diagnostic))
            {
                return false;
            }

            diagnostic = "allocated";
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = $"{ex.GetType().Name}:{ex.Message}";
            return false;
        }
        finally
        {
            _gl.BindTexture(TextureTarget.Texture2DArray, 0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    private uint AllocatePrefixTexture()
    {
        var texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        unsafe
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.RG32f,
                (uint)Profile.Width,
                (uint)Profile.Height,
                0,
                PixelFormat.RG,
                PixelType.Float,
                (void*)0);
        }

        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)GLEnum.Nearest);
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)GLEnum.Nearest);
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int)GLEnum.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int)GLEnum.ClampToEdge);
        return texture;
    }

    private void ConfigureArrayTexture()
    {
        _gl.TexParameter(
            TextureTarget.Texture2DArray,
            TextureParameterName.TextureMinFilter,
            (int)GLEnum.Linear);
        _gl.TexParameter(
            TextureTarget.Texture2DArray,
            TextureParameterName.TextureMagFilter,
            (int)GLEnum.Linear);
        _gl.TexParameter(
            TextureTarget.Texture2DArray,
            TextureParameterName.TextureWrapS,
            (int)GLEnum.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.Texture2DArray,
            TextureParameterName.TextureWrapT,
            (int)GLEnum.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.Texture2DArray,
            TextureParameterName.TextureWrapR,
            (int)GLEnum.ClampToEdge);
    }

    private bool TryClearAndValidate(out string diagnostic)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _gl.ClearColor(0f, 1f, 0f, 0f);
        for (var layer = 0; layer < Profile.Depth; layer++)
        {
            _gl.FramebufferTextureLayer(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                _arrayTexture,
                0,
                layer);
            var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
            {
                diagnostic = $"array-layer-{layer}-{status}";
                return false;
            }

            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        foreach (var texture in new[] { _prefixTexture0, _prefixTexture1 })
        {
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texture,
                0);
            var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
            {
                diagnostic = "prefix-" + status;
                return false;
            }

            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        var error = _gl.GetError();
        diagnostic = error == GLEnum.NoError ? "clear-ok" : "clear-" + error;
        return error == GLEnum.NoError;
    }

    private void FlushErrors()
    {
        for (var index = 0; index < 16 && _gl.GetError() != GLEnum.NoError; index++)
        {
        }
    }

    private void DestroyGpuResources()
    {
        if (_framebuffer != 0)
        {
            _gl.DeleteFramebuffer(_framebuffer);
            _framebuffer = 0;
        }

        if (_arrayTexture != 0)
        {
            _gl.DeleteTexture(_arrayTexture);
            _arrayTexture = 0;
        }

        if (_prefixTexture0 != 0)
        {
            _gl.DeleteTexture(_prefixTexture0);
            _prefixTexture0 = 0;
        }

        if (_prefixTexture1 != 0)
        {
            _gl.DeleteTexture(_prefixTexture1);
            _prefixTexture1 = 0;
        }

        IsGenerated = false;
        DensityIdentity = 0;
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
            DestroyGpuResources();
        }
        catch (Exception)
        {
            // Context may already be gone during preview teardown.
        }
    }
}
