namespace AutoPBR.App.Rendering.Abstractions;

/// <summary>Volumetric effect cost profiles for froxel god rays and clouds.</summary>
public static class PreviewVolumetricQuality
{
    public const int Low = 0;
    public const int Medium = 1;
    public const int High = 2;
    public const int Cinematic = 3;
    public const int Minimum = Low;
    public const int Maximum = Cinematic;

    public readonly record struct Profile(
        int FroxelDivisor,
        int FroxelMinSize,
        int FroxelSlices,
        float FroxelDepthExp,
        float FroxelTemporal3DWeight,
        float CloudTemporalWeight,
        int CloudQuality,
        float VolumeIntegrateTemporalWeight,
        float UpsampleTemporalWeight,
        float PreviewTaaWeight,
        float PreviewTaaJitterScale)
    {
        public int ResolveFroxelWidth(int viewportWidth) =>
            Math.Max(FroxelMinSize, viewportWidth / FroxelDivisor);

        public int ResolveFroxelHeight(int viewportHeight) =>
            Math.Max(FroxelMinSize, viewportHeight / FroxelDivisor);
    }

    public readonly record struct TaaProfile(
        float TemporalWeight,
        float JitterScale,
        float StableTemporalBoost,
        float MaxStableTemporal,
        float SharpenStrength,
        float DepthEdgeHistoryFloor,
        float EdgeAaBlend,
        float SourceFilterStrength,
        float SilhouetteHistoryWeight,
        float FxaaEdgeStrength);

    public static int Clamp(int quality) => Math.Clamp(quality, Minimum, Maximum);

    public static string GetName(int quality) =>
        Clamp(quality) switch
        {
            Low => nameof(Low),
            High => nameof(High),
            Cinematic => nameof(Cinematic),
            _ => nameof(Medium),
        };

    public static Profile Resolve(int quality) =>
        Clamp(quality) switch
        {
            Low => new Profile(FroxelDivisor: 8, FroxelMinSize: 24, FroxelSlices: 12, FroxelDepthExp: 0f,
                FroxelTemporal3DWeight: 0f, CloudTemporalWeight: 0f, CloudQuality: Low,
                VolumeIntegrateTemporalWeight: 0f, UpsampleTemporalWeight: 0f,
                PreviewTaaWeight: 0f, PreviewTaaJitterScale: 0f),
            High => new Profile(FroxelDivisor: 3, FroxelMinSize: 48, FroxelSlices: 24, FroxelDepthExp: 4.2f,
                FroxelTemporal3DWeight: 0.38f, CloudTemporalWeight: 0.72f, CloudQuality: High,
                VolumeIntegrateTemporalWeight: 0.42f, UpsampleTemporalWeight: 0.55f,
                PreviewTaaWeight: 0.84f, PreviewTaaJitterScale: 1.0f),
            Cinematic => new Profile(
                // CQ1 keeps fog/god-ray froxels at the accepted High cost. Cinematic raises only
                // cloud quality until dedicated CQ3 profiles are implemented.
                FroxelDivisor: 3, FroxelMinSize: 48, FroxelSlices: 24, FroxelDepthExp: 4.2f,
                FroxelTemporal3DWeight: 0.38f, CloudTemporalWeight: 0.84f, CloudQuality: Cinematic,
                VolumeIntegrateTemporalWeight: 0.42f, UpsampleTemporalWeight: 0.55f,
                PreviewTaaWeight: 0.84f, PreviewTaaJitterScale: 1.0f),
            _ => new Profile(FroxelDivisor: 4, FroxelMinSize: 32, FroxelSlices: 20, FroxelDepthExp: 2.8f,
                FroxelTemporal3DWeight: 0.28f, CloudTemporalWeight: 0.42f, CloudQuality: Medium,
                VolumeIntegrateTemporalWeight: 0.35f, UpsampleTemporalWeight: 0.45f,
                PreviewTaaWeight: 0.78f, PreviewTaaJitterScale: 1.0f),
        };

    public static TaaProfile ResolvePreviewTaa(int quality, int taaMode)
    {
        var baseProfile = Resolve(quality);
        if (baseProfile.PreviewTaaWeight <= 0f)
        {
            return new TaaProfile(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
        }

        return Math.Clamp(taaMode, 0, 4) switch
        {
            0 => new TaaProfile(
                TemporalWeight: Math.Min(0.88f, baseProfile.PreviewTaaWeight + 0.04f),
                JitterScale: baseProfile.PreviewTaaJitterScale * 0.52f,
                StableTemporalBoost: 0.08f,
                MaxStableTemporal: 0.93f,
                SharpenStrength: 0.035f,
                DepthEdgeHistoryFloor: 0.08f,
                EdgeAaBlend: 0.08f,
                SourceFilterStrength: 0.22f,
                SilhouetteHistoryWeight: 0.35f,
                FxaaEdgeStrength: 0.30f),
            1 => new TaaProfile(
                TemporalWeight: Math.Min(0.90f, baseProfile.PreviewTaaWeight + 0.10f),
                JitterScale: baseProfile.PreviewTaaJitterScale * 0.35f,
                StableTemporalBoost: 0.05f,
                MaxStableTemporal: 0.95f,
                SharpenStrength: 0.020f,
                DepthEdgeHistoryFloor: 0.10f,
                EdgeAaBlend: 0.07f,
                SourceFilterStrength: 0.16f,
                SilhouetteHistoryWeight: 0.24f,
                FxaaEdgeStrength: 0.22f),
            2 => new TaaProfile(
                TemporalWeight: Math.Min(0.86f, baseProfile.PreviewTaaWeight + 0.02f),
                JitterScale: baseProfile.PreviewTaaJitterScale * 0.70f,
                StableTemporalBoost: 0.10f,
                MaxStableTemporal: 0.90f,
                SharpenStrength: 0.035f,
                DepthEdgeHistoryFloor: 0.60f,
                EdgeAaBlend: 0.55f,
                SourceFilterStrength: 0.80f,
                SilhouetteHistoryWeight: 0.95f,
                FxaaEdgeStrength: 0.72f),
            3 => new TaaProfile(
                TemporalWeight: Math.Max(0.60f, baseProfile.PreviewTaaWeight - 0.10f),
                JitterScale: baseProfile.PreviewTaaJitterScale * 0.65f,
                StableTemporalBoost: 0.05f,
                MaxStableTemporal: 0.86f,
                SharpenStrength: 0.070f,
                DepthEdgeHistoryFloor: 0.04f,
                EdgeAaBlend: 0.05f,
                SourceFilterStrength: 0.12f,
                SilhouetteHistoryWeight: 0.24f,
                FxaaEdgeStrength: 0.20f),
            4 => new TaaProfile(
                TemporalWeight: Math.Max(0.68f, baseProfile.PreviewTaaWeight - 0.06f),
                JitterScale: 0f,
                StableTemporalBoost: 0.03f,
                MaxStableTemporal: 0.84f,
                SharpenStrength: 0.035f,
                DepthEdgeHistoryFloor: 0.18f,
                EdgeAaBlend: 0.28f,
                SourceFilterStrength: 0.45f,
                SilhouetteHistoryWeight: 0.30f,
                FxaaEdgeStrength: 0.55f),
            _ => new TaaProfile(
                TemporalWeight: Math.Min(0.88f, baseProfile.PreviewTaaWeight + 0.04f),
                JitterScale: baseProfile.PreviewTaaJitterScale * 0.52f,
                StableTemporalBoost: 0.08f,
                MaxStableTemporal: 0.93f,
                SharpenStrength: 0.035f,
                DepthEdgeHistoryFloor: 0.08f,
                EdgeAaBlend: 0.08f,
                SourceFilterStrength: 0.22f,
                SilhouetteHistoryWeight: 0.35f,
                FxaaEdgeStrength: 0.30f),
        };
    }

    /// <summary>
    /// When final preview TAA is active, per-pass temporal can be reduced so histories do not double-smear
    /// the same noise/shimmer the composited TAA pass already stabilizes.
    /// </summary>
    public static float EffectivePassTemporalWeight(float passWeight, in PreviewRenderSettingsSnapshot settings)
    {
        if (passWeight <= 0f || !settings.EnablePreviewTaa || settings.PreviewTaaTemporalScale <= 0f)
        {
            return passWeight;
        }

        if (Resolve(settings.VolumetricQuality).PreviewTaaWeight <= 0f)
        {
            return passWeight;
        }

        return passWeight * 0.5f;
    }
}
