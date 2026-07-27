using AutoPBR.App.Rendering.Abstractions;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Render-target contract shared by the cloud trace, temporal reconstruction and upsample passes.
/// The compatibility profile preserves the GLES/ANGLE byte packing; the desktop profile keeps
/// radiance/opacity in FP16 and representative distance/type as direct FP32 values.
/// </summary>
internal readonly record struct GlCloudRenderFormatProfile(
    string Name,
    InternalFormat ColorInternalFormat,
    PixelFormat ColorPixelFormat,
    PixelType ColorPixelType,
    InternalFormat DataInternalFormat,
    PixelFormat DataPixelFormat,
    PixelType DataPixelType,
    bool UsesDirectMetadata,
    InternalFormat MomentInternalFormat,
    PixelFormat MomentPixelFormat,
    PixelType MomentPixelType,
    bool UsesTemporalMoments)
{
    public const string CompatibilityName = "packed-rgba8";
    public const string DesktopFpName = "rgba16f-rg32f";
    public const string DesktopFpMomentsName = "rgba16f-rg32f-rg16f";

    public static GlCloudRenderFormatProfile Compatibility { get; } = new(
        CompatibilityName,
        InternalFormat.Rgba8,
        PixelFormat.Rgba,
        PixelType.UnsignedByte,
        InternalFormat.Rgba8,
        PixelFormat.Rgba,
        PixelType.UnsignedByte,
        UsesDirectMetadata: false,
        InternalFormat.RG16f,
        PixelFormat.RG,
        PixelType.HalfFloat,
        UsesTemporalMoments: false);

    public static GlCloudRenderFormatProfile DesktopFloatingPoint { get; } = new(
        DesktopFpName,
        InternalFormat.Rgba16f,
        PixelFormat.Rgba,
        PixelType.HalfFloat,
        InternalFormat.RG32f,
        PixelFormat.RG,
        PixelType.Float,
        UsesDirectMetadata: true,
        InternalFormat.RG16f,
        PixelFormat.RG,
        PixelType.HalfFloat,
        UsesTemporalMoments: false);

    public static GlCloudRenderFormatProfile DesktopFloatingPointMoments { get; } =
        DesktopFloatingPoint with
        {
            Name = DesktopFpMomentsName,
            UsesTemporalMoments = true,
        };

    public static GlCloudRenderFormatProfile Select(
        PreviewGlCapabilities? capabilities,
        int volumetricQuality) =>
        PreviewVolumetricQuality.Clamp(volumetricQuality) > PreviewVolumetricQuality.Low &&
        capabilities?.CanUseFloatingPointCloudTargets == true
            ? PreviewVolumetricQuality.Clamp(volumetricQuality) >= PreviewVolumetricQuality.High &&
              capabilities.CanUseCloudTemporalMoments
                ? DesktopFloatingPointMoments
                : DesktopFloatingPoint
            : Compatibility;

    public string DiagnosticLabel => UsesTemporalMoments
        ? "RGBA16F working radiance/opacity + direct RG32F distance/type + RG16F temporal moments"
        : UsesDirectMetadata
        ? "RGBA16F working radiance/opacity + direct RG32F distance/type; moments disabled"
        : "packed RGBA8 radiance/opacity + distance/type";
}
