using AutoPBR.App.Rendering.Abstractions;

namespace AutoPBR.App.Rendering.OpenGL;

internal enum PreviewCloudDensityBackend
{
    ProceduralLayer,
    SparseVoxel,
}

internal enum PreviewSparseCloudFallbackReason
{
    None,
    QualityNotCinematic,
    CapabilitiesUnavailable,
    OpenGlEs,
    ComputeShadersUnavailable,
    ImageLoadStoreUnavailable,
    ShaderStorageBuffersUnavailable,
    ForcedProceduralLayer,
    SparseResourcesNotReady,
    SparseRuntimeFaulted,
}

/// <summary>
/// CQ4 backend decision separated into requested, capability-eligible, and active state. CQ4.0
/// deliberately keeps resources unavailable, so eligible Cinematic systems still execute the
/// accepted CQ3.9 procedural layer until later milestones publish a complete sparse resource set.
/// </summary>
internal readonly record struct PreviewSparseCloudBackendSelection(
    PreviewCloudDensityBackend RequestedBackend,
    PreviewCloudDensityBackend ActiveBackend,
    bool CapabilityEligible,
    PreviewSparseCloudFallbackReason FallbackReason,
    bool ForceProceduralLayer,
    bool SparseResourcesReady,
    bool SparseRuntimeFaulted)
{
    public string FormatDiagnostic() =>
        $"requested={FormatBackend(RequestedBackend)};" +
        $"eligible={CapabilityEligible};" +
        $"active={FormatBackend(ActiveBackend)};" +
        $"fallback={FormatFallback(FallbackReason)};" +
        $"forceProcedural={ForceProceduralLayer};" +
        $"resourcesReady={SparseResourcesReady};" +
        $"runtimeFaulted={SparseRuntimeFaulted};" +
        "cq3Fallback=accepted-flat-layer";

    private static string FormatBackend(PreviewCloudDensityBackend backend) =>
        backend == PreviewCloudDensityBackend.SparseVoxel
            ? "sparse-voxel"
            : "procedural-layer";

    private static string FormatFallback(PreviewSparseCloudFallbackReason reason) =>
        reason switch
        {
            PreviewSparseCloudFallbackReason.None => "none",
            PreviewSparseCloudFallbackReason.QualityNotCinematic => "quality-not-cinematic",
            PreviewSparseCloudFallbackReason.CapabilitiesUnavailable => "capabilities-unavailable",
            PreviewSparseCloudFallbackReason.OpenGlEs => "gles-angle-compatibility",
            PreviewSparseCloudFallbackReason.ComputeShadersUnavailable => "compute-unavailable",
            PreviewSparseCloudFallbackReason.ImageLoadStoreUnavailable => "image-load-store-unavailable",
            PreviewSparseCloudFallbackReason.ShaderStorageBuffersUnavailable => "ssbo-unavailable",
            PreviewSparseCloudFallbackReason.ForcedProceduralLayer => "debug-force-procedural",
            PreviewSparseCloudFallbackReason.SparseResourcesNotReady => "resources-not-initialized-cq4.0",
            PreviewSparseCloudFallbackReason.SparseRuntimeFaulted => "session-runtime-fault",
            _ => "unknown",
        };
}

internal static class PreviewSparseCloudBackendPolicy
{
    /// <summary>
    /// Debug-only parity switch. This intentionally is not a persisted end-user setting.
    /// Hosts may set it before constructing the preview backend.
    /// </summary>
    public const string ForceProceduralLayerSwitch =
        "AutoPBR.Preview.ForceProceduralCloudLayer";

    public static bool IsForceProceduralLayerEnabled() =>
        AppContext.TryGetSwitch(ForceProceduralLayerSwitch, out var enabled) &&
        enabled;

    public static PreviewSparseCloudBackendSelection Select(
        int volumetricQuality,
        PreviewGlCapabilities? capabilities,
        bool forceProceduralLayer,
        bool sparseResourcesReady,
        bool sparseRuntimeFaulted)
    {
        if (PreviewVolumetricQuality.Clamp(volumetricQuality) !=
            PreviewVolumetricQuality.Cinematic)
        {
            return Procedural(
                PreviewCloudDensityBackend.ProceduralLayer,
                capabilityEligible: false,
                PreviewSparseCloudFallbackReason.QualityNotCinematic,
                forceProceduralLayer,
                sparseResourcesReady,
                sparseRuntimeFaulted);
        }

        var capabilityEligible =
            capabilities?.CanUseSparseCloudVolumes == true;
        if (forceProceduralLayer)
        {
            return Procedural(
                PreviewCloudDensityBackend.SparseVoxel,
                capabilityEligible,
                PreviewSparseCloudFallbackReason.ForcedProceduralLayer,
                forceProceduralLayer,
                sparseResourcesReady,
                sparseRuntimeFaulted);
        }

        if (capabilities is null)
        {
            return Procedural(
                PreviewCloudDensityBackend.SparseVoxel,
                capabilityEligible: false,
                PreviewSparseCloudFallbackReason.CapabilitiesUnavailable,
                forceProceduralLayer,
                sparseResourcesReady,
                sparseRuntimeFaulted);
        }

        if (capabilities.IsOpenGlEs)
        {
            return Procedural(
                PreviewCloudDensityBackend.SparseVoxel,
                capabilityEligible: false,
                PreviewSparseCloudFallbackReason.OpenGlEs,
                forceProceduralLayer,
                sparseResourcesReady,
                sparseRuntimeFaulted);
        }

        if (!capabilities.ComputeShaders)
        {
            return Procedural(
                PreviewCloudDensityBackend.SparseVoxel,
                capabilityEligible: false,
                PreviewSparseCloudFallbackReason.ComputeShadersUnavailable,
                forceProceduralLayer,
                sparseResourcesReady,
                sparseRuntimeFaulted);
        }

        if (!capabilities.ImageLoadStore)
        {
            return Procedural(
                PreviewCloudDensityBackend.SparseVoxel,
                capabilityEligible: false,
                PreviewSparseCloudFallbackReason.ImageLoadStoreUnavailable,
                forceProceduralLayer,
                sparseResourcesReady,
                sparseRuntimeFaulted);
        }

        if (!capabilities.ShaderStorageBuffers)
        {
            return Procedural(
                PreviewCloudDensityBackend.SparseVoxel,
                capabilityEligible: false,
                PreviewSparseCloudFallbackReason.ShaderStorageBuffersUnavailable,
                forceProceduralLayer,
                sparseResourcesReady,
                sparseRuntimeFaulted);
        }

        if (sparseRuntimeFaulted)
        {
            return Procedural(
                PreviewCloudDensityBackend.SparseVoxel,
                capabilityEligible: true,
                PreviewSparseCloudFallbackReason.SparseRuntimeFaulted,
                forceProceduralLayer,
                sparseResourcesReady,
                sparseRuntimeFaulted);
        }

        if (!sparseResourcesReady)
        {
            return Procedural(
                PreviewCloudDensityBackend.SparseVoxel,
                capabilityEligible: true,
                PreviewSparseCloudFallbackReason.SparseResourcesNotReady,
                forceProceduralLayer,
                sparseResourcesReady,
                sparseRuntimeFaulted);
        }

        return new PreviewSparseCloudBackendSelection(
            PreviewCloudDensityBackend.SparseVoxel,
            PreviewCloudDensityBackend.SparseVoxel,
            CapabilityEligible: true,
            PreviewSparseCloudFallbackReason.None,
            forceProceduralLayer,
            sparseResourcesReady,
            sparseRuntimeFaulted);
    }

    private static PreviewSparseCloudBackendSelection Procedural(
        PreviewCloudDensityBackend requestedBackend,
        bool capabilityEligible,
        PreviewSparseCloudFallbackReason reason,
        bool forceProceduralLayer,
        bool sparseResourcesReady,
        bool sparseRuntimeFaulted) =>
        new(
            requestedBackend,
            PreviewCloudDensityBackend.ProceduralLayer,
            capabilityEligible,
            reason,
            forceProceduralLayer,
            sparseResourcesReady,
            sparseRuntimeFaulted);
}
