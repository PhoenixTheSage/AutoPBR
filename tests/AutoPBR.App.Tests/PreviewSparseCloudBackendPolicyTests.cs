using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class PreviewSparseCloudBackendPolicyTests
{
    [Theory]
    [InlineData(PreviewVolumetricQuality.Low)]
    [InlineData(PreviewVolumetricQuality.Medium)]
    [InlineData(PreviewVolumetricQuality.High)]
    public void NonCinematicPresets_KeepProceduralLayer(int quality)
    {
        var selection = Select(quality, ModernDesktop());

        Assert.Equal(
            PreviewCloudDensityBackend.ProceduralLayer,
            selection.RequestedBackend);
        Assert.Equal(
            PreviewCloudDensityBackend.ProceduralLayer,
            selection.ActiveBackend);
        Assert.Equal(
            PreviewSparseCloudFallbackReason.QualityNotCinematic,
            selection.FallbackReason);
    }

    [Fact]
    public void CinematicEligibleButCq40ResourcesUnavailable_UsesAcceptedFallback()
    {
        var selection = Select(
            PreviewVolumetricQuality.Cinematic,
            ModernDesktop());

        Assert.True(selection.CapabilityEligible);
        Assert.Equal(
            PreviewCloudDensityBackend.SparseVoxel,
            selection.RequestedBackend);
        Assert.Equal(
            PreviewCloudDensityBackend.ProceduralLayer,
            selection.ActiveBackend);
        Assert.Equal(
            PreviewSparseCloudFallbackReason.SparseResourcesNotReady,
            selection.FallbackReason);
        Assert.Contains(
            "resources-not-initialized-cq4.0",
            selection.FormatDiagnostic(),
            StringComparison.Ordinal);
        Assert.Contains(
            "cq3Fallback=accepted-flat-layer",
            selection.FormatDiagnostic(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CinematicReadyResources_SelectSparseVoxel()
    {
        var selection = Select(
            PreviewVolumetricQuality.Cinematic,
            ModernDesktop(),
            sparseResourcesReady: true);

        Assert.True(selection.CapabilityEligible);
        Assert.Equal(
            PreviewCloudDensityBackend.SparseVoxel,
            selection.ActiveBackend);
        Assert.Equal(
            PreviewSparseCloudFallbackReason.None,
            selection.FallbackReason);
    }

    [Fact]
    public void DebugForceProcedural_WinsWithoutChangingEligibility()
    {
        var selection = Select(
            PreviewVolumetricQuality.Cinematic,
            ModernDesktop(),
            forceProceduralLayer: true,
            sparseResourcesReady: true);

        Assert.True(selection.CapabilityEligible);
        Assert.Equal(
            PreviewCloudDensityBackend.ProceduralLayer,
            selection.ActiveBackend);
        Assert.Equal(
            PreviewSparseCloudFallbackReason.ForcedProceduralLayer,
            selection.FallbackReason);
    }

    [Fact]
    public void RuntimeFault_DemotesReadySparseResourcesForSession()
    {
        var selection = Select(
            PreviewVolumetricQuality.Cinematic,
            ModernDesktop(),
            sparseResourcesReady: true,
            sparseRuntimeFaulted: true);

        Assert.Equal(
            PreviewCloudDensityBackend.ProceduralLayer,
            selection.ActiveBackend);
        Assert.Equal(
            PreviewSparseCloudFallbackReason.SparseRuntimeFaulted,
            selection.FallbackReason);
    }

    [Fact]
    public void GlesCinematic_UsesProceduralCompatibilityPath()
    {
        var selection = Select(
            PreviewVolumetricQuality.Cinematic,
            PreviewGlCapabilities.FromStrings(
                "OpenGL ES 3.0",
                "Google",
                "ANGLE",
                string.Empty,
                forceOpenGlEs: true),
            sparseResourcesReady: true);

        Assert.False(selection.CapabilityEligible);
        Assert.Equal(
            PreviewSparseCloudFallbackReason.OpenGlEs,
            selection.FallbackReason);
        Assert.Equal(
            PreviewCloudDensityBackend.ProceduralLayer,
            selection.ActiveBackend);
    }

    [Theory]
    [InlineData(
        "4.2.0",
        "GL_ARB_shader_image_load_store GL_ARB_shader_storage_buffer_object",
        (int)PreviewSparseCloudFallbackReason.ComputeShadersUnavailable)]
    [InlineData(
        "4.1.0",
        "GL_ARB_compute_shader GL_ARB_shader_storage_buffer_object",
        (int)PreviewSparseCloudFallbackReason.ImageLoadStoreUnavailable)]
    [InlineData(
        "4.2.0",
        "GL_ARB_compute_shader GL_ARB_shader_image_load_store",
        (int)PreviewSparseCloudFallbackReason.ShaderStorageBuffersUnavailable)]
    public void MissingRequiredDesktopFeature_ReportsSpecificFallback(
        string version,
        string extensions,
        int expectedReason)
    {
        var caps = PreviewGlCapabilities.FromStrings(
            version,
            "Vendor",
            "Renderer",
            extensions,
            forceOpenGlEs: false);

        var selection = Select(
            PreviewVolumetricQuality.Cinematic,
            caps,
            sparseResourcesReady: true);

        Assert.False(caps.CanUseSparseCloudVolumes);
        Assert.False(selection.CapabilityEligible);
        Assert.Equal(
            (PreviewSparseCloudFallbackReason)expectedReason,
            selection.FallbackReason);
    }

    private static PreviewSparseCloudBackendSelection Select(
        int quality,
        PreviewGlCapabilities? capabilities,
        bool forceProceduralLayer = false,
        bool sparseResourcesReady = false,
        bool sparseRuntimeFaulted = false) =>
        PreviewSparseCloudBackendPolicy.Select(
            quality,
            capabilities,
            forceProceduralLayer,
            sparseResourcesReady,
            sparseRuntimeFaulted);

    private static PreviewGlCapabilities ModernDesktop() =>
        PreviewGlCapabilities.FromStrings(
            "4.6.0 NVIDIA",
            "NVIDIA",
            "RTX",
            string.Empty,
            forceOpenGlEs: false);
}
