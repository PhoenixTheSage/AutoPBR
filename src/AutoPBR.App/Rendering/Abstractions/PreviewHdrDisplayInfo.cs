namespace AutoPBR.App.Rendering.Abstractions;

/// <summary>HDR capability of the monitor under a preview HWND.</summary>
public readonly record struct PreviewHdrDisplayInfo(
    bool SupportsHdr,
    float MaxLuminanceNits,
    float MaxFullFrameLuminanceNits,
    int BitsPerColor)
{
    public static PreviewHdrDisplayInfo Unsupported { get; } = new(false, 0f, 0f, 0);
}
