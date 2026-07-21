using AutoPBR.App.Rendering;
using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Tests;

public sealed class PreviewHdrPresentPolicyTests
{
    private static readonly PreviewHdrDisplayInfo HdrDisplay = new(
        SupportsHdr: true,
        MaxLuminanceNits: 1000f,
        MaxFullFrameLuminanceNits: 800f,
        BitsPerColor: 10);

    [Fact]
    public void Force_Sdr_always_selects_sdr()
    {
        var decision = PreviewHdrPresentPolicy.Resolve(
            PreviewHdrMode.Sdr,
            HdrDisplay,
            nativeWglActive: true,
            paperWhiteNits: 80f);

        Assert.Equal(PreviewHdrPresentMode.Sdr, decision.PresentMode);
        Assert.Equal(PreviewHdrFallbackReason.UserForcedSdr, decision.FallbackReason);
        Assert.True(decision.DisplaySupportsHdr);
    }

    [Fact]
    public void Auto_with_hdr_display_and_native_wgl_selects_hdr_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var decision = PreviewHdrPresentPolicy.Resolve(
            PreviewHdrMode.Auto,
            HdrDisplay,
            nativeWglActive: true,
            paperWhiteNits: 120f);

        Assert.True(decision.HdrPresentActive);
        Assert.Equal(PreviewHdrFallbackReason.None, decision.FallbackReason);
        Assert.Equal(120f, decision.PaperWhiteNits);
        Assert.Equal(1000f, decision.PeakNits);
    }

    [Fact]
    public void Auto_without_native_wgl_falls_back_with_opengl4_reason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var decision = PreviewHdrPresentPolicy.Resolve(
            PreviewHdrMode.Auto,
            HdrDisplay,
            nativeWglActive: false,
            paperWhiteNits: 80f);

        Assert.False(decision.HdrPresentActive);
        Assert.Equal(PreviewHdrFallbackReason.NativeWglRequired, decision.FallbackReason);
    }

    [Fact]
    public void Auto_without_hdr_display_falls_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var decision = PreviewHdrPresentPolicy.Resolve(
            PreviewHdrMode.Auto,
            PreviewHdrDisplayInfo.Unsupported,
            nativeWglActive: true,
            paperWhiteNits: 80f);

        Assert.False(decision.HdrPresentActive);
        Assert.Equal(PreviewHdrFallbackReason.DisplayNotHdr, decision.FallbackReason);
    }

    [Fact]
    public void Present_path_failure_forces_sdr_fallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var decision = PreviewHdrPresentPolicy.Resolve(
            PreviewHdrMode.Auto,
            HdrDisplay,
            nativeWglActive: true,
            paperWhiteNits: 80f,
            presentPathFailed: true);

        Assert.False(decision.HdrPresentActive);
        Assert.Equal(PreviewHdrFallbackReason.PresentFailed, decision.FallbackReason);
    }

    [Theory]
    [InlineData(null, PreviewHdrMode.Auto)]
    [InlineData("", PreviewHdrMode.Auto)]
    [InlineData("Auto", PreviewHdrMode.Auto)]
    [InlineData("Sdr", PreviewHdrMode.Sdr)]
    [InlineData("sdr", PreviewHdrMode.Sdr)]
    public void ParseMode_maps_persisted_strings(string? value, PreviewHdrMode expected) =>
        Assert.Equal(expected, PreviewHdrPresentPolicy.ParseMode(value));

    [Theory]
    [InlineData(79f, 80f)]
    [InlineData(80f, 80f)]
    [InlineData(200f, 200f)]
    [InlineData(2000f, 2000f)]
    [InlineData(2500f, 2000f)]
    public void ClampPaperWhiteNits_respects_range(float input, float expected) =>
        Assert.Equal(expected, PreviewHdrPresentPolicy.ClampPaperWhiteNits(input));
}
