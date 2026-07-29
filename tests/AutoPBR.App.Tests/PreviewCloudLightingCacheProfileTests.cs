using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudLightingCacheProfileTests
{
    [Theory]
    [InlineData(PreviewVolumetricQuality.Low)]
    [InlineData(PreviewVolumetricQuality.Medium)]
    public void LowAndMedium_RetainShortMarch(int quality)
    {
        var profile = PreviewCloudLightingCacheProfiles.Resolve(quality);
        Assert.False(profile.IsEnabled);
        Assert.Equal("none", profile.Format);
    }

    [Fact]
    public void High_MapsToAcceptedCascadeContract()
    {
        var profile = PreviewCloudLightingCacheProfiles.Resolve(PreviewVolumetricQuality.High);
        Assert.True(profile.IsEnabled);
        Assert.Equal("RG16F", profile.Format);
        Assert.Equal((192, 192, 16, 640f, 2),
            (profile.Near.Width, profile.Near.Height, profile.Near.Depth,
                profile.Near.WorldSpan, profile.Near.UpdateIntervalFrames));
        Assert.Equal((128, 128, 12, 2560f, 4),
            (profile.Far.Width, profile.Far.Height, profile.Far.Depth,
                profile.Far.WorldSpan, profile.Far.UpdateIntervalFrames));
        Assert.Equal(0, profile.LocalConeTapCount);
        Assert.Equal(0.20f, profile.NearOverlapFraction);
    }

    [Fact]
    public void Cinematic_MapsToAcceptedCascadeContract()
    {
        var profile = PreviewCloudLightingCacheProfiles.Resolve(PreviewVolumetricQuality.Cinematic);
        Assert.Equal((256, 256, 24, 1),
            (profile.Near.Width, profile.Near.Height, profile.Near.Depth,
                profile.Near.UpdateIntervalFrames));
        Assert.Equal((192, 192, 16, 4),
            (profile.Far.Width, profile.Far.Height, profile.Far.Depth,
                profile.Far.UpdateIntervalFrames));
        Assert.Equal(2, profile.LocalConeTapCount);
    }

    [Fact]
    public void GenerationPlan_SelectsComputeFragmentAndCompatibilityFallbacks()
    {
        var compute = PreviewGlCapabilities.FromStrings(
            "4.6.0 NVIDIA",
            "NVIDIA",
            "RTX",
            string.Empty,
            forceOpenGlEs: false);
        var fragment = PreviewGlCapabilities.FromStrings(
            "3.3.0",
            "Vendor",
            "Renderer",
            string.Empty,
            forceOpenGlEs: false);
        var gles = PreviewGlCapabilities.FromStrings(
            "OpenGL ES 3.0",
            "Google",
            "ANGLE",
            string.Empty,
            forceOpenGlEs: true);

        Assert.Equal(
            PreviewCloudLightingCacheGenerationPath.ComputeImageStore,
            PreviewCloudLightingCachePlan.Create(
                compute,
                PreviewVolumetricQuality.High).PreferredGenerationPath);
        Assert.Equal(
            PreviewCloudLightingCacheGenerationPath.FragmentSlices,
            PreviewCloudLightingCachePlan.Create(
                fragment,
                PreviewVolumetricQuality.High).PreferredGenerationPath);
        Assert.Equal(
            PreviewCloudLightingCacheGenerationPath.ShortMarch,
            PreviewCloudLightingCachePlan.Create(
                gles,
                PreviewVolumetricQuality.Cinematic).PreferredGenerationPath);
        Assert.Equal(
            PreviewCloudLightingCacheGenerationPath.ShortMarch,
            PreviewCloudLightingCachePlan.Create(
                compute,
                PreviewVolumetricQuality.Medium).PreferredGenerationPath);
    }

    [Fact]
    public void Cq30Plan_DoesNotClaimUnallocatedCacheIsActive()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.6.0 NVIDIA",
            "NVIDIA",
            "RTX",
            string.Empty,
            forceOpenGlEs: false);
        var plan = PreviewCloudLightingCachePlan.Create(
            caps,
            PreviewVolumetricQuality.Cinematic);

        Assert.Equal(
            PreviewCloudLightingCacheGenerationPath.ShortMarch,
            plan.ActiveRuntimePath);
        Assert.Contains("resources=not-allocated-cq3.0", plan.FormatDiagnostic(),
            StringComparison.Ordinal);
        Assert.Contains("cameraFogFroxels=separate", plan.FormatDiagnostic(),
            StringComparison.Ordinal);
    }
}
