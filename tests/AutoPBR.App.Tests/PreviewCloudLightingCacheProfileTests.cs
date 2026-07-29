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
    public void Cq35GroundTransmittance_UsesFarNativeProfilesAndCinematicOverlap()
    {
        var high = PreviewCloudGroundTransmittanceProfiles.Resolve(
            PreviewVolumetricQuality.High);
        var cinematic = PreviewCloudGroundTransmittanceProfiles.Resolve(
            PreviewVolumetricQuality.Cinematic);
        var medium = PreviewCloudGroundTransmittanceProfiles.Resolve(
            PreviewVolumetricQuality.Medium);

        Assert.Equal((128, 128, 2560f, false),
            (high.Width, high.Height, high.WorldSpan,
                high.CombineNearAndFar));
        Assert.Equal((192, 192, 2560f, true),
            (cinematic.Width, cinematic.Height, cinematic.WorldSpan,
                cinematic.CombineNearAndFar));
        Assert.Equal(new System.Numerics.Vector2(1f / 192f, 1f / 192f),
            cinematic.TexelSize);
        Assert.False(medium.IsEnabled);
        Assert.Contains("near-far-overlap",
            cinematic.FormatDiagnostic(),
            StringComparison.Ordinal);
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

    [Fact]
    public void Cq33Plan_ReportsCacheSamplingWithoutConflatingGenerator()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.6.0 NVIDIA",
            "NVIDIA",
            "RTX",
            string.Empty,
            forceOpenGlEs: false);
        var plan = PreviewCloudLightingCachePlan.Create(
            caps,
            PreviewVolumetricQuality.Cinematic) with
        {
            ActiveRuntimePath =
                PreviewCloudLightingCacheGenerationPath.CacheSampling,
        };

        Assert.Contains("preferredGenerator=compute-image-store",
            plan.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("activeRuntime=cache-sampling",
            plan.FormatDiagnostic(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cq34ShadingProfile_UsesAcceptedTwoOctavesAndRestrainedEnergy()
    {
        var profile = PreviewCloudLightingShadingProfiles.Default;

        Assert.Equal(new System.Numerics.Vector3(0.50f, 0.50f, 0.55f),
            profile.Octave1);
        Assert.Equal(new System.Numerics.Vector3(0.25f, 0.25f, 0.30f),
            profile.Octave2);
        Assert.Equal(2.25f, profile.ScatteredEnergyClamp);
        Assert.Equal(0.18f, profile.CachedSkyVisibilityFloor);
        Assert.Equal(0.11f, profile.GroundBounceStrength);
        Assert.Equal(0.45f, profile.LocalConeOpticalDepthScale);
    }

    [Fact]
    public void Cq34GroundBounce_UsesLinearLowFrequencyGroundAlbedo()
    {
        var material = new PreviewMaterial
        {
            Width = 1,
            Height = 1,
            AlbedoRgba = new byte[] { 128, 64, 32, 255 },
        };

        var bounce = PreviewCloudGroundBounceEstimator.Estimate(material);

        Assert.InRange(bounce.X, 0.215f, 0.217f);
        Assert.InRange(bounce.Y, 0.050f, 0.052f);
        Assert.InRange(bounce.Z, 0.014f, 0.015f);
        Assert.Equal(
            PreviewCloudGroundBounceEstimator.DefaultLinear,
            PreviewCloudGroundBounceEstimator.Estimate(null));
    }

    [Fact]
    public void Cq32GeneratorFallback_UsesComputeThenFragmentThenShortMarch()
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
            PreviewCloudLightingCacheGenerationPath.ComputeImageStore,
            PreviewCloudLightingCacheGeneratorFallback.Select(
                plan,
                computeProgramReady: true,
                computeSessionFaulted: false,
                fragmentProgramReady: true));
        Assert.Equal(
            PreviewCloudLightingCacheGenerationPath.FragmentSlices,
            PreviewCloudLightingCacheGeneratorFallback.Select(
                plan,
                computeProgramReady: true,
                computeSessionFaulted: true,
                fragmentProgramReady: true));
        Assert.Equal(
            PreviewCloudLightingCacheGenerationPath.FragmentSlices,
            PreviewCloudLightingCacheGeneratorFallback.Select(
                plan,
                computeProgramReady: false,
                computeSessionFaulted: false,
                fragmentProgramReady: true));
        Assert.Equal(
            PreviewCloudLightingCacheGenerationPath.ShortMarch,
            PreviewCloudLightingCacheGeneratorFallback.Select(
                plan,
                computeProgramReady: false,
                computeSessionFaulted: true,
                fragmentProgramReady: false));
    }

    [Fact]
    public void ComputeCloudCache_RequiresDesktopGl43EvenWhenExtensionsExist()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.2.0",
            "Vendor",
            "Renderer",
            "GL_ARB_compute_shader GL_ARB_shader_image_load_store",
            forceOpenGlEs: false);

        Assert.True(caps.ComputeShaders);
        Assert.True(caps.ImageLoadStore);
        Assert.True(caps.CanUseFragmentCloudLightingCache);
        Assert.False(caps.CanUseComputeCloudLightingCache);
    }

    [Fact]
    public void Cq36Scheduler_AppliesAcceptedHighCadence()
    {
        var profile = PreviewCloudLightingCacheProfiles.Resolve(
            PreviewVolumetricQuality.High);

        var frame1 = PreviewCloudLightUpdateScheduler.Evaluate(
            Request(profile, frame: 1, nearLast: 0, farLast: 0));
        var frame2 = PreviewCloudLightUpdateScheduler.Evaluate(
            Request(profile, frame: 2, nearLast: 0, farLast: 0));
        var frame4 = PreviewCloudLightUpdateScheduler.Evaluate(
            Request(profile, frame: 4, nearLast: 2, farLast: 0));

        Assert.Equal(PreviewCloudLightCascadeSelection.None, frame1.Cascades);
        Assert.Equal(PreviewCloudLightCascadeSelection.Near, frame2.Cascades);
        Assert.Equal(PreviewCloudLightCascadeSelection.Both, frame4.Cascades);
        Assert.False(frame2.InvalidateBeforeGeneration);
    }

    [Fact]
    public void Cq36Scheduler_AppliesAcceptedCinematicCadence()
    {
        var profile = PreviewCloudLightingCacheProfiles.Resolve(
            PreviewVolumetricQuality.Cinematic);

        var frame1 = PreviewCloudLightUpdateScheduler.Evaluate(
            Request(profile, frame: 1, nearLast: 0, farLast: 0));
        var frame3 = PreviewCloudLightUpdateScheduler.Evaluate(
            Request(profile, frame: 3, nearLast: 2, farLast: 0));
        var frame4 = PreviewCloudLightUpdateScheduler.Evaluate(
            Request(profile, frame: 4, nearLast: 3, farLast: 0));

        Assert.Equal(PreviewCloudLightCascadeSelection.Near, frame1.Cascades);
        Assert.Equal(PreviewCloudLightCascadeSelection.Near, frame3.Cascades);
        Assert.Equal(PreviewCloudLightCascadeSelection.Both, frame4.Cascades);
        Assert.Equal(PreviewCloudLightUpdateScheduler.MaximumReuseFrames, frame4.FarAge);
    }

    [Fact]
    public void Cq36Scheduler_ImmediatelyRebuildsInvalidatedOrMissingCaches()
    {
        var profile = PreviewCloudLightingCacheProfiles.Resolve(
            PreviewVolumetricQuality.High);
        var initial = PreviewCloudLightUpdateScheduler.Evaluate(
            Request(
                profile,
                frame: 0,
                nearLast: -1,
                farLast: -1,
                nearGenerated: false,
                farGenerated: false));
        var material = PreviewCloudLightUpdateScheduler.Evaluate(
            Request(
                profile,
                frame: 1,
                nearLast: 0,
                farLast: 0,
                materialChanged: true));
        var camera = PreviewCloudLightUpdateScheduler.Evaluate(
            Request(
                profile,
                frame: 1,
                nearLast: 0,
                farLast: 0,
                largeCamera: true));

        Assert.Equal(
            PreviewCloudLightInvalidationReason.InitialGeneration,
            initial.InvalidationReason);
        Assert.Equal(
            PreviewCloudLightInvalidationReason.MaterialSettingsChanged,
            material.InvalidationReason);
        Assert.Equal(
            PreviewCloudLightInvalidationReason.LargeCameraMovement,
            camera.InvalidationReason);
        Assert.All(
            new[] { initial, material, camera },
            decision =>
            {
                Assert.Equal(
                    PreviewCloudLightCascadeSelection.Both,
                    decision.Cascades);
                Assert.True(decision.InvalidateBeforeGeneration);
            });
    }

    [Fact]
    public void Cq36SunInvalidation_UsesHalfDegreeMaterialThreshold()
    {
        static System.Numerics.Vector3 Direction(float degrees)
        {
            var radians = degrees * MathF.PI / 180f;
            return System.Numerics.Vector3.Normalize(
                new System.Numerics.Vector3(
                    MathF.Sin(radians),
                    -MathF.Cos(radians),
                    0f));
        }

        Assert.False(
            PreviewCloudLightUpdateScheduler.IsMaterialSunDirectionChange(
                Direction(0f),
                Direction(0.49f)));
        Assert.True(
            PreviewCloudLightUpdateScheduler.IsMaterialSunDirectionChange(
                Direction(0f),
                Direction(0.51f)));
    }

    private static PreviewCloudLightUpdateRequest Request(
        PreviewCloudLightingCacheProfile profile,
        int frame,
        int nearLast,
        int farLast,
        bool nearGenerated = true,
        bool farGenerated = true,
        bool materialChanged = false,
        bool largeCamera = false) =>
        new(
            profile,
            frame,
            nearGenerated,
            farGenerated,
            nearLast,
            farLast,
            materialChanged,
            largeCamera,
            MaterialSunDirectionChanged: false,
            LightBasisChanged: false);
}
