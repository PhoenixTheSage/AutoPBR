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
