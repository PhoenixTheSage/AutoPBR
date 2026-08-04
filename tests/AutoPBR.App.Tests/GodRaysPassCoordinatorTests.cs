using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GodRaysPassCoordinatorTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(683, 684)]
    [InlineData(1025, 1026)]
    [InlineData(1366, 1366)]
    public void AlignEvenCaptureDimension_RoundsOddSizesUp(int input, int expected)
    {
        Assert.Equal(expected, GodRaysPassCoordinator.AlignEvenCaptureDimension(input));
    }

    [Fact]
    public void ResolveSceneCaptureSize_KeepsEvenDimensionsForOddViewportAt1_5x()
    {
        var frame = CreateFrame(vw: 862, vh: 683, taaMode: 0);

        GodRaysPassCoordinator.ResolveSceneCaptureSize(
            in frame,
            static _ => true,
            static s => PreviewVolumetricQuality.ResolvePreviewTaa(s.VolumetricQuality, s.PreviewTaaMode),
            out var captureW,
            out var captureH,
            out var captureScale);

        Assert.Equal(1.5f, captureScale);
        Assert.Equal(0, captureW & 1);
        Assert.Equal(0, captureH & 1);
        Assert.True(captureW >= (int)MathF.Ceiling(862 * 1.5f));
        Assert.True(captureH >= (int)MathF.Ceiling(683 * 1.5f));
    }

    [Fact]
    public void ResolveSceneCaptureSize_EdgeAaUses1_5xWithEvenDimensions()
    {
        var frame = CreateFrame(vw: 862, vh: 683, taaMode: 2);

        GodRaysPassCoordinator.ResolveSceneCaptureSize(
            in frame,
            static _ => true,
            static s => PreviewVolumetricQuality.ResolvePreviewTaa(s.VolumetricQuality, s.PreviewTaaMode),
            out var captureW,
            out var captureH,
            out var captureScale);

        Assert.Equal(1.5f, captureScale);
        Assert.Equal(0, captureW & 1);
        Assert.Equal(0, captureH & 1);
        Assert.Equal(GodRaysPassCoordinator.AlignEvenCaptureDimension((int)MathF.Ceiling(862 * 1.5f)), captureW);
        Assert.Equal(GodRaysPassCoordinator.AlignEvenCaptureDimension((int)MathF.Ceiling(683 * 1.5f)), captureH);
    }

    private static GlRenderFrame CreateFrame(int vw, int vh, int taaMode)
    {
        var frame = new GlRenderFrame();
        frame.Vw = vw;
        frame.Vh = vh;
        frame.Settings = new PreviewRenderSettingsSnapshot
        {
            EnablePreviewTaa = true,
            PreviewTaaMode = taaMode,
            VolumetricQuality = PreviewVolumetricQuality.High,
            PreviewTaaTemporalScale = 1f,
            PreviewTaaJitterScale = 1f,
            PreviewTaaEdgeBlendScale = 1f,
            PreviewTaaFxaaStrengthScale = 1f,
            PreviewTaaSourceFilterScale = 1f,
        };
        return frame;
    }
}
