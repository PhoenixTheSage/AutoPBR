namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>Startup OpenGL profile request applied before the preview context is created.</summary>
internal static class PreviewOpenGlSession
{
    /// <summary>True when desktop OpenGL 4.x was requested and accepted for this OS at launch.</summary>
    public static bool RequestedDesktopGl4 { get; set; }

    /// <summary>
    /// True when the user requested OpenGL 4.x but the current OS demoted to Avalonia OpenGL
    /// (Linux Phase 1 before EGL sidecar, or unsupported platforms).
    /// </summary>
    public static bool DesktopGl4DemotedForPlatform { get; set; }
}
