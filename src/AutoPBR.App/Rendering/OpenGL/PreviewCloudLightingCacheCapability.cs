using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Rendering.OpenGL;

internal enum PreviewCloudLightingCacheGenerationPath
{
    ShortMarch,
    FragmentSlices,
    ComputeImageStore,
}

internal readonly record struct PreviewCloudLightingCachePlan(
    PreviewCloudLightingCacheProfile Profile,
    PreviewCloudLightingCacheGenerationPath PreferredGenerationPath,
    PreviewCloudLightingCacheGenerationPath ActiveRuntimePath)
{
    // CQ3.0 freezes the ABI. CQ3.1 allocates and validates the fragment reference cache, but the
    // production cloud-light consumer remains the short march until CQ3.3.
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
            _ => "short-march",
        };
}
