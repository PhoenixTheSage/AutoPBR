using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Tests;

public sealed class PreviewVolumetricQualityTests
{
    [Theory]
    [InlineData(0, 8, 24, 12, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f)]
    [InlineData(1, 4, 32, 20, 2.8f, 0.28f, 0.42f, 1, 0.35f, 0.45f, 0.78f, 1.0f)]
    [InlineData(2, 3, 48, 24, 4.2f, 0.38f, 0.72f, 2, 0.42f, 0.55f, 0.84f, 1.0f)]
    [InlineData(3, 3, 48, 24, 4.2f, 0.38f, 0.84f, 3, 0.42f, 0.55f, 0.84f, 1.0f)]
    public void Resolve_ReturnsExpectedProfile(
        int quality,
        int divisor,
        int minSize,
        int slices,
        float depthExp,
        float froxelTemporal,
        float cloudTemporal,
        int cloudQuality,
        float volumeTemporal,
        float upsampleTemporal,
        float previewTaa,
        float previewTaaJitterScale)
    {
        var profile = PreviewVolumetricQuality.Resolve(quality);

        Assert.Equal(divisor, profile.FroxelDivisor);
        Assert.Equal(minSize, profile.FroxelMinSize);
        Assert.Equal(slices, profile.FroxelSlices);
        Assert.Equal(depthExp, profile.FroxelDepthExp);
        Assert.Equal(froxelTemporal, profile.FroxelTemporal3DWeight);
        Assert.Equal(cloudTemporal, profile.CloudTemporalWeight);
        Assert.Equal(cloudQuality, profile.CloudQuality);
        Assert.Equal(volumeTemporal, profile.VolumeIntegrateTemporalWeight);
        Assert.Equal(upsampleTemporal, profile.UpsampleTemporalWeight);
        Assert.Equal(previewTaa, profile.PreviewTaaWeight);
        Assert.Equal(previewTaaJitterScale, profile.PreviewTaaJitterScale);
    }

    [Theory]
    [InlineData(-1, 8, 0)]
    [InlineData(99, 3, 3)]
    public void Resolve_ClampsOutOfRangeQuality(int quality, int expectedDivisor, int expectedCloudQuality)
    {
        var profile = PreviewVolumetricQuality.Resolve(quality);
        Assert.Equal(expectedDivisor, profile.FroxelDivisor);
        Assert.Equal(expectedCloudQuality, profile.CloudQuality);
    }

    [Theory]
    [InlineData(0, "Low")]
    [InlineData(1, "Medium")]
    [InlineData(2, "High")]
    [InlineData(3, "Cinematic")]
    [InlineData(99, "Cinematic")]
    public void GetName_UsesPersistedQualityContract(int quality, string expected)
    {
        Assert.Equal(expected, PreviewVolumetricQuality.GetName(quality));
    }

    [Fact]
    public void ResolveFroxelDimensions_RespectsMinimumSize()
    {
        var profile = PreviewVolumetricQuality.Resolve(0);
        Assert.Equal(25, profile.ResolveFroxelWidth(200));
        Assert.Equal(24, profile.ResolveFroxelHeight(100));
        Assert.Equal(24, profile.ResolveFroxelWidth(120));
    }

    [Theory]
    [InlineData(1, 0, 0.82f, 0.52f, 0.08f, 0.93f, 0.035f, 0.08f, 0.08f, 0.22f, 0.35f, 0.30f)]
    [InlineData(1, 1, 0.88f, 0.35f, 0.05f, 0.95f, 0.020f, 0.10f, 0.07f, 0.16f, 0.24f, 0.22f)]
    [InlineData(1, 2, 0.80f, 0.70f, 0.10f, 0.90f, 0.035f, 0.60f, 0.55f, 0.80f, 0.95f, 0.72f)]
    [InlineData(1, 3, 0.68f, 0.65f, 0.05f, 0.86f, 0.070f, 0.04f, 0.05f, 0.12f, 0.24f, 0.20f)]
    [InlineData(1, 4, 0.72f, 0f, 0.03f, 0.84f, 0.035f, 0.18f, 0.28f, 0.45f, 0.30f, 0.55f)]
    public void ResolvePreviewTaa_ReturnsModeProfile(
        int quality,
        int mode,
        float temporal,
        float jitter,
        float stableBoost,
        float maxStable,
        float sharpen,
        float edgeFloor,
        float edgeBlend,
        float sourceFilter,
        float silhouetteHistory,
        float fxaaStrength)
    {
        var profile = PreviewVolumetricQuality.ResolvePreviewTaa(quality, mode);

        Assert.Equal(temporal, profile.TemporalWeight, 0.0001f);
        Assert.Equal(jitter, profile.JitterScale, 0.0001f);
        Assert.Equal(stableBoost, profile.StableTemporalBoost, 0.0001f);
        Assert.Equal(maxStable, profile.MaxStableTemporal, 0.0001f);
        Assert.Equal(sharpen, profile.SharpenStrength, 0.0001f);
        Assert.Equal(edgeFloor, profile.DepthEdgeHistoryFloor, 0.0001f);
        Assert.Equal(edgeBlend, profile.EdgeAaBlend, 0.0001f);
        Assert.Equal(sourceFilter, profile.SourceFilterStrength, 0.0001f);
        Assert.Equal(silhouetteHistory, profile.SilhouetteHistoryWeight, 0.0001f);
        Assert.Equal(fxaaStrength, profile.FxaaEdgeStrength, 0.0001f);
    }

    [Theory]
    [InlineData(0.55f, true, 1, 0.275f)]
    [InlineData(0.55f, true, 0, 0.55f)]
    [InlineData(0.55f, false, 1, 0.55f)]
    [InlineData(0f, true, 2, 0f)]
    public void EffectivePassTemporalWeight_HalvesWhenPreviewTaaActive(
        float passWeight,
        bool enableTaa,
        int quality,
        float expected)
    {
        var settings = new PreviewRenderSettings
        {
            EnablePreviewTaa = enableTaa,
            VolumetricQuality = quality,
        };

        Assert.Equal(expected, PreviewVolumetricQuality.EffectivePassTemporalWeight(passWeight, PreviewRenderSettingsSnapshot.From(settings)));
    }

    [Fact]
    public void EffectivePassTemporalWeight_DoesNotHalveWhenPreviewTaaTemporalScaleIsZero()
    {
        var settings = new PreviewRenderSettings
        {
            EnablePreviewTaa = true,
            VolumetricQuality = 1,
            PreviewTaaTemporalScale = 0f,
        };

        Assert.Equal(0.55f, PreviewVolumetricQuality.EffectivePassTemporalWeight(0.55f, PreviewRenderSettingsSnapshot.From(settings)));
    }
}
