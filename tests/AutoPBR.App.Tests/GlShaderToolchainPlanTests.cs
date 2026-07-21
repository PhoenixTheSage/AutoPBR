using AutoPBR.App.Rendering.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlShaderToolchainPlanTests
{
    [Fact]
    public void BundledManifest_ContainsProductionComputeSpirVAsset()
    {
        Assert.True(GlSpirVShaderManifest.Bundled.Contains("spv/genesis_indirect_compact.comp.spv"));
        Assert.Equal(1, GlSpirVShaderManifest.Bundled.Count);
    }

    [Fact]
    public void BundledComputeSpirVAsset_IsPackagedAndValid()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "Shaders",
            "spv",
            "genesis_indirect_compact.comp.spv");
        var bytes = File.ReadAllBytes(path);

        Assert.True(GlSpirVShaderBinary.TryCreate(
            "spv/genesis_indirect_compact.comp.spv",
            ShaderType.ComputeShader,
            bytes,
            out var binary));
        Assert.True(binary.IsValid);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AutoPBR.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the AutoPBR repository root.");
    }

    [Fact]
    public void Gles_KeepsGlslPrimaryAndDisablesDesktopToolchain()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "OpenGL ES 3.0 (ANGLE)",
            "Google",
            "ANGLE",
            "GL_EXT_disjoint_timer_query",
            forceOpenGlEs: true);

        var plan = GlShaderToolchainPlan.FromCapabilities(caps, spirvAssetCount: 4);

        Assert.Equal(GlShaderToolchainPlan.PrimaryPath, "GLSL source + program binary cache");
        Assert.False(plan.CanUseSpirVAssets);
        Assert.False(plan.CanEvaluateSeparablePrograms);
        Assert.Equal("off-gles", plan.SpirVStatus);
        Assert.Equal("off-gles", plan.SeparableProgramStatus);
        Assert.Contains("primary=GLSL source + program binary cache", plan.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("fallback=GLSL", plan.FormatDiagnostic(), StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopGl46_StagesSpirVUntilAssetsExist()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.6.0 NVIDIA",
            "NVIDIA",
            "RTX",
            string.Empty,
            forceOpenGlEs: false);

        var plan = GlShaderToolchainPlan.FromCapabilities(caps);

        Assert.True(caps.CanUseSpirVShaderBinaries);
        Assert.True(caps.CanUseSeparableShaderPrograms);
        Assert.False(plan.CanUseSpirVAssets);
        Assert.True(plan.CanEvaluateSeparablePrograms);
        Assert.Equal("no-assets", plan.SpirVStatus);
        Assert.Equal("available", plan.SeparableProgramStatus);
        Assert.Contains("SPIR-V staged", plan.FormatContextSuffix(), StringComparison.Ordinal);
        Assert.Contains("separable available", plan.FormatContextSuffix(), StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopGl46_WithSpirVAssets_MarksIngestionReady()
    {
        var caps = PreviewGlCapabilities.FromStrings(
            "4.6.0 Mesa",
            "Mesa",
            "Renderer",
            string.Empty,
            forceOpenGlEs: false);

        var plan = GlShaderToolchainPlan.FromCapabilities(caps, spirvAssetCount: 2);

        Assert.True(plan.CanUseSpirVAssets);
        Assert.Equal("ready", plan.SpirVStatus);
        Assert.Contains("spirvAssets=2", plan.FormatDiagnostic(), StringComparison.Ordinal);
        Assert.Contains("SPIR-V assets ready", plan.FormatContextSuffix(), StringComparison.Ordinal);
    }

    [Fact]
    public void SpirVBinary_ValidatesMagicAndWordAlignment()
    {
        var valid = new byte[20];
        valid[0] = 0x03;
        valid[1] = 0x02;
        valid[2] = 0x23;
        valid[3] = 0x07;

        Assert.True(GlSpirVShaderBinary.TryCreate("shader.vert.spv", ShaderType.VertexShader, valid, out var binary));
        Assert.True(binary.IsValid);

        Assert.False(GlSpirVShaderBinary.TryCreate("bad.spv", ShaderType.FragmentShader, valid.AsSpan(0, 19), out _));
        valid[0] = 0;
        Assert.False(GlSpirVShaderBinary.TryCreate("bad.spv", ShaderType.FragmentShader, valid, out _));
    }

    [Fact]
    public void SpirVManifest_NormalizesNames()
    {
        var manifest = new GlSpirVShaderManifest(["genesis\\main.vert.spv", "compute.comp.spv"]);

        Assert.Equal(2, manifest.Count);
        Assert.True(manifest.Contains("genesis/main.vert.spv"));
        Assert.True(manifest.Contains("COMPUTE.COMP.SPV"));
        Assert.False(manifest.Contains("missing.spv"));
    }
}
