using System.Numerics;

using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// CQ3.2 desktop GL 4.3 compute/image-store generator. Each 4x4 XY workgroup uses 24 local
/// Z lanes per column to build an ordered sun-to-world prefix, preserving optical depth
/// without an inter-dispatch dependency between logical slices.
/// </summary>
internal sealed class GlCloudLightComputeGenerator
{
    private const int LocalSize = 4;
    private const int MaximumSlices = 24;
    private const uint TextureFetchBarrierBit = 0x00000008;
    private const uint ShaderImageAccessBarrierBit = 0x00000020;

    private readonly GL _gl;
    private readonly GlShaderProgram _program;
    private readonly Uniforms _uniforms;

    public GlCloudLightComputeGenerator(GL gl, GlShaderProgram program)
    {
        _gl = gl;
        _program = program;
        _uniforms = Uniforms.Resolve(program);
    }

    public bool TryGenerate(
        GlCloudLightFroxelCache cache,
        in GlCloudLightSliceGenerationInputs inputs,
        out string diagnostic)
    {
        return TryGenerate(
            cache,
            inputs,
            PreviewCloudLightCascadeSelection.Both,
            0,
            out diagnostic);
    }

    public bool TryGenerate(
        GlCloudLightFroxelCache cache,
        in GlCloudLightSliceGenerationInputs inputs,
        PreviewCloudLightCascadeSelection cascades,
        int generationFrame,
        out string diagnostic)
    {
        if (!_program.IsValid ||
            !cache.IsValid ||
            cascades == PreviewCloudLightCascadeSelection.None)
        {
            diagnostic = "program-cache-or-selection-invalid";
            return false;
        }

        if (((cascades & PreviewCloudLightCascadeSelection.Near) != 0 &&
             cache.Near.Profile.Depth > MaximumSlices) ||
            ((cascades & PreviewCloudLightCascadeSelection.Far) != 0 &&
             cache.Far.Profile.Depth > MaximumSlices))
        {
            diagnostic = $"profile-depth-exceeds-compute-bound-{MaximumSlices}";
            return false;
        }

        try
        {
            _program.Use();
            BindSharedInputs(inputs);

            if ((cascades & PreviewCloudLightCascadeSelection.Near) != 0 &&
                !TryGenerateCascade(
                    cache.Near,
                    inputs.NearTransform,
                    inputs.WindOffset,
                    generationFrame,
                    inputs.ReferenceDensity,
                    inputs.DensityIdentity,
                    out var nearDiagnostic))
            {
                cache.Near.InvalidateGeneration();
                diagnostic = "near-" + nearDiagnostic;
                return false;
            }

            if ((cascades & PreviewCloudLightCascadeSelection.Far) != 0 &&
                !TryGenerateCascade(
                    cache.Far,
                    inputs.FarTransform,
                    inputs.WindOffset,
                    generationFrame,
                    inputs.ReferenceDensity,
                    inputs.DensityIdentity,
                    out var farDiagnostic))
            {
                cache.Far.InvalidateGeneration();
                diagnostic = "far-" + farDiagnostic;
                return false;
            }

            diagnostic =
                $"generated-cq3.6-compute;cascades={cascades};" +
                $"nearGeneration={cache.Near.GenerationId};" +
                $"farGeneration={cache.Far.GenerationId};" +
                $"fixture={(inputs.ReferenceDensity >= 0f ? "fixed-density" : "cq2-density")};" +
                "barrier=image-access+texture-fetch";
            return true;
        }
        catch (Exception ex)
        {
            InvalidateSelected(cache, cascades);
            diagnostic = $"{ex.GetType().Name}:{ex.Message}";
            return false;
        }
        finally
        {
            _gl.BindImageTexture(
                0,
                0,
                0,
                false,
                0,
                GLEnum.WriteOnly,
                GLEnum.RG16f);
        }
    }

    private bool TryGenerateCascade(
        GlCloudLightCascadeTarget target,
        in PreviewCloudLightCascadeTransform transform,
        Vector3 generationWindOffset,
        int generationFrame,
        float referenceDensity,
        int densityIdentity,
        out string diagnostic)
    {
        BindTransform(transform, referenceDensity);
        SetInt3(
            _uniforms.CacheSize,
            target.Profile.Width,
            target.Profile.Height,
            target.Profile.Depth);
        FlushErrors();
        _gl.BindImageTexture(
            0,
            target.ArrayTextureHandle,
            0,
            true,
            0,
            GLEnum.WriteOnly,
            GLEnum.RG16f);
        var bindError = _gl.GetError();
        if (bindError != GLEnum.NoError)
        {
            diagnostic = "image-bind-" + bindError;
            return false;
        }

        _gl.DispatchCompute(
            (uint)((target.Profile.Width + LocalSize - 1) / LocalSize),
            (uint)((target.Profile.Height + LocalSize - 1) / LocalSize),
            1);
        var dispatchError = _gl.GetError();
        if (dispatchError != GLEnum.NoError)
        {
            diagnostic = "dispatch-" + dispatchError;
            return false;
        }

        _gl.MemoryBarrier(ShaderImageAccessBarrierBit | TextureFetchBarrierBit);
        var barrierError = _gl.GetError();
        if (barrierError != GLEnum.NoError)
        {
            diagnostic = "barrier-" + barrierError;
            return false;
        }

        target.CommitGeneration(
            transform,
            generationFrame,
            generationWindOffset,
            densityIdentity);
        diagnostic = "generated";
        return true;
    }

    private static void InvalidateSelected(
        GlCloudLightFroxelCache cache,
        PreviewCloudLightCascadeSelection cascades)
    {
        if ((cascades & PreviewCloudLightCascadeSelection.Near) != 0)
        {
            cache.Near.InvalidateGeneration();
        }

        if ((cascades & PreviewCloudLightCascadeSelection.Far) != 0)
        {
            cache.Far.InvalidateGeneration();
        }
    }

    private void BindSharedInputs(in GlCloudLightSliceGenerationInputs inputs)
    {
        SetInt(_uniforms.CloudNoise, 0);
        SetInt(_uniforms.DetailNoise, 1);
        SetInt(_uniforms.CoverageMap, 2);
        SetInt(_uniforms.SparseCloudAtlas, 4);
        SetInt(_uniforms.SparseCloudPageL0, 5);
        SetInt(_uniforms.SparseCloudPageL1, 6);
        SetInt(_uniforms.SparseCloudPageL2, 7);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture3D, inputs.CloudNoiseTexture);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture3D, inputs.DetailNoiseTexture);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, inputs.CoverageTexture);
        BindSparseCloud(inputs.SparseCloud);

        SetVector3(_uniforms.PlanetCenter, inputs.PlanetCenter);
        SetFloat(_uniforms.PlanetRadius, inputs.PlanetRadius);
        SetFloat(
            _uniforms.CumulusBaseAltitude,
            inputs.AltitudeBounds.DensityCumulusBaseAltitude);
        SetFloat(
            _uniforms.CumulusTopAltitude,
            inputs.AltitudeBounds.DensityCumulusTopAltitude);
        SetFloat(_uniforms.CirrusBaseAltitude, inputs.AltitudeBounds.CirrusBaseAltitude);
        SetFloat(_uniforms.CirrusTopAltitude, inputs.AltitudeBounds.CirrusTopAltitude);
        SetFloat(_uniforms.Density, inputs.Density);
        SetFloat(_uniforms.CoverageScale, inputs.CoverageScale);
        SetFloat(_uniforms.VolumeSize, inputs.VolumeSize);
        SetVector3(_uniforms.WindOffset, inputs.WindOffset);
        SetFloat(_uniforms.CirrusStrength, inputs.CirrusStrength);
        SetVector2(_uniforms.CirrusWindOffset, inputs.CirrusWindOffset);
        SetVector2(_uniforms.CirrusWindDirection, inputs.CirrusWindDirection);
        SetInt(_uniforms.Quality, inputs.Quality);
        SetInt(_uniforms.HasCloudNoise, inputs.CloudNoiseTexture != 0 ? 1 : 0);
        SetInt(_uniforms.HasDetailNoise, inputs.DetailNoiseTexture != 0 ? 1 : 0);
        SetInt(_uniforms.HasCoverageMap, inputs.CoverageTexture != 0 ? 1 : 0);
        SetInt(_uniforms.DensityAssetVersion, inputs.DensityAssetVersion);
        SetInt(
            _uniforms.CumulusLayerCount,
            PreviewCloudLayerEnvelope.ClampDeckCount(inputs.CumulusLayerCount));
        SetFloat(_uniforms.InterDeckGap, inputs.InterDeckGap);
        SetFloat(_uniforms.HeightVariance, inputs.HeightVariance);
        SetFloat(_uniforms.UpperThicknessScale, inputs.UpperThicknessScale);
        SetFloat(_uniforms.UpperCoverageScale, inputs.UpperCoverageScale);
        SetFloat(_uniforms.UpperDensityScale, inputs.UpperDensityScale);
        SetVector3(_uniforms.UpperWindOffset, inputs.UpperWindOffset);
        SetFloat(_uniforms.CirrusGap, inputs.CirrusGap);
        SetFloat(_uniforms.CirrusThickness, inputs.CirrusThickness);
        SetInt(_uniforms.StyleBias, Math.Clamp(inputs.StyleBias, 0, 4));
    }

    private void BindSparseCloud(GlSparseCloudSamplingBindings? bindings)
    {
        var valid = bindings is { IsValid: true };
        var sparse = valid ? bindings!.Value : default;
        SetInt(_uniforms.HasSparseCloudTraversal, valid ? 1 : 0);
        SetInt3(_uniforms.SparseCloudOriginL0, sparse.OriginL0);
        SetInt3(_uniforms.SparseCloudOriginL1, sparse.OriginL1);
        SetInt3(_uniforms.SparseCloudOriginL2, sparse.OriginL2);
        _gl.ActiveTexture(TextureUnit.Texture4);
        _gl.BindTexture(TextureTarget.Texture3D, sparse.AtlasTexture);
        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture3D, sparse.PageTableL0);
        _gl.ActiveTexture(TextureUnit.Texture6);
        _gl.BindTexture(TextureTarget.Texture3D, sparse.PageTableL1);
        _gl.ActiveTexture(TextureUnit.Texture7);
        _gl.BindTexture(TextureTarget.Texture3D, sparse.PageTableL2);
    }

    private void BindTransform(
        in PreviewCloudLightCascadeTransform transform,
        float referenceDensity)
    {
        SetVector3(_uniforms.BasisRight, transform.Basis.Right);
        SetVector3(_uniforms.BasisUp, transform.Basis.Up);
        SetVector3(_uniforms.BasisForward, transform.Basis.Forward);
        SetVector2(
            _uniforms.PlaneCenter,
            new Vector2(transform.PlaneCenterX, transform.PlaneCenterY));
        SetFloat(_uniforms.WorldSpan, transform.Profile.WorldSpan);
        SetFloat(_uniforms.LightDepthMin, transform.LightDepthMin);
        SetFloat(_uniforms.LightDepthSpan, transform.LightDepthSpan);
        SetFloat(_uniforms.SliceLength, transform.DepthSliceWorldSize);
        SetFloat(
            _uniforms.FroxelFootprint,
            MathF.Max(transform.PlaneTexelWorldSize, transform.DepthSliceWorldSize));
        SetInt(_uniforms.LayerCount, transform.Profile.Depth);
        SetFloat(_uniforms.ReferenceDensity, referenceDensity);
    }

    private void SetInt(int location, int value)
    {
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    private void SetInt3(int location, int x, int y, int z)
    {
        if (location >= 0)
        {
            _gl.Uniform3(location, x, y, z);
        }
    }

    private void SetInt3(int location, AutoPBR.App.Rendering.Scene.Int3 value) =>
        SetInt3(location, value.X, value.Y, value.Z);

    private void SetFloat(int location, float value)
    {
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    private void SetVector2(int location, Vector2 value)
    {
        if (location >= 0)
        {
            _gl.Uniform2(location, value.X, value.Y);
        }
    }

    private void SetVector3(int location, Vector3 value)
    {
        if (location >= 0)
        {
            _gl.Uniform3(location, value.X, value.Y, value.Z);
        }
    }

    private void FlushErrors()
    {
        for (var index = 0; index < 16 && _gl.GetError() != GLEnum.NoError; index++)
        {
        }
    }

    private readonly record struct Uniforms(
        int CacheSize,
        int CloudNoise,
        int DetailNoise,
        int CoverageMap,
        int BasisRight,
        int BasisUp,
        int BasisForward,
        int PlaneCenter,
        int WorldSpan,
        int LightDepthMin,
        int LightDepthSpan,
        int SliceLength,
        int FroxelFootprint,
        int LayerCount,
        int PlanetCenter,
        int PlanetRadius,
        int CumulusBaseAltitude,
        int CumulusTopAltitude,
        int CirrusBaseAltitude,
        int CirrusTopAltitude,
        int Density,
        int CoverageScale,
        int VolumeSize,
        int WindOffset,
        int CirrusStrength,
        int CirrusWindOffset,
        int CirrusWindDirection,
        int Quality,
        int HasCloudNoise,
        int HasDetailNoise,
        int HasCoverageMap,
        int DensityAssetVersion,
        int ReferenceDensity,
        int SparseCloudAtlas,
        int SparseCloudPageL0,
        int SparseCloudPageL1,
        int SparseCloudPageL2,
        int SparseCloudOriginL0,
        int SparseCloudOriginL1,
        int SparseCloudOriginL2,
        int HasSparseCloudTraversal,
        int CumulusLayerCount,
        int InterDeckGap,
        int HeightVariance,
        int UpperThicknessScale,
        int UpperCoverageScale,
        int UpperDensityScale,
        int UpperWindOffset,
        int CirrusGap,
        int CirrusThickness,
        int StyleBias)
    {
        public static Uniforms Resolve(GlShaderProgram program) =>
            new(
                program.GetUniformLocation("uCacheSize"),
                program.GetUniformLocation("uCloudNoise"),
                program.GetUniformLocation("uDetailNoise"),
                program.GetUniformLocation("uCoverageMap"),
                program.GetUniformLocation("uBasisRight"),
                program.GetUniformLocation("uBasisUp"),
                program.GetUniformLocation("uBasisForward"),
                program.GetUniformLocation("uPlaneCenter"),
                program.GetUniformLocation("uWorldSpan"),
                program.GetUniformLocation("uLightDepthMin"),
                program.GetUniformLocation("uLightDepthSpan"),
                program.GetUniformLocation("uSliceLength"),
                program.GetUniformLocation("uFroxelFootprint"),
                program.GetUniformLocation("uLayerCount"),
                program.GetUniformLocation("uPlanetCenter"),
                program.GetUniformLocation("uPlanetRadius"),
                program.GetUniformLocation("uCumulusBaseAltitude"),
                program.GetUniformLocation("uCumulusTopAltitude"),
                program.GetUniformLocation("uCirrusBaseAltitude"),
                program.GetUniformLocation("uCirrusTopAltitude"),
                program.GetUniformLocation("uDensity"),
                program.GetUniformLocation("uCoverageScale"),
                program.GetUniformLocation("uVolumeSize"),
                program.GetUniformLocation("uWindOffset"),
                program.GetUniformLocation("uCirrusStrength"),
                program.GetUniformLocation("uCirrusWindOffset"),
                program.GetUniformLocation("uCirrusWindDir"),
                program.GetUniformLocation("uQuality"),
                program.GetUniformLocation("uHasCloudNoise"),
                program.GetUniformLocation("uHasDetailNoise"),
                program.GetUniformLocation("uHasCoverageMap"),
                program.GetUniformLocation("uDensityAssetVersion"),
                program.GetUniformLocation("uReferenceDensity"),
                program.GetUniformLocation("uSparseCloudAtlas"),
                program.GetUniformLocation("uSparseCloudPageL0"),
                program.GetUniformLocation("uSparseCloudPageL1"),
                program.GetUniformLocation("uSparseCloudPageL2"),
                program.GetUniformLocation("uSparseCloudOriginL0"),
                program.GetUniformLocation("uSparseCloudOriginL1"),
                program.GetUniformLocation("uSparseCloudOriginL2"),
                program.GetUniformLocation("uHasSparseCloudTraversal"),
                program.GetUniformLocation("uCumulusLayerCount"),
                program.GetUniformLocation("uInterDeckGap"),
                program.GetUniformLocation("uHeightVariance"),
                program.GetUniformLocation("uUpperThicknessScale"),
                program.GetUniformLocation("uUpperCoverageScale"),
                program.GetUniformLocation("uUpperDensityScale"),
                program.GetUniformLocation("uUpperWindOffset"),
                program.GetUniformLocation("uCirrusGap"),
                program.GetUniformLocation("uCirrusThickness"),
                program.GetUniformLocation("uStyleBias"));
    }
}
