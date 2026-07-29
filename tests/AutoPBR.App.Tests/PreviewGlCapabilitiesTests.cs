using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class PreviewGlCapabilitiesTests
{
    [Fact]
    public void GlesAngle_UsesCompatibilityFeatureSet()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "OpenGL ES 3.0 (ANGLE 2.1.1)",
            "Google Inc.",
            "ANGLE D3D11",
            "GL_EXT_disjoint_timer_query",
            forceOpenGlEs: true);

        Assert.True(caps.IsOpenGlEs);
        Assert.Equal(3, caps.Major);
        Assert.Equal(0, caps.Minor);
        Assert.True(caps.TextureArrays);
        Assert.True(caps.TimerQuery);
        Assert.False(caps.CanUseGpuTimerQueries);
        Assert.False(caps.BufferStorage);
        Assert.False(caps.CanUsePersistentUploadRing);
        Assert.False(caps.ShaderStorageBuffers);
        Assert.False(caps.CanUseEntitySkinningSsbo);
        Assert.False(caps.CanUseMaterialDrawRecordSsbo);
        Assert.False(caps.ComputeShaders);
        Assert.False(caps.CanUseComputeFroxelInject);
        Assert.False(caps.ImageLoadStore);
        Assert.False(caps.CanUseIndirectDrawCommands);
        Assert.False(caps.ShaderDrawParameters);
        Assert.False(caps.CanUseMultiDrawIndirectGroups);
        Assert.False(caps.CanUseGpuCommandCompaction);
        Assert.False(caps.CanUseGpuBatchCulling);
        Assert.False(caps.CanUseGpuCompactedDrawSubmission);
        Assert.False(caps.CanUseHierarchicalZOcclusion);
        Assert.False(caps.CanUseGpuTerrainShadowCull);
        Assert.False(caps.CanUseGpuReductionDiagnostics);
        Assert.False(caps.CanUseImageHistogram);
        Assert.False(caps.CanUseMaterialTextureArrays);
        Assert.False(caps.CanUseSpirVShaderBinaries);
        Assert.False(caps.CanUseSeparableShaderPrograms);
        Assert.False(caps.CanUseFloatingPointCloudTargets);
        Assert.False(caps.CanUseCloudTemporalMoments);
        Assert.False(caps.CanUseFragmentCloudLightingCache);
        Assert.False(caps.CanUseComputeCloudLightingCache);
        Assert.Contains("persistentUpload=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("entitySsbo=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("materialDrawSsbo=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("computeFroxels=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("indirectDraws=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("multiDrawGroups=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuCommandCompaction=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuBatchCulling=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuCompactedDraws=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("hiZOcclusion=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("voxelDdaOcclusion=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuTerrainShadowCull=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuReductions=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("imageHistogram=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("materialTextureArrays=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("cloudFpTargets=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("cloudLightCacheFragment=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("cloudLightCacheCompute=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuTimers=off", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("separablePrograms=no", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("GLES-safe uploads", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("draw uniforms", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("material samplers", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("fragment froxels", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("RGBA8 clouds", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("direct draws", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("no GPU timers", caps.FormatContextSuffix(), StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopGl33_DoesNotAssumeModernAcceleration()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "3.3.0 Compatibility Profile Context",
            "Vendor",
            "Renderer",
            string.Empty,
            forceOpenGlEs: false);

        Assert.False(caps.IsOpenGlEs);
        Assert.True(caps.TextureArrays);
        Assert.True(caps.TimerQuery);
        Assert.True(caps.CanUseGpuTimerQueries);
        Assert.True(caps.CanUseFloatingPointCloudTargets);
        Assert.True(caps.CanUseCloudTemporalMoments);
        Assert.True(caps.CanUseFragmentCloudLightingCache);
        Assert.False(caps.CanUseComputeCloudLightingCache);
        Assert.False(caps.CanUseSpirVShaderBinaries);
        Assert.False(caps.CanUseSeparableShaderPrograms);
        Assert.False(caps.BufferStorage);
        Assert.False(caps.CanUsePersistentUploadRing);
        Assert.False(caps.ShaderStorageBuffers);
        Assert.False(caps.CanUseEntitySkinningSsbo);
        Assert.False(caps.CanUseMaterialDrawRecordSsbo);
        Assert.False(caps.CanUseMaterialTextureArrays);
        Assert.False(caps.ComputeShaders);
        Assert.False(caps.CanUseComputeFroxelInject);
        Assert.False(caps.MultiDrawIndirect);
        Assert.False(caps.CanUseIndirectDrawCommands);
        Assert.False(caps.CanUseMultiDrawIndirectGroups);
        Assert.False(caps.CanUseGpuCommandCompaction);
        Assert.False(caps.CanUseGpuBatchCulling);
        Assert.Contains("cloudFpTargets=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("FP cloud targets", caps.FormatContextSuffix(), StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopGl40_KeepsGl46SystemsDisabled()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.0.0 NVIDIA 999.00",
            "NVIDIA",
            "RTX",
            string.Empty,
            forceOpenGlEs: false);

        Assert.False(caps.IsOpenGlEs);
        Assert.True(caps.TextureArrays);
        Assert.True(caps.TimerQuery);
        Assert.True(caps.CanUseGpuTimerQueries);
        Assert.False(caps.BufferStorage);
        Assert.False(caps.CanUsePersistentUploadRing);
        Assert.False(caps.ShaderStorageBuffers);
        Assert.False(caps.CanUseEntitySkinningSsbo);
        Assert.False(caps.CanUseMaterialDrawRecordSsbo);
        Assert.False(caps.CanUseMaterialTextureArrays);
        Assert.False(caps.ComputeShaders);
        Assert.False(caps.CanUseComputeFroxelInject);
        Assert.False(caps.ImageLoadStore);
        Assert.False(caps.SpirV);
        Assert.False(caps.CanUseSpirVShaderBinaries);
        Assert.False(caps.CanUseSeparableShaderPrograms);
        Assert.False(caps.CanUseIndirectDrawCommands);
        Assert.False(caps.CanUseMultiDrawIndirectGroups);
        Assert.False(caps.CanUseGpuCommandCompaction);
        Assert.False(caps.CanUseGpuBatchCulling);
    }

    [Fact]
    public void DesktopGl46_EnablesCoreModernSystems()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.6.0 NVIDIA 999.00",
            "NVIDIA",
            "RTX",
            "GL_ARB_bindless_texture",
            forceOpenGlEs: false);

        Assert.False(caps.IsOpenGlEs);
        Assert.True(caps.BufferStorage);
        Assert.True(caps.CanUsePersistentUploadRing);
        Assert.True(caps.ShaderStorageBuffers);
        Assert.True(caps.CanUseEntitySkinningSsbo);
        Assert.True(caps.CanUseMaterialDrawRecordSsbo);
        Assert.True(caps.CanUseMaterialTextureArrays);
        Assert.True(caps.ComputeShaders);
        Assert.True(caps.ImageLoadStore);
        Assert.True(caps.CanUseComputeFroxelInject);
        Assert.True(caps.ShaderAtomics);
        Assert.True(caps.MultiDrawIndirect);
        Assert.True(caps.CanUseIndirectDrawCommands);
        Assert.True(caps.ShaderDrawParameters);
        Assert.True(caps.CanUseMultiDrawIndirectGroups);
        Assert.True(caps.CanUseGpuCommandCompaction);
        Assert.True(caps.CanUseGpuBatchCulling);
        Assert.True(caps.IndirectParameters);
        Assert.True(caps.CanUseGpuCompactedDrawSubmission);
        Assert.True(caps.CanUseHierarchicalZOcclusion);
        Assert.True(caps.CanUseGpuTerrainShadowCull);
        Assert.True(caps.CanUseGpuReductionDiagnostics);
        Assert.True(caps.CanUseImageHistogram);
        Assert.True(caps.TimerQuery);
        Assert.True(caps.CanUseGpuTimerQueries);
        Assert.True(caps.TextureArrays);
        Assert.True(caps.BindlessTextures);
        Assert.True(caps.SpirV);
        Assert.True(caps.CanUseSpirVShaderBinaries);
        Assert.True(caps.SeparablePrograms);
        Assert.True(caps.CanUseSeparableShaderPrograms);
        Assert.True(caps.CanUseFloatingPointCloudTargets);
        Assert.True(caps.CanUseCloudTemporalMoments);
        Assert.True(caps.CanUseFragmentCloudLightingCache);
        Assert.True(caps.CanUseComputeCloudLightingCache);
        Assert.Contains("persistentUpload=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("entitySsbo=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("materialDrawSsbo=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("computeFroxels=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("indirectDraws=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("multiDrawGroups=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuCommandCompaction=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuBatchCulling=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuCompactedDraws=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("hiZOcclusion=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("voxelDdaOcclusion=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuTerrainShadowCull=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuReductions=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("imageHistogram=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("materialTextureArrays=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("cloudFpTargets=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("cloudLightCacheFragment=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("cloudLightCacheCompute=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("gpuTimers=on", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("separablePrograms=yes", caps.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("persistent uploads", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("draw SSBO", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("material arrays", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("compute froxels", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("FP cloud targets", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("multi-draw groups", caps.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("GPU timers", caps.FormatContextSuffix(), StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopExtensions_CanEnableIndividualSystemsBelowCoreVersion()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.3.0 Mesa",
            "Mesa",
            "Renderer",
            "GL_ARB_buffer_storage GL_ARB_gl_spirv",
            forceOpenGlEs: false);

        Assert.True(caps.BufferStorage);
        Assert.True(caps.CanUsePersistentUploadRing);
        Assert.True(caps.ShaderStorageBuffers);
        Assert.True(caps.CanUseEntitySkinningSsbo);
        Assert.True(caps.CanUseMaterialDrawRecordSsbo);
        Assert.True(caps.ComputeShaders);
        Assert.True(caps.ImageLoadStore);
        Assert.True(caps.CanUseComputeFroxelInject);
        Assert.True(caps.MultiDrawIndirect);
        Assert.True(caps.CanUseIndirectDrawCommands);
        Assert.False(caps.ShaderDrawParameters);
        Assert.False(caps.CanUseMultiDrawIndirectGroups);
        Assert.True(caps.CanUseGpuCommandCompaction);
        Assert.True(caps.CanUseGpuBatchCulling);
        Assert.False(caps.CanUseGpuCompactedDrawSubmission);
        Assert.True(caps.SpirV);
    }

    [Fact]
    public void DesktopGl43_ShaderDrawParametersExtensionEnablesMultiDrawGroups()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.3.0 Mesa",
            "Mesa",
            "Renderer",
            "GL_ARB_shader_draw_parameters",
            forceOpenGlEs: false);

        Assert.True(caps.MultiDrawIndirect);
        Assert.True(caps.ShaderStorageBuffers);
        Assert.True(caps.ShaderDrawParameters);
        Assert.True(caps.CanUseMultiDrawIndirectGroups);
        Assert.True(caps.CanUseGpuCommandCompaction);
        Assert.True(caps.CanUseGpuBatchCulling);
        Assert.False(caps.CanUseGpuCompactedDrawSubmission);
    }

    [Fact]
    public void DesktopGl43_IndirectParameterExtensionsEnableGpuCompactedSubmission()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.3.0 Mesa",
            "Mesa",
            "Renderer",
            "GL_ARB_shader_draw_parameters GL_ARB_indirect_parameters",
            forceOpenGlEs: false);

        Assert.True(caps.IndirectParameters);
        Assert.True(caps.CanUseGpuCompactedDrawSubmission);
        Assert.True(caps.CanUseGpuTerrainShadowCull);
    }
}
