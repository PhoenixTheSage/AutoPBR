using System.Numerics;

using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Immutable CQ3 cloud-light froxel dimensions and update policy. This profile is intentionally
/// separate from the camera-aligned fog/god-ray froxel profile.
/// </summary>
public readonly record struct PreviewCloudLightCascadeProfile(
    int Width,
    int Height,
    int Depth,
    float WorldSpan,
    int UpdateIntervalFrames)
{
    public bool IsEnabled => Width > 0 && Height > 0 && Depth > 0 && WorldSpan > 0f;
    public float TexelWorldSize => IsEnabled ? WorldSpan / Width : 0f;

    public bool IsUpdateDue(int cloudFrameIndex) =>
        IsEnabled &&
        UpdateIntervalFrames > 0 &&
        Math.Abs(cloudFrameIndex % UpdateIntervalFrames) == 0;

    public string FormatDimensions() =>
        IsEnabled
            ? $"{Width}x{Height}x{Depth}@{WorldSpan:0}"
            : "disabled";
}

public readonly record struct PreviewCloudLightingCacheProfile(
    string Name,
    string Format,
    PreviewCloudLightCascadeProfile Near,
    PreviewCloudLightCascadeProfile Far,
    float NearOverlapFraction,
    int LocalConeTapCount)
{
    public bool IsEnabled => Near.IsEnabled && Far.IsEnabled;

    public string FormatDiagnostic() =>
        IsEnabled
            ? $"{Name}/{Format};near={Near.FormatDimensions()}/every-{Near.UpdateIntervalFrames};" +
              $"far={Far.FormatDimensions()}/every-{Far.UpdateIntervalFrames};" +
              $"overlap={NearOverlapFraction:0.##};localConeTaps={LocalConeTapCount}"
            : $"{Name}/disabled";
}

public static class PreviewCloudLightingCacheProfiles
{
    public const string StorageFormat = "RG16F";
    public const float NearWorldSpan = 640f;
    public const float FarWorldSpan = 2560f;
    public const float NearOverlapFraction = 0.20f;

    public static PreviewCloudLightingCacheProfile Resolve(int volumetricQuality) =>
        PreviewVolumetricQuality.Clamp(volumetricQuality) switch
        {
            PreviewVolumetricQuality.High => new PreviewCloudLightingCacheProfile(
                Name: nameof(PreviewVolumetricQuality.High),
                Format: StorageFormat,
                Near: new PreviewCloudLightCascadeProfile(
                    Width: 192,
                    Height: 192,
                    Depth: 16,
                    WorldSpan: NearWorldSpan,
                    UpdateIntervalFrames: 2),
                Far: new PreviewCloudLightCascadeProfile(
                    Width: 128,
                    Height: 128,
                    Depth: 12,
                    WorldSpan: FarWorldSpan,
                    UpdateIntervalFrames: 4),
                NearOverlapFraction,
                LocalConeTapCount: 0),
            PreviewVolumetricQuality.Cinematic => new PreviewCloudLightingCacheProfile(
                Name: nameof(PreviewVolumetricQuality.Cinematic),
                Format: StorageFormat,
                Near: new PreviewCloudLightCascadeProfile(
                    Width: 256,
                    Height: 256,
                    Depth: 24,
                    WorldSpan: NearWorldSpan,
                    UpdateIntervalFrames: 1),
                Far: new PreviewCloudLightCascadeProfile(
                    Width: 192,
                    Height: 192,
                    Depth: 16,
                    WorldSpan: FarWorldSpan,
                    UpdateIntervalFrames: 4),
                NearOverlapFraction,
                LocalConeTapCount: 2),
            _ => new PreviewCloudLightingCacheProfile(
                Name: PreviewVolumetricQuality.GetName(volumetricQuality),
                Format: "none",
                Near: default,
                Far: default,
                NearOverlapFraction: 0f,
                LocalConeTapCount: 0),
        };
}

/// <summary>
/// CQ3.5 ground-shadow publication profile. High preserves the far cascade's native
/// XY footprint; Cinematic publishes the same far footprint while combining near/far
/// cache samples through the cloud-shading overlap contract.
/// </summary>
public readonly record struct PreviewCloudGroundTransmittanceProfile(
    int Width,
    int Height,
    float WorldSpan,
    bool CombineNearAndFar)
{
    public bool IsEnabled =>
        Width > 0 &&
        Height > 0 &&
        WorldSpan > 0f;
    public Vector2 TexelSize =>
        IsEnabled
            ? new Vector2(1f / Width, 1f / Height)
            : Vector2.One;

    public string FormatDiagnostic() =>
        IsEnabled
            ? $"R16F/{Width}x{Height}@{WorldSpan:0}/" +
              (CombineNearAndFar ? "near-far-overlap" : "far-native")
            : "disabled";
}

public static class PreviewCloudGroundTransmittanceProfiles
{
    public static PreviewCloudGroundTransmittanceProfile Resolve(
        int volumetricQuality)
    {
        var cache = PreviewCloudLightingCacheProfiles.Resolve(
            volumetricQuality);
        if (!cache.IsEnabled)
        {
            return default;
        }

        return new PreviewCloudGroundTransmittanceProfile(
            cache.Far.Width,
            cache.Far.Height,
            cache.Far.WorldSpan,
            CombineNearAndFar:
                PreviewVolumetricQuality.Clamp(volumetricQuality) ==
                PreviewVolumetricQuality.Cinematic);
    }
}

/// <summary>
/// CQ3.4 view-shading controls. Each octave vector stores extinction scale, phase-eccentricity
/// scale, and energy scale in that order. These are internal render profiles rather than user
/// settings so every generator/backend observes one stable lighting contract.
/// </summary>
public readonly record struct PreviewCloudLightingShadingProfile(
    Vector3 Octave1,
    Vector3 Octave2,
    float ScatteredEnergyClamp,
    float CachedSkyVisibilityFloor,
    float GroundBounceStrength,
    float LocalConeOpticalDepthScale);

public static class PreviewCloudLightingShadingProfiles
{
    public static readonly PreviewCloudLightingShadingProfile Default = new(
        Octave1: new Vector3(0.50f, 0.50f, 0.55f),
        Octave2: new Vector3(0.25f, 0.25f, 0.30f),
        ScatteredEnergyClamp: 2.25f,
        CachedSkyVisibilityFloor: 0.18f,
        GroundBounceStrength: 0.11f,
        LocalConeOpticalDepthScale: 0.45f);
}
