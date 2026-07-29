using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Rendering.OpenGL;

internal enum PreviewCloudLightingCacheGenerationPath
{
    ShortMarch,
    FragmentSlices,
    ComputeImageStore,
    CacheSampling,
}

internal readonly record struct PreviewCloudLightingCachePlan(
    PreviewCloudLightingCacheProfile Profile,
    PreviewCloudLightingCacheGenerationPath PreferredGenerationPath,
    PreviewCloudLightingCacheGenerationPath ActiveRuntimePath)
{
    // Creation describes the capability/resource preference. CQ3.3 promotes ActiveRuntimePath
    // to CacheSampling only after at least one generated cascade is safe to bind.
    public static PreviewCloudLightingCachePlan Create(
        PreviewGlCapabilities? capabilities,
        int volumetricQuality)
    {
        var profile = PreviewCloudLightingCacheProfiles.Resolve(volumetricQuality);
        var path = !profile.IsEnabled || capabilities is null
            ? PreviewCloudLightingCacheGenerationPath.ShortMarch
            : capabilities.CanUseComputeCloudLightingCache
                ? PreviewCloudLightingCacheGenerationPath.ComputeImageStore
                : capabilities.CanUseFragmentCloudLightingCache
                    ? PreviewCloudLightingCacheGenerationPath.FragmentSlices
                    : PreviewCloudLightingCacheGenerationPath.ShortMarch;
        return new PreviewCloudLightingCachePlan(
            profile,
            path,
            PreviewCloudLightingCacheGenerationPath.ShortMarch);
    }

    public string FormatDiagnostic(string resourceDiagnostic = "resources=not-allocated-cq3.0") =>
        $"profile={Profile.FormatDiagnostic()};" +
        $"preferredGenerator={FormatPath(PreferredGenerationPath)};" +
        $"activeRuntime={FormatPath(ActiveRuntimePath)};" +
        $"{resourceDiagnostic};cameraFogFroxels=separate";

    public static string FormatPath(PreviewCloudLightingCacheGenerationPath path) =>
        path switch
        {
            PreviewCloudLightingCacheGenerationPath.ComputeImageStore => "compute-image-store",
            PreviewCloudLightingCacheGenerationPath.FragmentSlices => "fragment-slices",
            PreviewCloudLightingCacheGenerationPath.CacheSampling => "cache-sampling",
            _ => "short-march",
        };
}

internal static class PreviewCloudLightingCacheGeneratorFallback
{
    public static PreviewCloudLightingCacheGenerationPath Select(
        in PreviewCloudLightingCachePlan plan,
        bool computeProgramReady,
        bool computeSessionFaulted,
        bool fragmentProgramReady)
    {
        if (!plan.Profile.IsEnabled)
        {
            return PreviewCloudLightingCacheGenerationPath.ShortMarch;
        }

        if (plan.PreferredGenerationPath ==
                PreviewCloudLightingCacheGenerationPath.ComputeImageStore &&
            computeProgramReady &&
            !computeSessionFaulted)
        {
            return PreviewCloudLightingCacheGenerationPath.ComputeImageStore;
        }

        return fragmentProgramReady
            ? PreviewCloudLightingCacheGenerationPath.FragmentSlices
            : PreviewCloudLightingCacheGenerationPath.ShortMarch;
    }
}
