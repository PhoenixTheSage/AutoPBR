using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Tests;

public class PreviewScreenSpaceAoQualityTests
{
    [Theory]
    [InlineData(PreviewAoMode.Ssao, PreviewVolumetricQuality.High, PreviewAoMode.Ssao)]
    [InlineData(PreviewAoMode.Gtao, PreviewVolumetricQuality.Low, PreviewAoMode.Gtao)]
    [InlineData(PreviewAoMode.Auto, PreviewVolumetricQuality.Low, PreviewAoMode.Ssao)]
    [InlineData(PreviewAoMode.Auto, PreviewVolumetricQuality.Medium, PreviewAoMode.Ssao)]
    [InlineData(PreviewAoMode.Auto, PreviewVolumetricQuality.High, PreviewAoMode.Gtao)]
    [InlineData(PreviewAoMode.Auto, PreviewVolumetricQuality.Cinematic, PreviewAoMode.Gtao)]
    public void ResolveTechnique_MatchesModeAndQuality(PreviewAoMode mode, int quality, PreviewAoMode expected)
    {
        Assert.Equal(expected, PreviewScreenSpaceAoQuality.ResolveTechnique(mode, quality));
    }

    [Fact]
    public void Resolve_LowUsesHalfResSsaoProfile()
    {
        var profile = PreviewScreenSpaceAoQuality.Resolve(PreviewAoMode.Auto, PreviewVolumetricQuality.Low);
        Assert.Equal(PreviewAoMode.Ssao, profile.Technique);
        Assert.Equal(0.5f, profile.ResolutionScale);
        Assert.Equal(8, profile.SsaoSamples);
        Assert.Equal(1, profile.BilateralPasses);
        Assert.False(profile.UseTemporal);
    }

    [Fact]
    public void Resolve_CinematicUsesTwoThirdsGtaoWithTemporal()
    {
        var profile = PreviewScreenSpaceAoQuality.Resolve(PreviewAoMode.Auto, PreviewVolumetricQuality.Cinematic);
        Assert.Equal(PreviewAoMode.Gtao, profile.Technique);
        Assert.Equal(2f / 3f, profile.ResolutionScale);
        Assert.Equal(6, profile.GtaoSlices);
        Assert.Equal(6, profile.GtaoSteps);
        Assert.True(profile.UseTemporal);
    }

    [Fact]
    public void Snapshot_CopiesAoFields()
    {
        var settings = new PreviewRenderSettings
        {
            EnableScreenSpaceAo = true,
            PreviewAoMode = (int)PreviewAoMode.Gtao,
            AoStrength = 0.7f,
            AoRadius = 1.2f,
            AoPower = 1.5f,
            AoDebugView = 1,
        };

        var snap = PreviewRenderSettingsSnapshot.From(settings);
        Assert.True(snap.EnableScreenSpaceAo);
        Assert.Equal((int)PreviewAoMode.Gtao, snap.PreviewAoMode);
        Assert.Equal(0.7f, snap.AoStrength);
        Assert.Equal(1.2f, snap.AoRadius);
        Assert.Equal(1.5f, snap.AoPower);
        Assert.Equal(1, snap.AoDebugView);
    }
}
