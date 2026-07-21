using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering;

/// <summary>Resolves Auto/SDR preference against display + native WGL capability.</summary>
internal static class PreviewHdrPresentPolicy
{
    public const float DefaultPaperWhiteNits = 200f;
    public const float MinPaperWhiteNits = 80f;
    public const float MaxPaperWhiteNits = 2000f;

    public static float ClampPaperWhiteNits(float nits) =>
        Math.Clamp(nits, MinPaperWhiteNits, MaxPaperWhiteNits);

    public static PreviewHdrMode ParseMode(string? value) =>
        string.Equals(value, "Sdr", StringComparison.OrdinalIgnoreCase)
            ? PreviewHdrMode.Sdr
            : PreviewHdrMode.Auto;

    public static string FormatMode(PreviewHdrMode mode) =>
        mode == PreviewHdrMode.Sdr ? "Sdr" : "Auto";

    public static PreviewHdrPresentDecision Resolve(
        PreviewHdrMode mode,
        in PreviewHdrDisplayInfo display,
        bool nativeWglActive,
        float paperWhiteNits,
        bool presentPathFailed = false)
    {
        var paper = ClampPaperWhiteNits(paperWhiteNits);
        var peak = display.MaxLuminanceNits > 0f ? display.MaxLuminanceNits : 0f;

        if (!OperatingSystem.IsWindows())
        {
            return PreviewHdrPresentDecision.Sdr(
                PreviewHdrFallbackReason.PlatformUnsupported,
                displaySupportsHdr: false,
                peakNits: 0f,
                paperWhiteNits: paper);
        }

        if (mode == PreviewHdrMode.Sdr)
        {
            return PreviewHdrPresentDecision.Sdr(
                PreviewHdrFallbackReason.UserForcedSdr,
                display.SupportsHdr,
                peak,
                paper);
        }

        if (presentPathFailed)
        {
            return PreviewHdrPresentDecision.Sdr(
                PreviewHdrFallbackReason.PresentFailed,
                display.SupportsHdr,
                peak,
                paper);
        }

        if (!display.SupportsHdr)
        {
            return PreviewHdrPresentDecision.Sdr(
                PreviewHdrFallbackReason.DisplayNotHdr,
                displaySupportsHdr: false,
                peakNits: peak,
                paperWhiteNits: paper);
        }

        if (!nativeWglActive)
        {
            return PreviewHdrPresentDecision.Sdr(
                PreviewHdrFallbackReason.NativeWglRequired,
                displaySupportsHdr: true,
                peakNits: peak,
                paperWhiteNits: paper);
        }

        return new PreviewHdrPresentDecision(
            PreviewHdrPresentMode.Hdr,
            PreviewHdrFallbackReason.None,
            DisplaySupportsHdr: true,
            PeakNits: peak,
            PaperWhiteNits: paper);
    }
}
