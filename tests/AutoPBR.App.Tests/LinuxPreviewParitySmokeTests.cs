using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

/// <summary>Linux / headless smoke — no WGL. Safe on Windows too (asserts visibility rules only).</summary>
public sealed class LinuxPreviewParitySmokeTests
{
    [Fact]
    public void PreviewSurfaces_OpenGl4OnLinux_KeepsAvaloniaVisible()
    {
        Assert.True(PreviewSurfaceVisibility.UseAvaloniaOpenGlSurface(requestedDesktopGl4: true, isWindows: false));
        Assert.False(PreviewSurfaceVisibility.UseNativeWglHost(requestedDesktopGl4: true, isWindows: false));
    }

    [Fact]
    public void PreviewSurfaces_LinuxNeverMountsNativeWglHost()
    {
        // Regression: mounting PreviewNativeWglHost on X11 crashes in X11NativeControlHost.Attachment
        // when CreateNativeControlCore cannot produce an HWND child.
        Assert.False(PreviewSurfaceVisibility.UseNativeWglHost(requestedDesktopGl4: false, isWindows: false));
        Assert.False(PreviewSurfaceVisibility.UseNativeWglHost(requestedDesktopGl4: true, isWindows: false));
    }

    [Fact]
    public void X11Options_DoNotBlacklistLlvmpipe()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var options = PreviewOpenGlPlatformConfigurator.CreateX11PlatformOptions();
        Assert.DoesNotContain(
            options.GlxRendererBlacklist,
            name => name.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreviewSurfaces_OpenGl4OnWindows_UsesNativeHost()
    {
        Assert.True(PreviewSurfaceVisibility.UseNativeWglHost(requestedDesktopGl4: true, isWindows: true));
        Assert.False(PreviewSurfaceVisibility.UseAvaloniaOpenGlSurface(requestedDesktopGl4: true, isWindows: true));
    }

    [Fact]
    public void EglSidecar_ProbeApi_DoesNotThrowOnCurrentOs()
    {
        var previous = PreviewDesktopEglSidecar.IsSupported;
        try
        {
            PreviewDesktopEglSidecar.EnsureProbed();
            _ = PreviewDesktopEglSidecar.IsSupported;
        }
        finally
        {
            PreviewDesktopEglSidecar.IsSupported = previous;
        }
    }
}
