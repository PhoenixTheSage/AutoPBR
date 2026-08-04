using System.Numerics;

using AutoPBR.App.Rendering.Scene;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal readonly record struct GlCloudLightSliceGenerationInputs(
    PreviewCloudLightCascadeTransform NearTransform,
    PreviewCloudLightCascadeTransform FarTransform,
    PreviewCloudLightAltitudeBounds AltitudeBounds,
    Vector3 PlanetCenter,
    float PlanetRadius,
    float Density,
    float CoverageScale,
    float VolumeSize,
    Vector3 WindOffset,
    float CirrusStrength,
    Vector2 CirrusWindOffset,
    Vector2 CirrusWindDirection,
    int Quality,
    int DensityAssetVersion,
    uint CloudNoiseTexture,
    uint DetailNoiseTexture,
    uint CoverageTexture,
    float ReferenceDensity = -1f,
    GlSparseCloudSamplingBindings? SparseCloud = null,
    int CumulusLayerCount = PreviewCloudLayerEnvelope.DefaultCumulusLayerCount,
    float InterDeckGap = PreviewCloudLayerEnvelope.DefaultInterDeckGap,
    float HeightVariance = PreviewCloudLayerEnvelope.DefaultHeightVariance,
    float UpperThicknessScale = PreviewCloudLayerEnvelope.DefaultUpperThicknessScale,
    float UpperCoverageScale = PreviewCloudLayerEnvelope.DefaultUpperCoverageScale,
    float UpperDensityScale = PreviewCloudLayerEnvelope.DefaultUpperDensityScale,
    Vector3 UpperWindOffset = default,
    float CirrusGap = PreviewCloudLayerEnvelope.DefaultCirrusGap,
    float CirrusThickness = PreviewCloudLayerEnvelope.DefaultCirrusThickness,
    int StyleBias = 0)
{
    public int DensityIdentity =>
        SparseCloud is { IsValid: true } sparse
            ? sparse.IdentitySignature
            : 0;
}

/// <summary>
/// Ordered GL 3.3 fragment-slice generator. Each layer writes the RG16F array and one alternating
/// RG32F prefix surface; the next layer samples the other prefix surface, avoiding a texture
/// feedback loop with the attached array.
/// </summary>
internal sealed class GlCloudLightFragmentSliceGenerator
{
    private readonly GL _gl;
    private readonly GlShaderProgram _program;
    private readonly uint _quadVertexArray;
    private readonly Uniforms _uniforms;

    public GlCloudLightFragmentSliceGenerator(
        GL gl,
        GlShaderProgram program,
        uint quadVertexArray)
    {
        _gl = gl;
        _program = program;
        _quadVertexArray = quadVertexArray;
        _uniforms = Uniforms.Resolve(program);
    }

    public bool TryGenerate(
        GlCloudLightFroxelCache cache,
        in GlCloudLightSliceGenerationInputs inputs,
        int restoreViewportWidth,
        int restoreViewportHeight,
        out string diagnostic)
    {
        return TryGenerate(
            cache,
            inputs,
            PreviewCloudLightCascadeSelection.Both,
            0,
            restoreViewportWidth,
            restoreViewportHeight,
            out diagnostic);
    }

    public bool TryGenerate(
        GlCloudLightFroxelCache cache,
        in GlCloudLightSliceGenerationInputs inputs,
        PreviewCloudLightCascadeSelection cascades,
        int generationFrame,
        int restoreViewportWidth,
        int restoreViewportHeight,
        out string diagnostic)
    {
        if (!_program.IsValid ||
            _quadVertexArray == 0 ||
            !cache.IsValid ||
            cascades == PreviewCloudLightCascadeSelection.None)
        {
            diagnostic = "program-quad-cache-or-selection-invalid";
            return false;
        }

        var priorBlend = _gl.IsEnabled(EnableCap.Blend);
        var priorDepth = _gl.IsEnabled(EnableCap.DepthTest);
        var priorScissor = _gl.IsEnabled(EnableCap.ScissorTest);
        var priorDepthMask = _gl.GetBoolean(GetPName.DepthWritemask);
        var priorColorMask = new bool[4];
        _gl.GetBoolean(GetPName.ColorWritemask, priorColorMask);

        try
        {
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.DepthMask(false);
            _gl.ColorMask(true, true, true, true);
            _program.Use();
            BindSharedInputs(inputs);
            _gl.BindVertexArray(_quadVertexArray);

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
                $"generated-cq3.6-fragment;cascades={cascades};" +
                $"nearGeneration={cache.Near.GenerationId};" +
                $"farGeneration={cache.Far.GenerationId};" +
                $"fixture={(inputs.ReferenceDensity >= 0f ? "fixed-density" : "cq2-density")}";
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
            _gl.BindVertexArray(0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.Viewport(
                0,
                0,
                (uint)Math.Max(1, restoreViewportWidth),
                (uint)Math.Max(1, restoreViewportHeight));
            _gl.DepthMask(priorDepthMask);
            _gl.ColorMask(
                priorColorMask[0],
                priorColorMask[1],
                priorColorMask[2],
                priorColorMask[3]);
            SetEnabled(EnableCap.Blend, priorBlend);
            SetEnabled(EnableCap.DepthTest, priorDepth);
            SetEnabled(EnableCap.ScissorTest, priorScissor);
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
        FlushErrors();
        for (var layer = 0; layer < target.Profile.Depth; layer++)
        {
            if (!target.TryBindGenerationLayer(
                    layer,
                    out var previousPrefixTexture,
                    out var bindDiagnostic))
            {
                diagnostic = $"layer-{layer}-{bindDiagnostic}";
                return false;
            }

            SetInt(_uniforms.LayerIndex, layer);
            SetInt(_uniforms.HasPrevious, layer > 0 ? 1 : 0);
            _gl.ActiveTexture(TextureUnit.Texture3);
            _gl.BindTexture(TextureTarget.Texture2D, previousPrefixTexture);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            var drawError = _gl.GetError();
            if (drawError != GLEnum.NoError)
            {
                diagnostic = $"layer-{layer}-draw-{drawError}";
                return false;
            }
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
        SetInt(_uniforms.PreviousPrefix, 3);
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

    private void SetEnabled(EnableCap capability, bool enabled)
    {
        if (enabled)
        {
            _gl.Enable(capability);
        }
        else
        {
            _gl.Disable(capability);
        }
    }

    private void SetInt(int location, int value)
    {
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    private void SetInt3(int location, AutoPBR.App.Rendering.Scene.Int3 value)
    {
        if (location >= 0)
        {
            _gl.Uniform3(location, value.X, value.Y, value.Z);
        }
    }

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
        int CloudNoise,
        int DetailNoise,
        int CoverageMap,
        int PreviousPrefix,
        int BasisRight,
        int BasisUp,
        int BasisForward,
        int PlaneCenter,
        int WorldSpan,
        int LightDepthMin,
        int LightDepthSpan,
        int SliceLength,
        int FroxelFootprint,
        int LayerIndex,
        int LayerCount,
        int HasPrevious,
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
                program.GetUniformLocation("uCloudNoise"),
                program.GetUniformLocation("uDetailNoise"),
                program.GetUniformLocation("uCoverageMap"),
                program.GetUniformLocation("uPreviousPrefix"),
                program.GetUniformLocation("uBasisRight"),
                program.GetUniformLocation("uBasisUp"),
                program.GetUniformLocation("uBasisForward"),
                program.GetUniformLocation("uPlaneCenter"),
                program.GetUniformLocation("uWorldSpan"),
                program.GetUniformLocation("uLightDepthMin"),
                program.GetUniformLocation("uLightDepthSpan"),
                program.GetUniformLocation("uSliceLength"),
                program.GetUniformLocation("uFroxelFootprint"),
                program.GetUniformLocation("uLayerIndex"),
                program.GetUniformLocation("uLayerCount"),
                program.GetUniformLocation("uHasPrevious"),
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
