using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CQ1.6 variance-clipping policy. Moment storage is still capability-selected by the GL
/// render-format profile; this type owns only preset behavior and deterministic confidence.
/// </summary>
internal static class PreviewCloudTemporalMoments
{
    public const int ConfidenceFrameCount = 8;

    internal readonly record struct Profile(
        bool Enabled,
        float Sigma,
        float MinimumBand);

    public static Profile Resolve(int cloudQuality) =>
        PreviewVolumetricQuality.Clamp(cloudQuality) switch
        {
            PreviewVolumetricQuality.High => new Profile(
                Enabled: true,
                Sigma: 1.5f,
                MinimumBand: 0.015f),
            PreviewVolumetricQuality.Cinematic => new Profile(
                Enabled: true,
                Sigma: 2.0f,
                MinimumBand: 0.01f),
            _ => new Profile(
                Enabled: false,
                Sigma: 0f,
                MinimumBand: 0f),
        };

    public static int AdvanceConfidence(int acceptedFrames) =>
        Math.Clamp(acceptedFrames + 1, 0, ConfidenceFrameCount);

    public static float ResolveConfidence(int acceptedFrames) =>
        Math.Clamp(acceptedFrames, 0, ConfidenceFrameCount) / (float)ConfidenceFrameCount;
}
