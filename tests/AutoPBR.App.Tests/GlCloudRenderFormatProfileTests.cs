using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlCloudRenderFormatProfileTests
{
    [Fact]
    public void CompatibilityProfile_PreservesPackedRgba8Contract()
    {
        var profile = GlCloudRenderFormatProfile.Compatibility;

        Assert.Equal(GlCloudRenderFormatProfile.CompatibilityName, profile.Name);
        Assert.Equal(InternalFormat.Rgba8, profile.ColorInternalFormat);
        Assert.Equal(PixelType.UnsignedByte, profile.ColorPixelType);
        Assert.Equal(InternalFormat.Rgba8, profile.DataInternalFormat);
        Assert.Equal(PixelFormat.Rgba, profile.DataPixelFormat);
        Assert.False(profile.UsesDirectMetadata);
        Assert.False(profile.UsesTemporalMoments);
    }

    [Fact]
    public void DesktopProfile_UsesFp16ColorAndDirectFp32Metadata()
    {
        var profile = GlCloudRenderFormatProfile.DesktopFloatingPoint;

        Assert.Equal(GlCloudRenderFormatProfile.DesktopFpName, profile.Name);
        Assert.Equal(InternalFormat.Rgba16f, profile.ColorInternalFormat);
        Assert.Equal(PixelType.HalfFloat, profile.ColorPixelType);
        Assert.Equal(InternalFormat.RG32f, profile.DataInternalFormat);
        Assert.Equal(PixelFormat.RG, profile.DataPixelFormat);
        Assert.Equal(PixelType.Float, profile.DataPixelType);
        Assert.True(profile.UsesDirectMetadata);
        Assert.False(profile.UsesTemporalMoments);
    }

    [Fact]
    public void DesktopMomentProfile_AddsRg16fThirdAttachment()
    {
        var profile = GlCloudRenderFormatProfile.DesktopFloatingPointMoments;

        Assert.Equal(GlCloudRenderFormatProfile.DesktopFpMomentsName, profile.Name);
        Assert.True(profile.UsesDirectMetadata);
        Assert.True(profile.UsesTemporalMoments);
        Assert.Equal(InternalFormat.RG16f, profile.MomentInternalFormat);
        Assert.Equal(PixelFormat.RG, profile.MomentPixelFormat);
        Assert.Equal(PixelType.HalfFloat, profile.MomentPixelType);
    }

    [Fact]
    public void Selector_UsesCompatibilityForGlesAndFloatingPointForDesktop()
    {
        var gles = PreviewGlCapabilities.FromStrings(
            "OpenGL ES 3.0", "Vendor", "ANGLE", string.Empty, forceOpenGlEs: true);
        var desktop = PreviewGlCapabilities.FromStrings(
            "3.3.0", "Vendor", "Renderer", string.Empty, forceOpenGlEs: false);

        Assert.Equal(
            GlCloudRenderFormatProfile.Compatibility,
            GlCloudRenderFormatProfile.Select(gles, PreviewVolumetricQuality.Cinematic));
        Assert.Equal(
            GlCloudRenderFormatProfile.Compatibility,
            GlCloudRenderFormatProfile.Select(desktop, PreviewVolumetricQuality.Low));
        Assert.Equal(
            GlCloudRenderFormatProfile.DesktopFloatingPointMoments,
            GlCloudRenderFormatProfile.Select(desktop, PreviewVolumetricQuality.Cinematic));
        Assert.Equal(
            GlCloudRenderFormatProfile.DesktopFloatingPoint,
            GlCloudRenderFormatProfile.Select(desktop, PreviewVolumetricQuality.Medium));
        Assert.Equal(
            GlCloudRenderFormatProfile.Compatibility,
            GlCloudRenderFormatProfile.Select(
                capabilities: null,
                PreviewVolumetricQuality.Cinematic));
    }

    [Fact]
    public void Selector_StepsHighDownToFpWithoutMomentsWhenThreeDrawBuffersAreUnavailable()
    {
        var limited = PreviewGlCapabilities.FromStrings(
            "3.3.0",
            "Vendor",
            "Renderer",
            string.Empty,
            forceOpenGlEs: false,
            maxColorAttachments: 8,
            maxDrawBuffers: 2);

        Assert.False(limited.CanUseCloudTemporalMoments);
        Assert.Equal(
            GlCloudRenderFormatProfile.DesktopFloatingPoint,
            GlCloudRenderFormatProfile.Select(limited, PreviewVolumetricQuality.High));
    }

}
