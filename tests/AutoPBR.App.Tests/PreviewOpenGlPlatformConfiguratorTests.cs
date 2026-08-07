using AutoPBR.App.Models;
using AutoPBR.App.Rendering.OpenGL;

using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Win32;

namespace AutoPBR.App.Tests;

public sealed class PreviewOpenGlPlatformConfiguratorTests
{
    [Fact]
    public void CreateWin32PlatformOptions_DefaultsToAngleOnly()
    {
        PreviewOpenGlSession.RequestedDesktopGl4 = false;
        PreviewOpenGlSession.DesktopGl4DemotedForPlatform = false;

        var options = PreviewOpenGlPlatformConfigurator.CreateWin32PlatformOptions(new UserSettings());

        Assert.False(PreviewOpenGlSession.RequestedDesktopGl4);
        Assert.Equal([Win32RenderingMode.AngleEgl], options.RenderingMode);
    }

    [Fact]
    public void CreateWin32PlatformOptions_DesktopKeepsAngleCompositorWithWglProfilesForSidecar()
    {
        var options = PreviewOpenGlPlatformConfigurator.CreateWin32PlatformOptions(new UserSettings
        {
            PreviewUseOpenGl4 = true,
        });

        Assert.True(PreviewOpenGlSession.RequestedDesktopGl4);
        Assert.False(PreviewOpenGlSession.DesktopGl4DemotedForPlatform);
        Assert.Equal([Win32RenderingMode.AngleEgl], options.RenderingMode);
        Assert.Equal(GlProfileType.OpenGL, options.WglProfiles[0].Type);
        Assert.Equal(4, options.WglProfiles[0].Major);
        Assert.Equal(6, options.WglProfiles[0].Minor);
        Assert.Equal(4, options.WglProfiles[1].Major);
        Assert.Equal(0, options.WglProfiles[1].Minor);
        Assert.Equal(3, options.WglProfiles[2].Major);
        Assert.Equal(3, options.WglProfiles[2].Minor);
    }

    [Fact]
    public void SyncSessionFromSettings_Off_ClearsDesktopFlag()
    {
        PreviewOpenGlSession.RequestedDesktopGl4 = true;
        PreviewOpenGlPlatformConfigurator.SyncSessionFromSettings(new UserSettings { PreviewUseOpenGl4 = false });
        Assert.False(PreviewOpenGlSession.RequestedDesktopGl4);
        Assert.False(PreviewOpenGlSession.DesktopGl4DemotedForPlatform);
    }

    [Fact]
    public void SyncSessionFromSettings_OpenGl4_OnWindows_RequestsDesktop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        PreviewOpenGlPlatformConfigurator.SyncSessionFromSettings(new UserSettings { PreviewUseOpenGl4 = true });
        Assert.True(PreviewOpenGlSession.RequestedDesktopGl4);
        Assert.False(PreviewOpenGlSession.DesktopGl4DemotedForPlatform);
    }

    [Fact]
    public void SyncSessionFromSettings_OpenGl4_OnNonWindows_DemotesUntilEglSidecar()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var previous = PreviewDesktopEglSidecar.IsSupported;
        try
        {
            PreviewDesktopEglSidecar.IsSupported = false;
            PreviewOpenGlPlatformConfigurator.SyncSessionFromSettings(new UserSettings { PreviewUseOpenGl4 = true });
            Assert.False(PreviewOpenGlSession.RequestedDesktopGl4);
            Assert.True(PreviewOpenGlSession.DesktopGl4DemotedForPlatform);
        }
        finally
        {
            PreviewDesktopEglSidecar.IsSupported = previous;
        }
    }

    [Fact]
    public void Configure_ReturnsSameBuilder_OnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var builder = AppBuilder.Configure<AutoPBR.App.App>().UsePlatformDetect();
        var configured = PreviewOpenGlPlatformConfigurator.Configure(builder, new UserSettings());
        Assert.Same(builder, configured);
    }

    [Fact]
    public void CreateX11PlatformOptions_AllowsLlvmpipeForSoftGpuPreview()
    {
        var options = PreviewOpenGlPlatformConfigurator.CreateX11PlatformOptions();
        Assert.Empty(options.GlxRendererBlacklist);
        Assert.Contains(X11RenderingMode.Glx, options.RenderingMode);
        Assert.Contains(X11RenderingMode.Software, options.RenderingMode);
    }
}

public sealed class PreviewSurfaceVisibilityTests
{
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, true)]
    public void Surfaces_NeverBothHidden(
        bool requestedDesktopGl4,
        bool isWindows,
        bool expectNativeWgl,
        bool expectAvalonia)
    {
        Assert.Equal(expectNativeWgl, PreviewSurfaceVisibility.UseNativeWglHost(requestedDesktopGl4, isWindows));
        Assert.Equal(expectAvalonia, PreviewSurfaceVisibility.UseAvaloniaOpenGlSurface(requestedDesktopGl4, isWindows));
        Assert.True(
            PreviewSurfaceVisibility.UseNativeWglHost(requestedDesktopGl4, isWindows) ||
            PreviewSurfaceVisibility.UseAvaloniaOpenGlSurface(requestedDesktopGl4, isWindows));
    }

    [Fact]
    public void ShouldDemoteDesktopGl4_OnNonWindows_WhenEglUnsupported()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.False(PreviewSurfaceVisibility.ShouldDemoteDesktopGl4OnCurrentOs(true));
            return;
        }

        var previous = PreviewDesktopEglSidecar.IsSupported;
        try
        {
            PreviewDesktopEglSidecar.IsSupported = false;
            Assert.True(PreviewSurfaceVisibility.ShouldDemoteDesktopGl4OnCurrentOs(true));
            Assert.False(PreviewSurfaceVisibility.ShouldDemoteDesktopGl4OnCurrentOs(false));

            PreviewDesktopEglSidecar.IsSupported = true;
            Assert.False(PreviewSurfaceVisibility.ShouldDemoteDesktopGl4OnCurrentOs(true));
        }
        finally
        {
            PreviewDesktopEglSidecar.IsSupported = previous;
        }
    }
}
