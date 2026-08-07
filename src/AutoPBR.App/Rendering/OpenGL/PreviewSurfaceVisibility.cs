namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Decides which preview host surfaces are visible. Never hides both (Linux + OpenGL4 must keep Avalonia GL).
/// </summary>
internal static class PreviewSurfaceVisibility
{
    /// <summary>
    /// Native WGL child host is Windows-only and only when desktop GL 4 was requested at launch.
    /// </summary>
    public static bool UseNativeWglHost(bool requestedDesktopGl4, bool isWindows) =>
        requestedDesktopGl4 && isWindows;

    /// <summary>
    /// Avalonia <c>OpenGlControlBase</c> surface: always on when native WGL is not the active host.
    /// On Linux (Phase 1), desktop GL 4 still uses this path until the EGL sidecar ships.
    /// </summary>
    public static bool UseAvaloniaOpenGlSurface(bool requestedDesktopGl4, bool isWindows) =>
        !UseNativeWglHost(requestedDesktopGl4, isWindows);

    /// <summary>
    /// Until the Linux EGL sidecar is available, demote desktop GL 4 requests to the Avalonia path on non-Windows.
    /// Phase 2 clears this demotion when <see cref="PreviewDesktopEglSidecar.IsSupported"/> is true.
    /// </summary>
    public static bool ShouldDemoteDesktopGl4OnCurrentOs(bool requestedDesktopGl4) =>
        requestedDesktopGl4 &&
        !OperatingSystem.IsWindows() &&
        !PreviewDesktopEglSidecar.IsSupported;
}
