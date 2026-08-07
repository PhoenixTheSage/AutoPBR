using Avalonia.Media;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Bundled OFL monospace used for HUD overlays and debug UI (cross-platform).</summary>
internal static class PreviewMonoFont
{
    public const string ResourcePath = "avares://AutoPBR.App/Assets/Fonts/CascadiaMono.ttf#Cascadia Mono";

    /// <summary>Primary family plus system fallbacks.</summary>
    public const string FontFamilyCss = ResourcePath + ", Cascadia Mono, Consolas, Courier New, monospace";

    public static FontFamily Family { get; } = new(FontFamilyCss);
}
