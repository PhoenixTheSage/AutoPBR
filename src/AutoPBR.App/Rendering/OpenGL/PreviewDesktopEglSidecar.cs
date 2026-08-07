namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Linux desktop OpenGL sidecar (EGL). <see cref="IsSupported"/> is probed once at app configure time.
/// </summary>
internal static class PreviewDesktopEglSidecar
{
    private static int _probed;

    /// <summary>
    /// True when Linux can create a dedicated EGL desktop-GL context and present via async PBO.
    /// </summary>
    public static bool IsSupported { get; internal set; }

    /// <summary>Probes EGL once on Linux; no-op on other OSes.</summary>
    public static void EnsureProbed()
    {
        if (Interlocked.Exchange(ref _probed, 1) != 0)
        {
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            IsSupported = false;
            return;
        }

        IsSupported = PreviewDesktopEglContext.TryProbeSupported();
    }
}
