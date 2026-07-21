using AutoPBR.App.Rendering.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class PreviewVolumeInjectShaderEsTests
{
    [Theory]
    [InlineData("genesis_volume_inject.frag")]
    [InlineData("genesis_volume_inject_lite.frag")]
    [InlineData("genesis_volume_integrate.frag")]
    [InlineData("genesis_volume_integrate_lite.frag")]
    [InlineData("genesis_clouds.frag")]
    public void EsAdaptedVolumeShaders_AvoidGlesIncompatibleOutParams(string fragmentFile)
    {
        var adapted = ResolveAndAdapt(fragmentFile);
        Assert.DoesNotContain("bool vcIntersectLayer", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain(", out float", adapted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("genesis_volume_inject.frag")]
    [InlineData("genesis_volume_inject_lite.frag")]
    public void EsAdaptedVolumeInject_UsesAngleSafePackHelper(string fragmentFile)
    {
        var adapted = ResolveAndAdapt(fragmentFile);
        Assert.Contains("FragColor = viPackFroxelInject(mediumRho, uLightColor", adapted, StringComparison.Ordinal);
        Assert.Contains("#define GENESIS_GLES 1", adapted, StringComparison.Ordinal);
        Assert.Contains("injectOut.r = mediumRho;", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("vec4 packed;", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("return;", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("vcFbm", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("vmMediumDensity", adapted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("genesis_volume_inject.frag")]
    [InlineData("genesis_volume_inject_lite.frag")]
    public void DesktopVolumeInject_UsesDesktopPackHelper(string fragmentFile)
    {
        var adapted = ResolveAndAdapt(fragmentFile, useOpenGlEs: false);
        // Desktop Adapt leaves source unchanged: both #ifdef GENESIS_GLES branches remain in the TU;
        // GLSL compile picks the #else vec4() path when GENESIS_GLES is undefined.
        Assert.Contains("return vec4(mediumRho, sunLit.x, sunLit.y, occ);", adapted, StringComparison.Ordinal);
        Assert.Contains("#ifdef GENESIS_GLES", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("#define GENESIS_GLES 1", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("vec4 packed;", adapted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("genesis_volume_integrate.frag")]
    [InlineData("genesis_volume_integrate_lite.frag")]
    public void EsAdaptedVolumeIntegrate_UsesTexelFetchAndGodRayOutput(string fragmentFile)
    {
        var adapted = ResolveAndAdapt(fragmentFile);
        Assert.Contains("GENESIS_GLES_PACK rev29", adapted, StringComparison.Ordinal);
        Assert.Contains("atmosphereMiePhase", adapted, StringComparison.Ordinal);
        Assert.Contains("vmSegmentInscatterWeight", adapted, StringComparison.Ordinal);
        Assert.Contains("vmSegmentTransmittance", adapted, StringComparison.Ordinal);
        Assert.Contains("viSampleFroxel", adapted, StringComparison.Ordinal);
        Assert.Contains("grWorldRayDir", adapted, StringComparison.Ordinal);
        Assert.Contains("vfWorldToFroxelUv", adapted, StringComparison.Ordinal);
        Assert.Contains("vfFroxelEdgeWeight", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("texelFetch(", adapted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("genesis_volume_integrate.frag")]
    [InlineData("genesis_volume_integrate_lite.frag")]
    public void VolumeIntegrate_ConsumesDetailedCloudTransmittanceAtDepth(string fragmentFile)
    {
        var adapted = ResolveAndAdapt(fragmentFile);

        Assert.Contains("uniform sampler2D uCloudTransmittance", adapted, StringComparison.Ordinal);
        Assert.Contains("uniform sampler2D uCloudData", adapted, StringComparison.Ordinal);
        Assert.Contains("cstResolveViewSignal", adapted, StringComparison.Ordinal);
        Assert.Contains("cstViewTransmittance(t, sharedCloudDistance", adapted, StringComparison.Ordinal);
        Assert.Contains("transmittance * cloudViewT * sunScatter", adapted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("genesis_volume_inject.frag")]
    [InlineData("genesis_volume_inject_lite.frag")]
    [InlineData("genesis_volume_integrate.frag")]
    [InlineData("genesis_volume_integrate_lite.frag")]
    [InlineData("genesis_clouds.frag")]
    public void EsAdaptedVolumeShaders_ContainNoNonAsciiBytes(string fragmentFile)
    {
        var adapted = ResolveAndAdapt(fragmentFile);
        foreach (var ch in adapted)
        {
            Assert.True(ch <= '\x7F',
                $"Non-ASCII char U+{(int)ch:X4} in adapted '{fragmentFile}' breaks ANGLE's GLES lexer.");
        }
    }

    [Fact]
    public void LiteIntegrate_ExcludesFbmCloudDensity()
    {
        var adapted = ResolveAndAdapt("genesis_volume_integrate_lite.frag");
        Assert.DoesNotContain("vcFbm", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("vmMediumDensity", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("sampleShadowPcf3x3", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void LiteInject_ExcludesRayMarchHelpers()
    {
        var adapted = ResolveAndAdapt("genesis_volume_inject_lite.frag");
        Assert.DoesNotContain("vcMarchClouds", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("vcIntersectLayerRange", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("vcCloudDensityEx", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("sampler3D cloudNoise", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("vfSampleFroxel", adapted, StringComparison.Ordinal);
        Assert.Contains("viInjectMediumDensity", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisClouds_DefinesDensityFunctionsBeforeUse()
    {
        // Regression: the flattened TU once referenced vcCloudDensityRaw from the light-march helper
        // before any density include appeared, so the cloud program silently failed to compile and
        // the clouds toggle had no visible effect.
        var adapted = ResolveAndAdapt("genesis_clouds.frag");
        var densityEx = adapted.IndexOf("float vcCloudDensityEx(", StringComparison.Ordinal);
        var lightMarch = adapted.IndexOf("float vcLightOpticalDepthFromBase(", StringComparison.Ordinal);
        var marchUse = adapted.IndexOf("vcLightOpticalDepthFromBase(baseShape", StringComparison.Ordinal);
        Assert.True(densityEx >= 0, "vcCloudDensityEx definition missing from flattened genesis_clouds.frag");
        Assert.True(lightMarch >= 0, "vcLightOpticalDepthFromBase definition missing from flattened genesis_clouds.frag");
        Assert.True(marchUse > lightMarch, "main() must call vcLightOpticalDepthFromBase after its definition");
        Assert.True(densityEx < lightMarch,
            "cloud density functions must be defined before the sun light march that samples them");
    }

    [Fact]
    public void GenesisClouds_UsesCurvedShellAndConservativeMarch()
    {
        var adapted = ResolveAndAdapt("genesis_clouds.frag");

        Assert.Contains("vcsIntersectShell", adapted, StringComparison.Ordinal);
        Assert.Contains("vcCloudConservativeDensity", adapted, StringComparison.Ordinal);
        Assert.Contains("const int CLOUD_MAX_STEPS = 64", adapted, StringComparison.Ordinal);
        Assert.Contains("tExit = min(slabSeg.y, sceneT)", adapted, StringComparison.Ordinal);
        Assert.Contains("vcsPlanetOcclusionDistance", adapted, StringComparison.Ordinal);
        Assert.Contains("vcsPlanetHorizonVisibility", adapted, StringComparison.Ordinal);
        Assert.Contains("float sceneT = cloudSceneDistance(rd)", adapted, StringComparison.Ordinal);
        Assert.Contains("density = vcCloudDensityFromBase", adapted, StringComparison.Ordinal);
        Assert.Contains("uWindOffset) * slabHorizonVisibility", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("cloudMinRayElevation", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("cloudHorizonLifetime", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("cloudHeightMarchT", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisClouds_UsesLayerAwareCumulusAndWindShearedCirrus()
    {
        var adapted = ResolveAndAdapt("genesis_clouds.frag");

        Assert.Contains("vec2 branchSpace", adapted, StringComparison.Ordinal);
        Assert.Contains("float filament", adapted, StringComparison.Ordinal);
        Assert.Contains("float detachedPatch", adapted, StringComparison.Ordinal);
        Assert.Contains("uCirrusWindDir", adapted, StringComparison.Ordinal);
        Assert.Contains("float topFadeStart", adapted, StringComparison.Ordinal);
        Assert.Contains("float horizontalScale", adapted, StringComparison.Ordinal);
        Assert.Contains("vec2 upperDrift", adapted, StringComparison.Ordinal);
        Assert.Contains("float edgeWeight", adapted, StringComparison.Ordinal);
        Assert.Contains("int cirrusSamples = uQuality >= 2 ? 2 : 1", adapted, StringComparison.Ordinal);
        Assert.Contains("float cirrusOd", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudUpsample_RejectsCloudsBehindOpaqueSceneDepth()
    {
        var adapted = ResolveAndAdapt("genesis_clouds_upsample.frag");

        Assert.Contains("uniform sampler2D uCloudData", adapted, StringComparison.Ordinal);
        Assert.Contains("cloudTapSceneVisibility", adapted, StringComparison.Ordinal);
        Assert.Contains("csdCloudInFrontOfScene", adapted, StringComparison.Ordinal);
        Assert.Contains("sceneDepthWeight(centerDepth, uv0) * cloudTapSceneVisibility", adapted,
            StringComparison.Ordinal);
        Assert.Contains("vcsPlanetOcclusionDistance", adapted, StringComparison.Ordinal);
        Assert.Contains("vcsPlanetHorizonVisibility", adapted, StringComparison.Ordinal);
        Assert.Contains("ctDecodeDistance", adapted, StringComparison.Ordinal);
        Assert.Contains("FragColor = vec4(rgb, coverage) * planetVisibility", adapted,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CloudTrace_UsesConservativeFootprintAndSharedSceneDepthContract()
    {
        var adapted = ResolveAndAdapt("genesis_clouds.frag");

        Assert.Contains("GENESIS_CLOUD_SCENE_DEPTH_GLSL", adapted, StringComparison.Ordinal);
        Assert.Contains("fwidth(vUv) * 0.25", adapted, StringComparison.Ordinal);
        Assert.Contains("float conservativeDepth = max(", adapted, StringComparison.Ordinal);
        Assert.Contains("csdSceneRayDistanceFromDepth", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("maxCloudOccluderDist", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisCloudTemporal_UsesDepthWindAndNeighborhoodRejection()
    {
        var trace = ResolveAndAdapt("genesis_clouds.frag");
        var temporal = ResolveAndAdapt("genesis_clouds_temporal.frag");

        Assert.Contains("layout(location = 1) out vec4 FragCloudData", trace, StringComparison.Ordinal);
        Assert.Contains("ctEncodeDistance(representativeT)", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("uPrevClouds", trace, StringComparison.Ordinal);
        Assert.Contains("expectedPreviousDistance", temporal, StringComparison.Ordinal);
        Assert.Contains("uWindDelta", temporal, StringComparison.Ordinal);
        Assert.Contains("uCirrusWindDelta", temporal, StringComparison.Ordinal);
        Assert.Contains("cloudNeighborhood", temporal, StringComparison.Ordinal);
        Assert.Contains("trClipHistoryToNeighborhoodYCoCg", temporal, StringComparison.Ordinal);
        Assert.Contains("kindWeight", temporal, StringComparison.Ordinal);
    }

    [Fact]
    public void DumpAdaptedVolumeShaders_ForAngleDebug()
    {
        var outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "agent-tools"));
        Directory.CreateDirectory(outDir);
        foreach (var f in new[]
        {
            "genesis_volume_inject.frag",
            "genesis_volume_inject_lite.frag",
            "genesis_volume_integrate.frag",
            "genesis_volume_integrate_lite.frag"
        })
        {
            var adapted = ResolveAndAdapt(f);
            File.WriteAllText(Path.Combine(outDir, f + ".es.glsl"), adapted);
        }
    }

    private static string ResolveAndAdapt(string fragmentFile, bool useOpenGlEs = true)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "AutoPBR.App", "Rendering", "Shaders"));
        string Read(string name) =>
            File.ReadAllText(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));

        return GlslSourceAdapter.Adapt(
            GlslIncludeResolver.Resolve(fragmentFile, Read),
            ShaderType.FragmentShader,
            useOpenGlEs);
    }
}
