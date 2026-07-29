using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudRayFootprintTests
{
    [Theory]
    [InlineData(456, 0.0016836142f)]
    [InlineData(341, 0.0022514020f)]
    public void PixelAngularSize_MatchesVerticalFovContract(
        int traceTargetHeight,
        float expected)
    {
        var actual = PreviewCloudRayFootprint.ComputePixelAngularSize(
            42f * (MathF.PI / 180f),
            traceTargetHeight);

        Assert.InRange(MathF.Abs(actual - expected), 0f, 1e-9f);
    }

    [Fact]
    public void PixelAngularSize_ClampsInvalidHeightAndFov()
    {
        var invalid = PreviewCloudRayFootprint.ComputePixelAngularSize(float.NaN, 0);
        var fallback = PreviewCloudRayFootprint.ComputePixelAngularSize(
            42f * (MathF.PI / 180f),
            1);

        Assert.Equal(fallback, invalid);
        Assert.True(float.IsFinite(invalid));
        Assert.True(invalid > 0f);
    }

    [Fact]
    public void ExplicitLod_IncreasesMonotonicallyWithDistanceAndStepLength()
    {
        var pixelAngularSize = PreviewCloudRayFootprint.ComputePixelAngularSize(
            42f * (MathF.PI / 180f),
            456);
        var near = PreviewCloudRayFootprint.ComputeLod(
            rayDistance: 100f,
            marchStepLength: 0.25f,
            pixelAngularSize,
            worldRepeatSize: 89f,
            textureDimension: 64);
        var distant = PreviewCloudRayFootprint.ComputeLod(
            rayDistance: 1600f,
            marchStepLength: 0.25f,
            pixelAngularSize,
            worldRepeatSize: 89f,
            textureDimension: 64);
        var longerStep = PreviewCloudRayFootprint.ComputeLod(
            rayDistance: 1600f,
            marchStepLength: 8f,
            pixelAngularSize,
            worldRepeatSize: 89f,
            textureDimension: 64);

        Assert.True(distant > near, $"near={near}, distant={distant}");
        Assert.True(longerStep > distant, $"distant={distant}, longerStep={longerStep}");
    }

    [Fact]
    public void CinematicDetailBias_IsBoundedAndSelectsFinerMip()
    {
        const float pixelAngularSize = 0.0017f;
        var high = PreviewCloudRayFootprint.ComputeLod(
            2400f,
            2f,
            pixelAngularSize,
            89f,
            64);
        var cinematic = PreviewCloudRayFootprint.ComputeLod(
            2400f,
            2f,
            pixelAngularSize,
            89f,
            64,
            lodBias: -0.35f);

        Assert.InRange(high - cinematic, 0.349f, 0.351f);
        Assert.True(cinematic >= 0f);
    }

    [Fact]
    public void ShaderAndBackend_UseExplicitDynamicMarchFootprints()
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
        var cloudSource = File.ReadAllText(Path.Combine(shaderRoot, "genesis_clouds.frag"));
        var repairSource = File.ReadAllText(Path.Combine(
            shaderRoot,
            "genesis_clouds_repair.frag"));
        var backendSource = File.ReadAllText(RepositoryPath(
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));

        Assert.Contains("uniform float uPixelAngularSize;", cloudSource, StringComparison.Ordinal);
        Assert.Contains("uniform float uPixelAngularSize;", repairSource, StringComparison.Ordinal);
        Assert.Contains("textureLod(cloudNoise", densitySource, StringComparison.Ordinal);
        Assert.Contains("textureLod(detailNoise", densitySource, StringComparison.Ordinal);
        Assert.Contains("textureLod(coverageMap", densitySource, StringComparison.Ordinal);
        Assert.DoesNotContain("texture(detailNoise", densitySource, StringComparison.Ordinal);
        Assert.DoesNotContain("texture(coverageMap", densitySource, StringComparison.Ordinal);
        Assert.Contains("sampleT * uPixelAngularSize", cloudSource, StringComparison.Ordinal);
        Assert.Contains("sampleDistance * uPixelAngularSize", repairSource, StringComparison.Ordinal);
        Assert.Contains("traceTargetHeight", backendSource, StringComparison.Ordinal);
        Assert.Contains("frame.Vh));", backendSource, StringComparison.Ordinal);
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
