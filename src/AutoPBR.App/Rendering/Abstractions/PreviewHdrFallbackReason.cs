namespace AutoPBR.App.Rendering.Abstractions;

/// <summary>Why HDR present was not selected (or empty when HDR is active).</summary>
public enum PreviewHdrFallbackReason
{
    None = 0,
    UserForcedSdr = 1,
    DisplayNotHdr = 2,
    NativeWglRequired = 3,
    PlatformUnsupported = 4,
    PresentFailed = 5
}
