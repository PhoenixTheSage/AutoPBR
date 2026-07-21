namespace AutoPBR.App.Rendering.Abstractions;

/// <summary>User preference for preview HDR presentation.</summary>
public enum PreviewHdrMode
{
    /// <summary>Use HDR when the display and native WGL path both support it.</summary>
    Auto = 0,

    /// <summary>Always present as SDR (ACES → sRGB8).</summary>
    Sdr = 1
}
