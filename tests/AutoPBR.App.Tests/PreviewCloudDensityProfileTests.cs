using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudDensityProfileTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void V2ProfilePolicy_RequiresDesktopAndCompletedShaderSemantics(
        bool useOpenGlEs,
        bool shaderProfileReady,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenGlPreviewBackend.CanUseCq2V2DensityProfile(
                useOpenGlEs,
                shaderProfileReady));
    }

    [Fact]
    public void DensityShader_PreservesV1AndConsumesV2WeatherChannelsIndependently()
    {
        var shaderRoot = RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders");
        var densitySource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "common",
            "volumetric_clouds_density_maps.glsl"));
        var lightingSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "common",
            "volumetric_clouds.glsl"));
        var cloudSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "genesis_clouds.frag"));
        var repairSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "genesis_clouds_repair.frag"));

        Assert.Contains("uniform int uDensityAssetVersion;", cloudSource, StringComparison.Ordinal);
        Assert.Contains("uniform int uDensityAssetVersion;", repairSource, StringComparison.Ordinal);
        Assert.Contains("weather.ba = vec2(0.5, 0.0);", densitySource, StringComparison.Ordinal);
        Assert.Contains("float densityPotential", densitySource, StringComparison.Ordinal);
        Assert.Contains("float convection", densitySource, StringComparison.Ordinal);
        Assert.Contains("weather.z, weather.w, densityAssetVersion", densitySource, StringComparison.Ordinal);
        Assert.Contains("float wispy = dn.b;", densitySource, StringComparison.Ordinal);
        Assert.Contains("vcCloudDensityPotentialScale(", lightingSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Backend_BindsSelectedAssetVersionForTraceAndRepair()
    {
        var backendSource = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));

        Assert.Contains("Cq2V2ShaderProfileReady = true", backendSource, StringComparison.Ordinal);
        Assert.Contains("ru.DensityAssetVersion,", backendSource, StringComparison.Ordinal);
        Assert.Contains("cu.DensityAssetVersion,", backendSource, StringComparison.Ordinal);
        Assert.Contains("densitySemantics=v{_cloudDensityAssetVersion}", backendSource, StringComparison.Ordinal);
        Assert.Contains("ca1-broad-boundary-high-cinematic", backendSource, StringComparison.Ordinal);
        Assert.Contains("ca2-dual-scale-asymmetric-v2-templates", backendSource, StringComparison.Ordinal);
        Assert.Contains("thinFeaturePreservation=", backendSource, StringComparison.Ordinal);
        Assert.Contains("PreviewCloudTemporalLowAlphaWeight.FormatDiagnostic()", backendSource,
            StringComparison.Ordinal);
        Assert.Contains("PreviewCloudEdgeRepairClassifier.FormatDiagnostic()", backendSource,
            StringComparison.Ordinal);
        Assert.Contains("PreviewCloudLightingShadingProfiles.FormatDiagnostic()", backendSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DensityShader_UsesSecondDetailLookupOnlyForV2HighCinematicBoundaries()
    {
        var densitySource = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders",
            "common",
            "volumetric_clouds_density_maps.glsl"));
        Assert.Contains(
            "if (quality >= 2 && edgeWeight > 1e-3)",
            densitySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "coverage * mix(0.055, 1.22, population)",
            densitySource,
            StringComparison.Ordinal);
        Assert.Contains("vec4 boundaryDn = textureLod(", densitySource, StringComparison.Ordinal);
        Assert.Contains(
            "float boundaryScale = detailScale * (quality >= 3 ? 0.68 : 0.82);",
            densitySource,
            StringComparison.Ordinal);
        Assert.Contains("float curl = dn.a * 2.0 - 1.0;", densitySource, StringComparison.Ordinal);
        Assert.DoesNotContain("texture(detailNoise", densitySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Ca1BoundaryMaterial_IsDirectionalHeightAwareAndCoreProtected()
    {
        var shaderRoot = RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders");
        var densitySource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "common",
            "volumetric_clouds_density_maps.glsl"));
        var traceSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "genesis_clouds.frag"));
        var repairSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "genesis_clouds_repair.frag"));
        var lightSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "common",
            "cloud_light_cache_generation.glsl"));

        Assert.Contains("vec2 flowDirection", densitySource, StringComparison.Ordinal);
        Assert.Contains("vec2 along = length(flowDirection)", densitySource, StringComparison.Ordinal);
        Assert.Contains("float heightShear = (h - 0.42)", densitySource, StringComparison.Ordinal);
        Assert.Contains("edgeWeight = 1.0 - smoothstep(0.12, 0.70, base);", densitySource, StringComparison.Ordinal);
        Assert.Contains("float boundaryRepeat = boundaryScale / 0.72;", densitySource, StringComparison.Ordinal);
        Assert.Contains("float lowerEvaporation = 1.0 - smoothstep", densitySource, StringComparison.Ordinal);
        Assert.Contains("erosionStrength = mix(0.055, materialStrength, edgeWeight);", densitySource, StringComparison.Ordinal);
        Assert.Contains("uWindOffset, uCirrusWindDir,", traceSource, StringComparison.Ordinal);
        Assert.Contains("uCirrusWindDir,", repairSource, StringComparison.Ordinal);
        Assert.Contains("uCirrusWindDir,", lightSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CirrusDetailWarp_IsExplicitLodAndCinematicV2Only()
    {
        var shaderRoot = RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders");
        var densitySource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "common",
            "volumetric_clouds_density_maps.glsl"));
        var cloudSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "genesis_clouds.frag"));
        var repairSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "genesis_clouds_repair.frag"));

        Assert.Contains("float vcCirrusDensityWithDetail(", densitySource, StringComparison.Ordinal);
        Assert.Contains(
            "if (quality < 3 || densityAssetVersion < 2 || hasDetailNoise < 1)",
            densitySource,
            StringComparison.Ordinal);
        Assert.Contains("vec2 signedDetail = detail.ba * 2.0 - 1.0;", densitySource, StringComparison.Ordinal);
        Assert.Contains("vec4 detail = textureLod(detailNoise,", densitySource, StringComparison.Ordinal);
        Assert.Contains("float cirrusSampleFootprint = max(", cloudSource, StringComparison.Ordinal);
        Assert.Contains("vcCirrusDensityWithDetail(", cloudSource, StringComparison.Ordinal);
        Assert.Contains("vcCirrusDensityWithDetail(", repairSource, StringComparison.Ordinal);
        Assert.DoesNotContain("texture(detailNoise", densitySource, StringComparison.Ordinal);
    }

    [Fact]
    public void WeatherShader_ExpandsV2WorldAddressingWithoutChangingV1Period()
    {
        var densitySource = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders",
            "common",
            "volumetric_clouds_density_maps.glsl"));
        var backendSource = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));

        Assert.Contains(
            "primaryPeriod = scale * (densityAssetVersion >= 2 ? 16.0 : 4.0);",
            densitySource,
            StringComparison.Ordinal);
        Assert.Contains("const mat2 weatherRotationScale", densitySource, StringComparison.Ordinal);
        Assert.Contains(
            "float secondaryPeriod = primaryPeriod * 0.447214;",
            densitySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "float secondaryBlend = mix(0.08, 0.22, saturate1(weather.a));",
            densitySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "var windPeriod = Math.Max(frame.Settings.CloudVolumeSize, 8f) * 16f;",
            backendSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "var period = Math.Max(settings.CloudVolumeSize, 8f) * 16f;",
            backendSource,
            StringComparison.Ordinal);

        var legacyNeutral = densitySource.IndexOf(
            "weather.ba = vec2(0.5, 0.0);",
            StringComparison.Ordinal);
        var legacyReturn = densitySource.IndexOf(
            "return weather;",
            legacyNeutral,
            StringComparison.Ordinal);
        var rotatedAddress = densitySource.IndexOf(
            "const mat2 weatherRotationScale",
            StringComparison.Ordinal);
        Assert.True(legacyNeutral >= 0 && legacyReturn > legacyNeutral);
        Assert.True(
            legacyReturn < rotatedAddress,
            "The v1 compatibility branch must return before v2 secondary addressing.");
    }

    [Fact]
    public void SunDiscOcclusion_IsFullResolutionAndPostTemporal()
    {
        var shaderRoot = RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders");
        var upsampleSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "genesis_clouds_upsample.frag"));
        var occlusionSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "common",
            "cloud_direct_disc.glsl"));
        var backendSource = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));

        Assert.Contains(
            "common/cloud_direct_disc.glsl",
            upsampleSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "float compositeCoverage = uApplyCloudEncoding > 0",
            upsampleSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "compositeRgb = presentedRgb * (compositeCoverage / max(coverage, 1e-5))",
            upsampleSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "cdoDirectDiscOcclusionAlpha(",
            upsampleSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "smoothstep(0.45, 0.60, opacity)",
            occlusionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "upu.SunCosDiscEdge",
            backendSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "upu.SunDiscVisibility",
            backendSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ComputeCloudDirectDiscCosEdge(in frame)",
            backendSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ComputeCloudSunDiscVisibility(in frame)",
            backendSource,
            StringComparison.Ordinal);
    }

    private static string RepositoryPath(params string[] segments)
    {
        var parts = new[]
        {
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
        }.Concat(segments).ToArray();
        return Path.GetFullPath(Path.Combine(parts));
    }
}
