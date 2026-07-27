using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudTraceSizingTests
{
    [Theory]
    [InlineData(PreviewVolumetricQuality.Low, 862, 683, 431, 341)]
    [InlineData(PreviewVolumetricQuality.Medium, 575, 455, 287, 227)]
    [InlineData(PreviewVolumetricQuality.High, 3, 3, 1, 1)]
    public void CompatibilityThroughHigh_RetainsHalfResolution(
        int quality,
        int viewportWidth,
        int viewportHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var size = PreviewCloudTraceSizing.Resolve(viewportWidth, viewportHeight, quality);

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
        Assert.Equal(0.5f, size.Scale);
    }

    [Theory]
    [InlineData(862, 683, 576, 456)]
    [InlineData(575, 455, 384, 304)]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(1919, 1079, 1280, 720)]
    [InlineData(3, 3, 2, 2)]
    [InlineData(1, 1, 2, 2)]
    public void Cinematic_UsesEvenRoundedTwoThirdsResolution(
        int viewportWidth,
        int viewportHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var size = PreviewCloudTraceSizing.Resolve(
            viewportWidth,
            viewportHeight,
            PreviewVolumetricQuality.Cinematic);

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
        Assert.Equal(0, size.Width & 1);
        Assert.Equal(0, size.Height & 1);
        Assert.Equal(2f / 3f, size.Scale);
    }

    [Fact]
    public void OutOfRangeQuality_UsesClampedCinematicPolicy()
    {
        Assert.Equal(
            PreviewCloudTraceSizing.Resolve(101, 99, PreviewVolumetricQuality.Cinematic),
            PreviewCloudTraceSizing.Resolve(101, 99, int.MaxValue));
    }

    [Fact]
    public void Backend_TracksFullViewportAndResolvedTraceDimensions()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.VolumetricClouds.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("PreviewCloudTraceSizing.Resolve(", source, StringComparison.Ordinal);
        Assert.Contains("_cloudHistoryViewportW != frame.Vw", source, StringComparison.Ordinal);
        Assert.Contains("_cloudHistoryViewportH != frame.Vh", source, StringComparison.Ordinal);
        Assert.Contains("_cloudHistoryW != w || _cloudHistoryH != h", source, StringComparison.Ordinal);
        Assert.Contains("InvalidateCloudTemporalHistory()", source, StringComparison.Ordinal);
        Assert.Contains("trace={traceSize.Width}x{traceSize.Height}@{traceSize.Scale:0.###}", source,
            StringComparison.Ordinal);
    }
}
