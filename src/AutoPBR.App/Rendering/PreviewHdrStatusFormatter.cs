using System.Globalization;

using AutoPBR.App.Lang;
using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering;

/// <summary>Formats HDR present status for Settings / preview chrome.</summary>
internal static class PreviewHdrStatusFormatter
{
    public static string Format(in PreviewHdrPresentDecision decision)
    {
        if (decision.HdrPresentActive)
        {
            var peak = decision.PeakNits > 0f
                ? string.Format(CultureInfo.CurrentCulture, "~{0:0} nits", decision.PeakNits)
                : "scRGB";
            return string.Format(
                CultureInfo.CurrentCulture,
                LocalizedStrings.PreviewHdrStatusActive,
                peak);
        }

        return decision.FallbackReason switch
        {
            PreviewHdrFallbackReason.UserForcedSdr => LocalizedStrings.PreviewHdrStatusForcedSdr,
            PreviewHdrFallbackReason.DisplayNotHdr => LocalizedStrings.PreviewHdrStatusDisplayNotHdr,
            PreviewHdrFallbackReason.NativeWglRequired => LocalizedStrings.PreviewHdrStatusOpenGl4Required,
            PreviewHdrFallbackReason.PlatformUnsupported => LocalizedStrings.PreviewHdrStatusPlatformUnsupported,
            PreviewHdrFallbackReason.PresentFailed => LocalizedStrings.PreviewHdrStatusPresentFailed,
            _ => LocalizedStrings.PreviewHdrStatusForcedSdr
        };
    }
}
