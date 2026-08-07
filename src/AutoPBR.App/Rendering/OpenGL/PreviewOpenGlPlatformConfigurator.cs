using AutoPBR.App.Models;

using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Win32;

namespace AutoPBR.App.Rendering.OpenGL;

internal static class PreviewOpenGlPlatformConfigurator
{
    /// <summary>
    /// Syncs <see cref="PreviewOpenGlSession.RequestedDesktopGl4"/> from settings on every OS.
    /// Win32 ANGLE/WGL options are applied only on Windows; Linux configures X11 GL for soft GPUs.
    /// </summary>
    public static AppBuilder Configure(AppBuilder builder, UserSettings settings)
    {
        PreviewDesktopEglSidecar.EnsureProbed();
        SyncSessionFromSettings(settings);

        if (OperatingSystem.IsWindows())
        {
            return builder.With(CreateWin32PlatformOptions(settings));
        }

        if (OperatingSystem.IsLinux())
        {
            return builder.With(CreateX11PlatformOptions());
        }

        return builder;
    }

    /// <summary>
    /// Avalonia defaults blacklist <c>llvmpipe</c>/<c>SVGA3D</c>, which prevents
    /// <c>OpenGlControlBase</c> from initializing on cloud/CI Mesa software GL. Clear the blacklist
    /// so Linux preview can draw; heavy-feature demotion stays in capability gates.
    /// </summary>
    public static X11PlatformOptions CreateX11PlatformOptions() =>
        new()
        {
            GlxRendererBlacklist = [],
            RenderingMode =
            [
                X11RenderingMode.Glx,
                X11RenderingMode.Egl,
                X11RenderingMode.Software,
            ],
        };

    /// <summary>Updates the launch-scoped desktop GL 4 session flag without touching platform options.</summary>
    public static void SyncSessionFromSettings(UserSettings settings)
    {
        var requested = settings.PreviewUseOpenGl4;
        if (PreviewSurfaceVisibility.ShouldDemoteDesktopGl4OnCurrentOs(requested))
        {
            // Keep Avalonia OpenGL surface; native desktop sidecar is Windows (WGL) or Linux Phase 2 (EGL).
            PreviewOpenGlSession.RequestedDesktopGl4 = false;
            PreviewOpenGlSession.DesktopGl4DemotedForPlatform = true;
            return;
        }

        PreviewOpenGlSession.RequestedDesktopGl4 = requested;
        PreviewOpenGlSession.DesktopGl4DemotedForPlatform = false;
    }

    public static Win32PlatformOptions CreateWin32PlatformOptions(UserSettings settings)
    {
        // Session flag is owned by SyncSessionFromSettings / Configure; keep Windows path explicit for tests.
        PreviewOpenGlSession.RequestedDesktopGl4 = settings.PreviewUseOpenGl4;
        PreviewOpenGlSession.DesktopGl4DemotedForPlatform = false;

        var useDesktop = settings.PreviewUseOpenGl4;
        return new Win32PlatformOptions
        {
            // Keep ANGLE for the Avalonia compositor (display refresh pacing). Desktop GL 4.x preview uses a WGL sidecar.
            RenderingMode =
            [
                Win32RenderingMode.AngleEgl,
            ],
            WglProfiles = useDesktop
                ?
                [
                    new GlVersion(GlProfileType.OpenGL, 4, 6),
                    new GlVersion(GlProfileType.OpenGL, 4, 0),
                    new GlVersion(GlProfileType.OpenGL, 3, 3),
                ]
                : new Win32PlatformOptions().WglProfiles,
            CompositionMode =
            [
                Win32CompositionMode.WinUIComposition,
                Win32CompositionMode.DirectComposition,
                Win32CompositionMode.RedirectionSurface,
            ],
        };
    }
}
