using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudTemporalMomentsTests
{
    [Theory]
    [InlineData(PreviewVolumetricQuality.Low, false, 0f, 0f)]
    [InlineData(PreviewVolumetricQuality.Medium, false, 0f, 0f)]
    [InlineData(PreviewVolumetricQuality.High, true, 1.5f, 0.015f)]
    [InlineData(PreviewVolumetricQuality.Cinematic, true, 2.0f, 0.01f)]
    public void Resolve_UsesDocumentedPresetPolicy(
        int quality,
        bool enabled,
        float sigma,
        float minimumBand)
    {
        var profile = PreviewCloudTemporalMoments.Resolve(quality);

        Assert.Equal(enabled, profile.Enabled);
        Assert.Equal(sigma, profile.Sigma);
        Assert.Equal(minimumBand, profile.MinimumBand);
    }

    [Fact]
    public void Confidence_ReachesOneAfterEightAcceptedFrames()
    {
        var acceptedFrames = 0;
        Assert.Equal(0f, PreviewCloudTemporalMoments.ResolveConfidence(acceptedFrames));

        for (var frame = 1; frame <= PreviewCloudTemporalMoments.ConfidenceFrameCount; frame++)
        {
            acceptedFrames = PreviewCloudTemporalMoments.AdvanceConfidence(acceptedFrames);
            Assert.Equal(
                frame / (float)PreviewCloudTemporalMoments.ConfidenceFrameCount,
                PreviewCloudTemporalMoments.ResolveConfidence(acceptedFrames));
        }

        Assert.Equal(
            PreviewCloudTemporalMoments.ConfidenceFrameCount,
            PreviewCloudTemporalMoments.AdvanceConfidence(acceptedFrames));
        Assert.Equal(1f, PreviewCloudTemporalMoments.ResolveConfidence(int.MaxValue));
        Assert.Equal(0f, PreviewCloudTemporalMoments.ResolveConfidence(-1));
    }
}
