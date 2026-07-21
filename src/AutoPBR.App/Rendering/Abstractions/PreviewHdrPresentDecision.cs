namespace AutoPBR.App.Rendering.Abstractions;

/// <summary>Resolved HDR present policy for one preview frame / settings push.</summary>
public readonly record struct PreviewHdrPresentDecision(
    PreviewHdrPresentMode PresentMode,
    PreviewHdrFallbackReason FallbackReason,
    bool DisplaySupportsHdr,
    float PeakNits,
    float PaperWhiteNits)
{
    public bool HdrPresentActive => PresentMode == PreviewHdrPresentMode.Hdr;

    public static PreviewHdrPresentDecision Sdr(
        PreviewHdrFallbackReason reason,
        bool displaySupportsHdr,
        float peakNits,
        float paperWhiteNits) =>
        new(PreviewHdrPresentMode.Sdr, reason, displaySupportsHdr, peakNits, paperWhiteNits);
}
