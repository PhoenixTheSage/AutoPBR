namespace AutoPBR.App.Rendering.Abstractions;

/// <summary>Cost profiles for preview screen-space AO (SSAO / GTAO).</summary>
public static class PreviewScreenSpaceAoQuality
{
    public readonly record struct Profile(
        PreviewAoMode Technique,
        float ResolutionScale,
        int SsaoSamples,
        int GtaoSlices,
        int GtaoSteps,
        float RadiusScale,
        int BilateralPasses,
        bool UseTemporal);

    public static PreviewAoMode ResolveTechnique(PreviewAoMode mode, int volumetricQuality)
    {
        if (mode == PreviewAoMode.Ssao || mode == PreviewAoMode.Gtao)
        {
            return mode;
        }

        var q = PreviewVolumetricQuality.Clamp(volumetricQuality);
        return q >= PreviewVolumetricQuality.High ? PreviewAoMode.Gtao : PreviewAoMode.Ssao;
    }

    public static Profile Resolve(PreviewAoMode mode, int volumetricQuality)
    {
        var technique = ResolveTechnique(mode, volumetricQuality);
        var q = PreviewVolumetricQuality.Clamp(volumetricQuality);
        return q switch
        {
            PreviewVolumetricQuality.Low => new Profile(
                Technique: technique,
                ResolutionScale: 0.5f,
                SsaoSamples: 8,
                GtaoSlices: 2,
                GtaoSteps: 2,
                RadiusScale: 0.75f,
                BilateralPasses: 1,
                UseTemporal: false),
            PreviewVolumetricQuality.High => new Profile(
                Technique: technique,
                ResolutionScale: 0.5f,
                SsaoSamples: 16,
                GtaoSlices: 4,
                GtaoSteps: 4,
                RadiusScale: 1.0f,
                BilateralPasses: 2,
                UseTemporal: true),
            PreviewVolumetricQuality.Cinematic => new Profile(
                Technique: technique,
                ResolutionScale: 2f / 3f,
                SsaoSamples: 24,
                GtaoSlices: 6,
                GtaoSteps: 6,
                RadiusScale: 1.15f,
                BilateralPasses: 2,
                UseTemporal: true),
            _ => new Profile(
                Technique: technique,
                ResolutionScale: 0.5f,
                SsaoSamples: 16,
                GtaoSlices: 3,
                GtaoSteps: 3,
                RadiusScale: 0.9f,
                BilateralPasses: 2,
                UseTemporal: false),
        };
    }
}
