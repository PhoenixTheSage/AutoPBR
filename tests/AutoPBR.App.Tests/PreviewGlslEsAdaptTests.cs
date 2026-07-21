using AutoPBR.App.Rendering.OpenGL;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Tests;

public class PreviewGlslEsAdaptTests
{
    [Fact]
    public void PreparedSource_DefinesAreInsertedAfterVersionHeader()
    {
        var defines = new Dictionary<string, int> { ["GENESIS_ENABLE_SHADOW"] = 1 };
        var prepared = GlslPreparedSourceCache.GetOrPrepare(
            "genesis.vert",
            ShaderType.VertexShader,
            useOpenGlEs: true,
            defines);

        Assert.StartsWith("#version 300 es", prepared.TrimStart(), StringComparison.Ordinal);
        var versionIdx = prepared.IndexOf("#version 300 es", StringComparison.Ordinal);
        var defineIdx = prepared.IndexOf("#define GENESIS_ENABLE_SHADOW", StringComparison.Ordinal);
        Assert.True(versionIdx >= 0);
        Assert.True(defineIdx > versionIdx, "defines must follow #version on GLES");
        Assert.Contains("precision highp float;", prepared, StringComparison.Ordinal);
        var precisionIdx = prepared.IndexOf("precision highp float;", StringComparison.Ordinal);
        Assert.True(defineIdx > precisionIdx, "defines must follow precision qualifiers on GLES");
        Assert.Contains("#define GENESIS_GLES 1", prepared, StringComparison.Ordinal);
    }

    [Fact]
    public void AtmoSky_DoesNotRedefineSkyViewLutWidth()
    {
        foreach (var file in new[] { "atmo_sky.frag", "atmo_skyview.frag", "genesis_clouds.frag" })
        {
            var src = GlslIncludeResolver.Resolve(file, LoadShader);
            var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);
            var count = CountSubstringOccurrences(adapted, "const float SKY_VIEW_LUT_WIDTH");
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public void VolumeInject_MultiOutput_FragColorHasExplicitLocation()
    {
        foreach (var file in new[] { "genesis_volume_inject.frag", "genesis_volume_inject_lite.frag" })
        {
            var src = GlslIncludeResolver.Resolve(file, LoadShader);
            var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);
            Assert.Contains("layout(location = 0) out vec4 FragColor;", adapted, StringComparison.Ordinal);
            Assert.Contains("layout(location = 1) out float FragOccupancy;", adapted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GodRayShadowSparseMarch_DeclaresMarchTOutsideConditionalBranches()
    {
        var raw = GlslIncludeResolver.Resolve("genesis_godrays_shadow.frag", LoadShader);
        var withDefine = "#define GENESIS_GODRAY_SPARSE_MARCH 1\n" + raw;
        var adapted = GlslSourceAdapter.Adapt(withDefine, ShaderType.FragmentShader, useOpenGlEs: true);
        Assert.Contains("float t;", adapted, StringComparison.Ordinal);
        Assert.Contains("t = grSparseMarchT(i, GR_SAMPLES);", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("#endif        vec2 marchUv", adapted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("genesis.frag")]
    [InlineData("genesis_godrays.frag")]
    [InlineData("genesis_godrays_shadow.frag")]
    [InlineData("genesis_godrays_upsample.frag")]
    [InlineData("genesis_scene_present.frag")]
    [InlineData("genesis_clouds.frag")]
    [InlineData("genesis_clouds_upsample.frag")]
    [InlineData("genesis_taa_resolve.frag")]
    [InlineData("genesis_volume_inject.frag")]
    [InlineData("genesis_volume_integrate.frag")]
    [InlineData("atmo_sky.frag")]
    [InlineData("atmo_transmittance.frag")]
    [InlineData("atmo_skyview.frag")]
    public void EsAdaptedFragment_StartsWithEsVersionAndSamplerPrecisions(string fragmentFile)
    {
        var src = GlslIncludeResolver.Resolve(fragmentFile, LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.StartsWith("#version 300 es", adapted.TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain("#version 330 core", adapted, StringComparison.Ordinal);
        Assert.Contains("precision highp sampler2D;", adapted, StringComparison.Ordinal);
        if (adapted.Contains("sampler2DArray", StringComparison.Ordinal))
        {
            Assert.Contains("precision highp sampler2DArray;", adapted, StringComparison.Ordinal);
        }

        if (adapted.Contains("sampler2DShadow", StringComparison.Ordinal))
        {
            Assert.Contains("precision highp sampler2DShadow;", adapted, StringComparison.Ordinal);
        }

        if (adapted.Contains("sampler3D", StringComparison.Ordinal))
        {
            Assert.Contains("precision highp sampler3D;", adapted, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("genesis.vert", ShaderType.VertexShader)]
    [InlineData("genesis.frag", ShaderType.FragmentShader)]
    [InlineData("genesis_shadow.vert", ShaderType.VertexShader)]
    [InlineData("genesis_shadow.frag", ShaderType.FragmentShader)]
    [InlineData("genesis_volume_inject.frag", ShaderType.FragmentShader)]
    [InlineData("genesis_volume_integrate.frag", ShaderType.FragmentShader)]
    public void EsAdaptedShaders_DoNotEnableDesktopOnlyAccelerationDefines(string shaderFile, ShaderType shaderType)
    {
        var src = GlslIncludeResolver.Resolve(shaderFile, LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, shaderType, useOpenGlEs: true);

        Assert.DoesNotContain("GENESIS_GL46", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("GENESIS_USE_SSBO", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("GENESIS_USE_COMPUTE", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("GENESIS_USE_IMAGE_STORE", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("#define GENESIS_ENTITY_SKINNING_SSBO", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("#define GENESIS_MATERIAL_DRAW_RECORD_SSBO", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("writeonly image", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("layout(local_size", adapted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("genesis.vert")]
    [InlineData("genesis_shadow.vert")]
    public void EsAdaptedGenesisVertex_KeepsDrawRecordWriteHelperWithoutSsbo(string shaderFile)
    {
        var prepared = GlslPreparedSourceCache.GetOrPrepare(
            shaderFile,
            ShaderType.VertexShader,
            useOpenGlEs: true,
            defines: null);

        Assert.DoesNotContain("#define GENESIS_MATERIAL_DRAW_RECORD_SSBO", prepared, StringComparison.Ordinal);
        Assert.Contains("void genesisWriteDrawRecordIndexVarying()", prepared, StringComparison.Ordinal);
        Assert.Contains("genesisWriteDrawRecordIndexVarying();", prepared, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("genesis.vert")]
    [InlineData("genesis_shadow.vert")]
    public void DesktopPreparedGenesisEntitySkinningSsbo_DefinesStorageBufferVariant(string shaderFile)
    {
        var prepared = GlslPreparedSourceCache.GetOrPrepare(
            shaderFile,
            ShaderType.VertexShader,
            useOpenGlEs: false,
            new Dictionary<string, int> { ["GENESIS_ENTITY_SKINNING_SSBO"] = 1 });

        Assert.StartsWith("#version 330 core", prepared.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("#define GENESIS_ENTITY_SKINNING_SSBO 1", prepared, StringComparison.Ordinal);
        Assert.Contains("#extension GL_ARB_shader_storage_buffer_object : require", prepared, StringComparison.Ordinal);
        Assert.Contains("layout(std430, binding = 5) readonly buffer EntitySkinningBonesSsbo", prepared, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("genesis.vert", ShaderType.VertexShader)]
    [InlineData("genesis.frag", ShaderType.FragmentShader)]
    [InlineData("genesis_shadow.vert", ShaderType.VertexShader)]
    [InlineData("genesis_shadow.frag", ShaderType.FragmentShader)]
    public void DesktopPreparedGenesisMaterialDrawRecordSsbo_DefinesStorageBufferVariant(
        string shaderFile,
        ShaderType shaderType)
    {
        var prepared = GlslPreparedSourceCache.GetOrPrepare(
            shaderFile,
            shaderType,
            useOpenGlEs: false,
            new Dictionary<string, int> { ["GENESIS_MATERIAL_DRAW_RECORD_SSBO"] = 1 });

        Assert.StartsWith("#version 330 core", prepared.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("#define GENESIS_MATERIAL_DRAW_RECORD_SSBO 1", prepared, StringComparison.Ordinal);
        Assert.Contains("#extension GL_ARB_shader_storage_buffer_object : require", prepared, StringComparison.Ordinal);
        Assert.Contains("layout(std430, binding = 8) readonly buffer GenesisMaterialDrawRecords", prepared, StringComparison.Ordinal);
        Assert.Contains("uGenesisMaterialDrawRecords", prepared, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisProgramDefines_KeepDesktopSsboLanesCapabilityGated()
    {
        var fallback = OpenGlPreviewBackend.TestBuildGenesisProgramDefines(
            entitySkinningSsbo: false,
            materialDrawRecordSsbo: false);
        Assert.DoesNotContain("GENESIS_ENTITY_SKINNING_SSBO", fallback.Keys);
        Assert.DoesNotContain("GENESIS_MATERIAL_DRAW_RECORD_SSBO", fallback.Keys);

        var desktop = OpenGlPreviewBackend.TestBuildGenesisProgramDefines(
            entitySkinningSsbo: true,
            materialDrawRecordSsbo: true,
            drawRecordBaseInstance: true,
            materialTextureArrays: true);
        Assert.Equal(1, desktop["GENESIS_ENTITY_SKINNING_SSBO"]);
        Assert.Equal(1, desktop["GENESIS_MATERIAL_DRAW_RECORD_SSBO"]);
        Assert.Equal(1, desktop["GENESIS_DRAW_RECORD_BASE_INSTANCE"]);
        Assert.Equal(1, desktop["GENESIS_MATERIAL_TEXTURE_ARRAYS"]);

        var impossible = OpenGlPreviewBackend.TestBuildGenesisProgramDefines(
            entitySkinningSsbo: false,
            materialDrawRecordSsbo: false,
            materialTextureArrays: true);
        Assert.DoesNotContain("GENESIS_MATERIAL_TEXTURE_ARRAYS", impossible.Keys);
    }

    [Theory]
    [InlineData("genesis.frag", ShaderType.FragmentShader)]
    [InlineData("genesis_shadow.frag", ShaderType.FragmentShader)]
    public void DesktopPreparedGenesisMaterialTextureArrays_DefinesArraySamplers(
        string shaderFile,
        ShaderType shaderType)
    {
        var prepared = GlslPreparedSourceCache.GetOrPrepare(
            shaderFile,
            shaderType,
            useOpenGlEs: false,
            new Dictionary<string, int>
            {
                ["GENESIS_MATERIAL_DRAW_RECORD_SSBO"] = 1,
                ["GENESIS_MATERIAL_TEXTURE_ARRAYS"] = 1,
            });

        Assert.Contains("#define GENESIS_MATERIAL_TEXTURE_ARRAYS 1", prepared, StringComparison.Ordinal);
        Assert.Contains("sampler2DArray", prepared, StringComparison.Ordinal);
        Assert.Contains("uGenesisUseMaterialTextureArray", prepared, StringComparison.Ordinal);
        Assert.Contains("genesisMaterialTextureLayer", prepared, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("genesis.vert", ShaderType.VertexShader, "gl_BaseInstanceARB", "flat out int vGenesisDrawRecordIndex")]
    [InlineData("genesis.frag", ShaderType.FragmentShader, "vGenesisDrawRecordIndex", "flat in int vGenesisDrawRecordIndex")]
    [InlineData("genesis_shadow.vert", ShaderType.VertexShader, "gl_BaseInstanceARB", "flat out int vGenesisDrawRecordIndex")]
    [InlineData("genesis_shadow.frag", ShaderType.FragmentShader, "vGenesisDrawRecordIndex", "flat in int vGenesisDrawRecordIndex")]
    public void DesktopPreparedGenesisDrawRecordBaseInstance_UsesShaderDrawParametersBridge(
        string shaderFile,
        ShaderType shaderType,
        string expectedIndexSource,
        string expectedVarying)
    {
        var prepared = GlslPreparedSourceCache.GetOrPrepare(
            shaderFile,
            shaderType,
            useOpenGlEs: false,
            new Dictionary<string, int>
            {
                ["GENESIS_MATERIAL_DRAW_RECORD_SSBO"] = 1,
                ["GENESIS_DRAW_RECORD_BASE_INSTANCE"] = 1,
            });

        Assert.Contains("#define GENESIS_DRAW_RECORD_BASE_INSTANCE 1", prepared, StringComparison.Ordinal);
        Assert.Contains(expectedIndexSource, prepared, StringComparison.Ordinal);
        Assert.Contains(expectedVarying, prepared, StringComparison.Ordinal);
        if (shaderType == ShaderType.VertexShader)
        {
            Assert.Contains("#extension GL_ARB_shader_draw_parameters : require", prepared, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DesktopPreparedIndirectCommandCompactor_UsesSsboAtomicPath()
    {
        var prepared = GlslPreparedSourceCache.GetOrPrepare(
            "genesis_indirect_compact.comp",
            ShaderType.ComputeShader,
            useOpenGlEs: false);

        Assert.StartsWith("#version 430 core", prepared.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("layout(local_size_x = 64", prepared, StringComparison.Ordinal);
        Assert.Contains("layout(std430, binding = 0) readonly buffer SourceCommands", prepared, StringComparison.Ordinal);
        Assert.Contains("layout(std430, binding = 4) readonly buffer BatchCullRecords", prepared, StringComparison.Ordinal);
        Assert.Contains("uniform vec4 uFrustumPlanes[6]", prepared, StringComparison.Ordinal);
        Assert.Contains("uniform uint uFirstCommand", prepared, StringComparison.Ordinal);
        Assert.Contains("uint uExaminedCommands", prepared, StringComparison.Ordinal);
        Assert.Contains("uniform uint uOutputCapacity", prepared, StringComparison.Ordinal);
        Assert.Contains("uniform int uCollectDiagnostics", prepared, StringComparison.Ordinal);
        Assert.Contains("atomicAdd(uVisibleCommandCount", prepared, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPreparedVolumeInjectCompute_UsesImageStorePath()
    {
        var prepared = GlslPreparedSourceCache.GetOrPrepare(
            "genesis_volume_inject.comp",
            ShaderType.ComputeShader,
            useOpenGlEs: false);

        Assert.StartsWith("#version 430 core", prepared.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;", prepared, StringComparison.Ordinal);
        Assert.Contains("writeonly uniform image2DArray uFroxelOut", prepared, StringComparison.Ordinal);
        Assert.Contains("writeonly uniform image2DArray uFroxelOccupancyOut", prepared, StringComparison.Ordinal);
        Assert.Contains("imageStore(uFroxelOut", prepared, StringComparison.Ordinal);
        Assert.Contains("grShadowGateCascaded", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("#define GENESIS_GLES 1", prepared, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPreparedVolumeInjectCompute_InsertsDefinesAfterComputeVersion()
    {
        var prepared = GlslPreparedSourceCache.GetOrPrepare(
            "genesis_volume_inject.comp",
            ShaderType.ComputeShader,
            useOpenGlEs: false,
            new Dictionary<string, int> { ["GENESIS_VOLUME_COMPUTE_INJECT"] = 1 });

        var versionIdx = prepared.IndexOf("#version 430 core", StringComparison.Ordinal);
        var defineIdx = prepared.IndexOf("#define GENESIS_VOLUME_COMPUTE_INJECT 1", StringComparison.Ordinal);
        Assert.True(versionIdx >= 0);
        Assert.True(defineIdx > versionIdx, "compute defines must follow #version");
    }

    [Fact]
    public void DesktopPreparedLuminanceHistogram_UsesBoundedImageAtomicPath()
    {
        var prepared = GlslPreparedSourceCache.GetOrPrepare(
            "genesis_luminance_histogram.comp",
            ShaderType.ComputeShader,
            useOpenGlEs: false);

        Assert.StartsWith("#version 430 core", prepared.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("readonly uniform image2D uSourceImage", prepared, StringComparison.Ordinal);
        Assert.Contains("layout(std430, binding = 0) buffer LuminanceHistogram", prepared, StringComparison.Ordinal);
        Assert.Contains("uniform uint uSampleCapacity", prepared, StringComparison.Ordinal);
        Assert.Contains("atomicAdd(uBins[binIndex]", prepared, StringComparison.Ordinal);
        Assert.Contains("atomicAdd(uOverflowCount", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("#define GENESIS_GLES 1", prepared, StringComparison.Ordinal);
    }

    [Fact]
    public void EsAdapt_StripsNonAsciiCommentChars_PreservingLength()
    {
        const string src = "#version 330 core\n// Beer\u2013Lambert \u2014 mist\nout vec4 c;\nvoid main(){c=vec4(1.0);}\n";
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.DoesNotContain('\u2013', adapted);
        Assert.DoesNotContain('\u2014', adapted);
        foreach (var ch in adapted)
        {
            Assert.True(ch <= '\x7F');
        }
    }

    [Fact]
    public void EsAdaptedGenesis_IncludesSplitSumIblWithoutAtmosphereOnlySymbols()
    {
        var src = GlslIncludeResolver.Resolve("genesis.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("iblEnvBrdfFactor", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("ATM_PI", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisMetalIbl_RetainsPreviewBaseForLabPbrMetalIds()
    {
        var src = GlslIncludeResolver.Resolve("genesis.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("metalPreviewBaseVisibility", adapted, StringComparison.Ordinal);
        Assert.Contains("mat.metallic * metalPreviewBaseVisibility(mat.roughness)", adapted, StringComparison.Ordinal);
        Assert.Contains("metalPreviewIrradiance * albedoLinear * metalBaseVis", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "indirect += iblDiff * albedoLinear * (1.0 - mat.metallic) * uIblStrength;",
            adapted,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisIbl_UsesGeneratedWorldSpaceProbeAndOffscreenSun()
    {
        var src = GlslIncludeResolver.Resolve("genesis.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("previewEnvSunRadiance", adapted, StringComparison.Ordinal);
        Assert.Contains("previewEnvCubemapRadiance", adapted, StringComparison.Ordinal);
        Assert.Contains("previewAmbientProbeIrradiance", adapted, StringComparison.Ordinal);
        Assert.Contains("groundSpecularReceiverFade", adapted, StringComparison.Ordinal);
        Assert.Contains("uIsGroundPass", adapted, StringComparison.Ordinal);
        Assert.Contains("uTerrainPomFadeStart", adapted, StringComparison.Ordinal);
        Assert.Contains("uTerrainPomFadeEnd", adapted, StringComparison.Ordinal);
        Assert.Contains("groundPomFade", adapted, StringComparison.Ordinal);
        Assert.Contains("previewAerialFogRadiance", adapted, StringComparison.Ordinal);
        Assert.Contains("uLightDir, uLightColor, uAtmosphereSunIntensity", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("Sun / punctual highlights come from direct lighting only", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("iblPrefilteredSkyRadianceFallback", adapted, StringComparison.Ordinal);

        // Soft LOD haze keeps day/night inscatter but must not pay per-fragment sky-view LUT samples.
        var ibl = GlslIncludeResolver.Resolve("common/ibl.glsl", LoadShader);
        var fogFn = ibl.IndexOf("vec3 previewAerialFogRadiance", StringComparison.Ordinal);
        Assert.True(fogFn >= 0);
        var fogBody = ibl[fogFn..];
        var fogEnd = fogBody.IndexOf("\nvec3 ", 1, StringComparison.Ordinal);
        if (fogEnd < 0)
        {
            fogEnd = fogBody.Length;
        }

        fogBody = fogBody[..fogEnd];
        Assert.Contains("nightHaze", fogBody, StringComparison.Ordinal);
        Assert.Contains("dayHaze", fogBody, StringComparison.Ordinal);
        Assert.Contains("daylightGate", fogBody, StringComparison.Ordinal);
        Assert.Contains("moonGate", fogBody, StringComparison.Ordinal);
        // Wide luminance twilight ramp (pairs with SceneLightColor horizon blend).
        Assert.Contains("smoothstep(0.08, 0.72, lightLum)", fogBody, StringComparison.Ordinal);
        // Night inscatter must stay modest (full moonlight previously washed the horizon).
        Assert.Contains("0.04, moonGate", fogBody, StringComparison.Ordinal);
        Assert.Contains("lightColor * scatter", fogBody, StringComparison.Ordinal);
        Assert.DoesNotContain("sampleSkyViewLutSrgb", fogBody, StringComparison.Ordinal);
        Assert.DoesNotContain("previewEnvSkyGroundRadianceCtx", fogBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisShadowSampling_UsesSoftPcfKernel()
    {
        var shadowSrc = GlslIncludeResolver.Resolve("common/shadow.glsl", LoadShader);
        var shadowAdapted = GlslSourceAdapter.Adapt(shadowSrc, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("sampleSceneShadowCascaded", shadowAdapted, StringComparison.Ordinal);
        Assert.Contains("sampleShadowPcfSoft", shadowAdapted, StringComparison.Ordinal);
        Assert.Contains("sampleSceneShadowFromClip", shadowAdapted, StringComparison.Ordinal);
        Assert.Contains("shadowUvEdgeWeight", shadowAdapted, StringComparison.Ordinal);
        Assert.Contains("shadowRangeFade", shadowAdapted, StringComparison.Ordinal);
        Assert.Contains("lightClip.w <= 0.0", shadowAdapted, StringComparison.Ordinal);

        var genesisSrc = "#define GENESIS_ENABLE_SHADOW 1\n" +
                         GlslIncludeResolver.Resolve("genesis.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(genesisSrc, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("uShadowMapNear", adapted, StringComparison.Ordinal);
        Assert.Contains("uShadowMapMid", adapted, StringComparison.Ordinal);
        Assert.Contains("uLightViewProjMid", adapted, StringComparison.Ordinal);
        Assert.Contains("uEnableShadowCascades", adapted, StringComparison.Ordinal);
        Assert.Contains("uCascadeSplitDistance", adapted, StringComparison.Ordinal);
        Assert.Contains("uCascadeMidSplitDistance", adapted, StringComparison.Ordinal);
        Assert.Contains("uCascadeBlendWidth", adapted, StringComparison.Ordinal);
        Assert.Contains("uShadowDistance", adapted, StringComparison.Ordinal);
        Assert.Contains("uShadowFadeStart", adapted, StringComparison.Ordinal);
        Assert.Contains("sampleSceneShadowCascaded", adapted, StringComparison.Ordinal);
        Assert.Contains("mix(nearVis, midVis, nearMidT)", shadowAdapted, StringComparison.Ordinal);
        Assert.Contains("mix(midVis, farVis, midFarT)", shadowAdapted, StringComparison.Ordinal);
        // Far cascade must still occlude near-camera receivers when hills sit outside near/mid orthos.
        Assert.Contains("min(min(nearVis, midVis), farVis)", shadowAdapted, StringComparison.Ordinal);
        Assert.Contains("min(min(blended, midVis), farVis)", shadowAdapted, StringComparison.Ordinal);
        Assert.Contains("min(blended, farVis)", shadowAdapted, StringComparison.Ordinal);
        Assert.Contains("min(softnessTexels, 1.0)", shadowAdapted, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudMarch_UsesLightOpticalDepthFromBase()
    {
        var clouds = GlslIncludeResolver.Resolve("genesis_clouds.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(clouds, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("vcLightOpticalDepthFromBase", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void GodRaySparseMarch_CompilesWithDefineOnEs()
    {
        var raw = GlslIncludeResolver.Resolve("genesis_godrays.frag", LoadShader);
        var withDefine = "#define GENESIS_GODRAY_SPARSE_MARCH 1\n" + raw;
        var adapted = GlslSourceAdapter.Adapt(withDefine, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("grSparseMarchT", adapted, StringComparison.Ordinal);
        Assert.Contains("grSparseMarchSkipOddStepScreen", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void GodRayShadowSparseMarch_CompilesWithDefineOnEs()
    {
        var raw = GlslIncludeResolver.Resolve("genesis_godrays_shadow.frag", LoadShader);
        var withDefine = "#define GENESIS_GODRAY_SPARSE_MARCH 1\n" + raw;
        var adapted = GlslSourceAdapter.Adapt(withDefine, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("grSparseMarchSkipOddStepShadow", adapted, StringComparison.Ordinal);
        Assert.Contains("uCascadeBlendWidth", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeIntegrate_TemporalVariant_IsGuardedByCompileDefine()
    {
        var raw = GlslIncludeResolver.Resolve("genesis_volume_integrate.frag", LoadShader);
        var prevUse = raw.IndexOf("texture(uPrevIntegrate", StringComparison.Ordinal);
        Assert.True(prevUse >= 0, "temporal history sample must exist behind compile define");
        var guard = raw.LastIndexOf("#ifdef GENESIS_VOLUME_TEMPORAL", prevUse, StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < prevUse,
            "uPrevIntegrate sampling must be wrapped in #ifdef GENESIS_VOLUME_TEMPORAL");
    }

    [Fact]
    public void LabPbrMetalF0_UsesConstTableLookup()
    {
        var src = GlslIncludeResolver.Resolve("common/material_labpbr.glsl", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("LABPBR_METAL_F0", adapted, StringComparison.Ordinal);
        Assert.Contains("gIndex - 230", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeIntegrate_IncludesSparseOccupancyMarch()
    {
        var src = GlslIncludeResolver.Resolve("genesis_volume_integrate.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("uFroxelOccupancy", adapted, StringComparison.Ordinal);
        Assert.Contains("viSampleFroxelOccupancy", adapted, StringComparison.Ordinal);
        Assert.Contains("viSparseMarchT", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeInject_WritesOccupancyTarget()
    {
        var src = GlslIncludeResolver.Resolve("genesis_volume_inject.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("FragOccupancy", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeIntegrate_MediumpAccum_PreservesQualifierOnEs()
    {
        var raw = GlslIncludeResolver.Resolve("genesis_volume_integrate.frag", LoadShader);
        var withDefine = "#define GENESIS_VOLUME_MEDIUMP_ACCUM 1\n" + raw;
        var adapted = GlslSourceAdapter.Adapt(withDefine, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("mediump vec3 accum", adapted, StringComparison.Ordinal);
        Assert.Contains("mediump float transmittance", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisParallax_UsesTileLocalPomWithoutPostClamp()
    {
        var src = GlslIncludeResolver.Resolve("genesis.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("textureGrad(heightTex", adapted, StringComparison.Ordinal);
        Assert.Contains("sampleGenesisAlbedoGrad(uv, uvDx, uvDy)", adapted, StringComparison.Ordinal);
        Assert.Contains("sampleGenesisAlbedo(vUv)", adapted, StringComparison.Ordinal);
        Assert.Contains("pomActiveEarly", adapted, StringComparison.Ordinal);
        Assert.Contains("sampleGenesisNormalGrad(uv, dx, dy)", adapted, StringComparison.Ordinal);
        Assert.Contains("sampleGenesisSpecularGrad(uv, uvDx, uvDy)", adapted, StringComparison.Ordinal);
        Assert.Contains("pomTileUv(tileBase", adapted, StringComparison.Ordinal);
        Assert.Contains("tileBase + fract(localUv)", adapted, StringComparison.Ordinal);
        Assert.Contains("uParallaxTraceLayers", adapted, StringComparison.Ordinal);
        Assert.Contains("uParallaxRefineSteps", adapted, StringComparison.Ordinal);
        Assert.Contains("uParallaxShadowSamples", adapted, StringComparison.Ordinal);
        Assert.Contains("uParallaxShadowSoftness", adapted, StringComparison.Ordinal);
        Assert.Contains("uParallaxUvScale", adapted, StringComparison.Ordinal);
        Assert.Contains(
            "pomUvDisplacementScale(strength) * clamp(genesisParallaxUvScale(uParallaxUvScale), 0.02, 1.0)",
            adapted,
            StringComparison.Ordinal);
        Assert.Contains("uParallaxHeightTexSize", adapted, StringComparison.Ordinal);
        Assert.Contains("if (Vtan.z <= 0.0)", adapted, StringComparison.Ordinal);
        Assert.Contains("Vtan.xy / max(Vtan.z, GEN_POM_MIN_VIEW_Z)", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("if (Vtan.z < GEN_POM_MIN_VIEW_Z)", adapted, StringComparison.Ordinal);
        Assert.Contains("if (curLayer < sampleH)", adapted, StringComparison.Ordinal);
        Assert.Contains("float delta = curLayer - sampleH;", adapted, StringComparison.Ordinal);
        Assert.Contains("smoothstep(0.0, softWidth, delta)", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("uv = clamp(uvDisp", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("clamp(localUv", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("cavityAmt", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("sampleHeight01(sampler2D heightTex, vec2 uv)", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisTessellation_UsesTrianglePatchDisplacementContract()
    {
        var control = GlslIncludeResolver.Resolve("genesis.tcs", LoadShader);
        var evaluation = GlslIncludeResolver.Resolve("genesis.tes", LoadShader);

        Assert.Contains("#version 400 core", control, StringComparison.Ordinal);
        Assert.Contains("layout(vertices = 3) out;", control, StringComparison.Ordinal);
        Assert.Contains("gl_TessLevelOuter", control, StringComparison.Ordinal);
        Assert.Contains("genesisEnableTessellationDisplacement(uEnableTessellationDisplacement)", control, StringComparison.Ordinal);
        Assert.Contains("genesisHasHeight(uHasHeight)", control, StringComparison.Ordinal);
        Assert.Contains("genesisWriteDrawRecordIndexVarying()", control, StringComparison.Ordinal);
        Assert.Contains("clamp(uTessellationLevel, 1.0, 16.0)", control, StringComparison.Ordinal);

        Assert.Contains("#version 400 core", evaluation, StringComparison.Ordinal);
        Assert.Contains("layout(triangles, equal_spacing, ccw) in;", evaluation, StringComparison.Ordinal);
        Assert.Contains("textureLod(uHeight, uv, 0.0).r", evaluation, StringComparison.Ordinal);
        Assert.Contains("textureLod(\n                uHeightArray", evaluation, StringComparison.Ordinal);
        Assert.Contains("genesisMaterialTextureLayer(0)", evaluation, StringComparison.Ordinal);
        Assert.Contains("max(rawHeight - 0.5, 0.0) * 2.0", evaluation, StringComparison.Ordinal);
        Assert.Contains("uTessellationDisplacementStrength", evaluation, StringComparison.Ordinal);
        Assert.Contains("uLightViewProj * vec4(worldPos, 1.0)", evaluation, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenePresent_AndGenesis_AdaptHdrPresentUniforms()
    {
        var present = GlslSourceAdapter.Adapt(
            GlslIncludeResolver.Resolve("genesis_scene_present.frag", LoadShader),
            ShaderType.FragmentShader,
            useOpenGlEs: true);
        Assert.Contains("uHdrPresent", present, StringComparison.Ordinal);
        Assert.Contains("uSceneIsLinear", present, StringComparison.Ordinal);
        Assert.Contains("uHdrPaperWhiteNits", present, StringComparison.Ordinal);
        Assert.Contains("presentEncodeScRgb", present, StringComparison.Ordinal);

        var genesis = GlslSourceAdapter.Adapt(
            GlslIncludeResolver.Resolve("genesis.frag", LoadShader),
            ShaderType.FragmentShader,
            useOpenGlEs: true);
        Assert.Contains("uHdrPresent", genesis, StringComparison.Ordinal);
        Assert.Contains("uHdrPaperWhiteNits", genesis, StringComparison.Ordinal);
        // Fog is authored in post-ACES space; HDR recovers linear via inverse so present ACES matches SDR.
        Assert.Contains("applyTerrainAerialFog(tonemapAcesNarkowicz(hdr))", genesis, StringComparison.Ordinal);
        Assert.Contains("inverseTonemapAcesNarkowicz(foggedMapped)", genesis, StringComparison.Ordinal);
        // Night HDR undoes paper-white scale on fog midtones so the LOD band matches SDR.
        Assert.Contains("1.0 / max(paperScale, 1.0)", genesis, StringComparison.Ordinal);

        var tonemap = GlslIncludeResolver.Resolve("common/tonemap.glsl", LoadShader);
        Assert.Contains("vec3 inverseTonemapAcesNarkowicz", tonemap, StringComparison.Ordinal);

        var sky = GlslSourceAdapter.Adapt(
            GlslIncludeResolver.Resolve("atmo_sky.frag", LoadShader),
            ShaderType.FragmentShader,
            useOpenGlEs: true);
        Assert.Contains("uHdrPresent", sky, StringComparison.Ordinal);

        var taa = GlslSourceAdapter.Adapt(
            GlslIncludeResolver.Resolve("genesis_taa_resolve.frag", LoadShader),
            ShaderType.FragmentShader,
            useOpenGlEs: true);
        Assert.Contains("uHdrPresent", taa, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisTaaResolve_UsesReactiveYcocgHistoryClip()
    {
        var src = GlslIncludeResolver.Resolve("genesis_taa_resolve.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("uTaaSignal", adapted, StringComparison.Ordinal);
        Assert.Contains("uHasTaaSignal", adapted, StringComparison.Ordinal);
        Assert.Contains("objectMotion", adapted, StringComparison.Ordinal);
        Assert.Contains("smoothstep(0.08, 0.75, objectMotion)", adapted, StringComparison.Ordinal);
        Assert.Contains("smoothstep(0.18, 0.90, reactivity)", adapted, StringComparison.Ordinal);
        Assert.Contains("taaFetchNeighborhood3x3", adapted, StringComparison.Ordinal);
        Assert.Contains("trNeighborhoodMinMax3YCoCgFromTaps", adapted, StringComparison.Ordinal);
        Assert.Contains("trClipHistoryToNeighborhoodYCoCg", adapted, StringComparison.Ordinal);
        Assert.Contains("trLuminanceReactiveWeight", adapted, StringComparison.Ordinal);
        Assert.Contains("trDepthEdgeWeight", adapted, StringComparison.Ordinal);
        Assert.Contains("stableTemporal", adapted, StringComparison.Ordinal);
        Assert.Contains("uStableTemporalBoost", adapted, StringComparison.Ordinal);
        Assert.Contains("uMaxStableTemporal", adapted, StringComparison.Ordinal);
        Assert.Contains("uTaaSharpenStrength", adapted, StringComparison.Ordinal);
        Assert.Contains("uDepthEdgeHistoryFloor", adapted, StringComparison.Ordinal);
        Assert.Contains("uEdgeAaBlend", adapted, StringComparison.Ordinal);
        Assert.Contains("uCurrentJitterPixels", adapted, StringComparison.Ordinal);
        Assert.Contains("uCaptureTexelSize", adapted, StringComparison.Ordinal);
        Assert.Contains("uSourceFilterStrength", adapted, StringComparison.Ordinal);
        Assert.Contains("uSilhouetteHistoryWeight", adapted, StringComparison.Ordinal);
        Assert.Contains("uFxaaEdgeStrength", adapted, StringComparison.Ordinal);
        Assert.Contains("uFxaaLumaEdgeStrength", adapted, StringComparison.Ordinal);
        Assert.Contains("uFxaaLumaThreshold", adapted, StringComparison.Ordinal);
        Assert.Contains("uForceFxaa", adapted, StringComparison.Ordinal);
        Assert.Contains("taaCurrentResolveFilter", adapted, StringComparison.Ordinal);
        Assert.Contains("taaFxaaLite", adapted, StringComparison.Ordinal);
        Assert.Contains("taaTentBlur3x3", adapted, StringComparison.Ordinal);
        Assert.Contains("taaMorphologicalEdgeBlendFromTaps", adapted, StringComparison.Ordinal);
        Assert.Contains("taaClosestGeometryDepthFromTaps", adapted, StringComparison.Ordinal);
        Assert.Contains("taaLumaEdgeMaskFromTaps", adapted, StringComparison.Ordinal);
        Assert.Contains("depthGeometryW", adapted, StringComparison.Ordinal);
        Assert.Contains("signalGeometryW", adapted, StringComparison.Ordinal);
        Assert.Contains("silhouetteW", adapted, StringComparison.Ordinal);
        Assert.Contains("depthFxaaMask", adapted, StringComparison.Ordinal);
        Assert.Contains("lumaFxaaMask", adapted, StringComparison.Ordinal);
        Assert.Contains("geometryFxaaW", adapted, StringComparison.Ordinal);
        Assert.Contains("fxaaEdgeMask", adapted, StringComparison.Ordinal);
        Assert.Contains("edgeHistoryW", adapted, StringComparison.Ordinal);
        Assert.Contains("postFxaaW", adapted, StringComparison.Ordinal);
        Assert.Contains("acrossNormal", adapted, StringComparison.Ordinal);
        Assert.Contains("depthGrad", adapted, StringComparison.Ordinal);
        Assert.Contains("forceFullFrame", adapted, StringComparison.Ordinal);
        Assert.Contains("uForceFxaa > 0", adapted, StringComparison.Ordinal);
        Assert.Contains("thresholdLow", adapted, StringComparison.Ordinal);
        Assert.Contains("edgeMask * strength", adapted, StringComparison.Ordinal);
        Assert.Contains("coverageW", adapted, StringComparison.Ordinal);
        Assert.Contains("reprojectionDepth", adapted, StringComparison.Ordinal);
        Assert.Contains("rawDepthEdgeW", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporalReproject_ProvidesYcocgAndReactiveHelpers()
    {
        var src = GlslIncludeResolver.Resolve("common/temporal_reproject.glsl", LoadShader);

        Assert.Contains("vec3 trRgbToYCoCg", src, StringComparison.Ordinal);
        Assert.Contains("vec3 trYCoCgToRgb", src, StringComparison.Ordinal);
        Assert.Contains("float trLuminanceReactiveWeight", src, StringComparison.Ordinal);
        Assert.Contains("float trDepthEdgeWeight", src, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisFragment_WritesTaaSignalForGeometryReactivity()
    {
        var src = GlslIncludeResolver.Resolve("genesis.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("layout(location = 1) out vec4 TaaSignal", adapted, StringComparison.Ordinal);
        Assert.Contains("alphaEdge", adapted, StringComparison.Ordinal);
        Assert.Contains("vPrevClip", adapted, StringComparison.Ordinal);
        Assert.Contains("motion = clamp", adapted, StringComparison.Ordinal);
        Assert.Contains("genesisEntityAlphaMode(uEntityAlphaMode) == 1", adapted, StringComparison.Ordinal);
        Assert.Contains("genesisEntityAlphaMode(uEntityAlphaMode) == 2", adapted, StringComparison.Ordinal);
        Assert.Contains("TaaSignal = vec4", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisFragment_GatesSpecularLobeAndDithersSrgbOutput()
    {
        var src = GlslIncludeResolver.Resolve("genesis.frag", LoadShader);
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("#if defined(GENESIS_ENABLE_SPECULAR_MAP)", adapted, StringComparison.Ordinal);
        Assert.Contains("float specLobe = 1.0;", adapted, StringComparison.Ordinal);
        Assert.Contains("br.specular *= groundSpecFade * specLobe;", adapted, StringComparison.Ordinal);
        Assert.Contains("ditherSrgb8(linearToSrgb(mapped), gl_FragCoord.xy)", adapted, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisVertex_EmitsPreviousClipForTaaMotionSignal()
    {
        var src = GlslIncludeResolver.Resolve("genesis.vert", LoadShader);

        Assert.Contains("uniform mat4 uPrevModel", src, StringComparison.Ordinal);
        Assert.Contains("uniform mat4 uPrevViewProj", src, StringComparison.Ordinal);
        Assert.Contains("uniform mat4 uTaaCurrViewProj", src, StringComparison.Ordinal);
        Assert.Contains("EntityPrevSkinningBones", src, StringComparison.Ordinal);
        Assert.Contains("EntitySkinningNormals", src, StringComparison.Ordinal);
        Assert.Contains("uNormalBoneMatrices", src, StringComparison.Ordinal);
        Assert.DoesNotContain("inverse(bone)", src, StringComparison.Ordinal);
        Assert.Contains("uEntityPrevBonePaletteValid", src, StringComparison.Ordinal);
        Assert.Contains("uniform vec2 uTextureAtlasScale", src, StringComparison.Ordinal);
        Assert.Contains("vUv = aUv * genesisTextureAtlasScale(uTextureAtlasScale)", src, StringComparison.Ordinal);
        Assert.Contains("prevEntityPos = prevBone * vec4(aPos, 1.0)", src, StringComparison.Ordinal);
        Assert.Contains("out vec4 vPrevClip", src, StringComparison.Ordinal);
        Assert.Contains("vCurrClip = uTaaCurrViewProj * wp", src, StringComparison.Ordinal);
        Assert.Contains("vPrevClip = uPrevViewProj * uPrevModel * prevEntityPos", src, StringComparison.Ordinal);
        Assert.Contains("gl_Position = clip", src, StringComparison.Ordinal);
    }

    [Fact]
    public void EsAdapt_LeavesAsciiSourceUnchangedAfterHeaderSwap()
    {
        const string src = "#version 330 core\n// plain ascii\nout vec4 c;\nvoid main(){c=vec4(1.0);}\n";
        var adapted = GlslSourceAdapter.Adapt(src, ShaderType.FragmentShader, useOpenGlEs: true);

        Assert.Contains("// plain ascii", adapted, StringComparison.Ordinal);
    }

    private static int CountSubstringOccurrences(string source, string needle)
    {
        var count = 0;
        var start = 0;
        while ((start = source.IndexOf(needle, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += needle.Length;
        }

        return count;
    }

    private static string LoadShader(string fileName) => LoadShaderCore(fileName, ThisFilePath());

    private static string ThisFilePath([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "") =>
        sourceFilePath;

    private static string LoadShaderCore(string fileName, string sourceFilePath)
    {
        var relative = fileName.Replace('/', Path.DirectorySeparatorChar);
        var sourceDir = Path.GetDirectoryName(sourceFilePath) ?? string.Empty;
        foreach (var start in new[] { sourceDir, AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var root = Path.Combine(dir.FullName, "src", "AutoPBR.App", "Rendering", "Shaders");
                var path = Path.Combine(root, relative);
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                dir = dir.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate shader source '{fileName}'.");
    }
}
