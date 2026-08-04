using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

using AutoPBR.PreviewGpuAssets;

using Avalonia.OpenGL;

using Silk.NET.OpenGL;

using System.Numerics;

namespace AutoPBR.App.Tests;

public sealed class PreviewCloudLiveGlSmokeTests
{
    [Fact]
    public void HiddenWglContext_GeneratesConservativeBorderedSparseCloudBricks()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("AUTOPBR_RUN_LIVE_GL_SMOKE"),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var diagnostics = new List<string>();
        using var context = PreviewDesktopWglContext.TryCreate(
            [new GlVersion(GlProfileType.OpenGL, 4, 6)],
            IntPtr.Zero,
            diagnostics.Add,
            probePresentationAdapter: false);
        Assert.NotNull(context);

        context!.Invoke(() =>
        {
            using (context.BindOnOwnerThread())
            {
                var gl = context.Gl;
                var caps = PreviewGlCapabilities.FromGl(
                    gl,
                    useOpenGlEs: false,
                    context.VersionString);
                if (!caps.CanUseSparseCloudVolumes)
                {
                    return;
                }

                var compile = new GlShaderCompileContext(
                    gl,
                    useOpenGlEs: false,
                    caps.Vendor,
                    caps.Renderer);
                using var program = compile.CreateComputeProgram(
                    "genesis_sparse_cloud_brick_generate.comp",
                    out var shaderError,
                    "cq4.4-sparse-brick-live-smoke");
                Assert.True(
                    program.IsValid,
                    "CQ4.4 sparse brick compute shader failed: " + shaderError);
                Assert.True(
                    PreviewSparseCloudTemplateAssetLoader.TryLoad(
                        out var templates,
                        out var templateReason),
                    templateReason);
                Assert.True(
                    GlSparseCloudVolumeResources.TryCreate(
                        gl,
                        caps,
                        out var resources,
                        out var resourceReason),
                    resourceReason);
                Assert.NotNull(resources);
                using (resources)
                {
                    Assert.True(
                        GlSparseCloudBrickGenerator.TryCreate(
                            gl,
                            program,
                            templates,
                            out var generator,
                            out var generatorReason),
                        generatorReason);
                    Assert.NotNull(generator);
                    using (generator)
                    {
                        var allocator =
                            new PreviewSparseCloudBrickAllocator();
                        PreviewSparseCloudLogicalBrickKey[] adjacent =
                        [
                            new(0, 2, 2, 4),
                            new(0, 3, 2, 4),
                            new(0, 2, 100, 4),
                        ];
                        var inputs =
                            new PreviewSparseCloudBrickGenerationInputs(
                                Frame: 7,
                                CloudBaseWorldY: 0f,
                                CloudTopWorldY: 128f,
                                Density: 1.5f,
                                CoverageScale: 1.5f,
                                VolumeSize: 178f,
                                WindOffset: Vector3.Zero,
                                WeatherTexture: 0);
                        Assert.True(
                            generator.TryDispatch(
                                resources!,
                                allocator,
                                adjacent,
                                inputs,
                                out var dispatched,
                                out var dispatchReason),
                            dispatchReason);
                        Assert.Equal(3, dispatched);
                        Assert.True(generator.HasPendingGeneration);

                        gl.Finish();
                        Assert.True(
                            generator.TryCollectCompleted(
                                out var completed,
                                out var completionReason),
                            completionReason);
                        Assert.Equal(3, completed.Count);
                        Assert.False(generator.HasPendingGeneration);
                        foreach (var record in completed)
                        {
                            Assert.True(
                                allocator.MarkResident(
                                    record.Key,
                                    generator.GenerationId,
                                    visibleFrame: 8));
                        }

                        var atlas = ReadRg8Texture3D(
                            gl,
                            resources!.AtlasTextureHandle,
                            PreviewSparseCloudVolumeContract.AtlasTexelSize);
                        var first = ExtractSparseBrick(
                            atlas,
                            completed[0].PhysicalBrickIndex);
                        var second = ExtractSparseBrick(
                            atlas,
                            completed[1].PhysicalBrickIndex);
                        var empty = ExtractSparseBrick(
                            atlas,
                            completed[2].PhysicalBrickIndex);
                        Assert.True(
                            PreviewSparseCloudBrickGenerationContract
                                .ValidateConservativeBrick(
                                    first,
                                    out var firstReason),
                            firstReason);
                        Assert.True(
                            PreviewSparseCloudBrickGenerationContract
                                .ValidateConservativeBrick(
                                    second,
                                    out var secondReason),
                            secondReason);
                        AssertSparseBrickSharedXBorderMatches(first, second);
                        Assert.Contains(
                            first.Where((_, index) => index % 2 == 0),
                            density => density != 0);
                        Assert.True(
                            PreviewSparseCloudBrickGenerationContract
                                .ValidateConservativeBrick(
                                    empty,
                                    out var emptyReason),
                            emptyReason);
                        Assert.Equal("valid-empty", emptyReason);
                        Assert.All(
                            empty.Where((_, index) => index % 2 == 0),
                            density => Assert.Equal(0, density));
                    }
                }

                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
        });
    }

    [Fact]
    public void HiddenWglContext_TraversesSparseCloudPagesWithoutSkippingDensity()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("AUTOPBR_RUN_LIVE_GL_SMOKE"),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var diagnostics = new List<string>();
        using var context = PreviewDesktopWglContext.TryCreate(
            [new GlVersion(GlProfileType.OpenGL, 4, 6)],
            IntPtr.Zero,
            diagnostics.Add,
            probePresentationAdapter: false);
        Assert.NotNull(context);

        context!.Invoke(() =>
        {
            using (context.BindOnOwnerThread())
            {
                var gl = context.Gl;
                var caps = PreviewGlCapabilities.FromGl(
                    gl,
                    useOpenGlEs: false,
                    context.VersionString);
                if (!caps.CanUseSparseCloudVolumes)
                {
                    return;
                }

                var compile = new GlShaderCompileContext(
                    gl,
                    useOpenGlEs: false,
                    caps.Vendor,
                    caps.Renderer);
                using var program = compile.CreateComputeProgram(
                    "genesis_sparse_cloud_traversal_validate.comp",
                    out var shaderError,
                    "cq4.5-sparse-traversal-live-smoke");
                Assert.True(
                    program.IsValid,
                    "CQ4.5 sparse traversal compute shader failed: " +
                    shaderError);
                Assert.True(
                    GlSparseCloudVolumeResources.TryCreate(
                        gl,
                        caps,
                        out var resources,
                        out var resourceReason),
                    resourceReason);
                Assert.NotNull(resources);
                using (resources)
                {
                    UploadSparseTraversalFixture(gl, resources!);
                    var output = DispatchSparseTraversalFixture(
                        gl,
                        program,
                        resources);
                    Assert.InRange(output[0], 0f, 10f);
                    Assert.InRange(output[1], 0f, 1f);
                    Assert.Equal(0f, output[2]);
                    Assert.Equal(1f, output[3]);
                    Assert.True(
                        output[5] > 0f,
                        "The fixed ray did not take a conservative-distance step.");
                    Assert.True(
                        output[6] > 0f,
                        "The fixed ray did not enter boundary/fine evaluation.");
                    Assert.Equal(0f, output[7]);
                }

                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
        });
    }

    [Fact]
    public void HiddenWglContext_CompilesFlatContinuousWorldCloudShaders()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AUTOPBR_RUN_LIVE_GL_SMOKE"), "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var diagnostics = new List<string>();
        using var context = PreviewDesktopWglContext.TryCreate(
            [new GlVersion(GlProfileType.OpenGL, 4, 6), new GlVersion(GlProfileType.OpenGL, 3, 3)],
            IntPtr.Zero,
            diagnostics.Add,
            probePresentationAdapter: false);
        Assert.NotNull(context);

        context!.Invoke(() =>
        {
            using (context.BindOnOwnerThread())
            {
                var gl = context.Gl;
                var caps = PreviewGlCapabilities.FromGl(gl, useOpenGlEs: false, context.VersionString);
                var cq40Backend = PreviewSparseCloudBackendPolicy.Select(
                    PreviewVolumetricQuality.Cinematic,
                    caps,
                    forceProceduralLayer: false,
                    sparseResourcesReady: false,
                    sparseRuntimeFaulted: false);
                Assert.Equal(
                    PreviewCloudDensityBackend.SparseVoxel,
                    cq40Backend.RequestedBackend);
                Assert.Equal(
                    PreviewCloudDensityBackend.ProceduralLayer,
                    cq40Backend.ActiveBackend);
                Assert.Equal(
                    caps.CanUseSparseCloudVolumes,
                    cq40Backend.CapabilityEligible);
                if (caps.CanUseSparseCloudVolumes)
                {
                    Assert.Equal(
                        PreviewSparseCloudFallbackReason.SparseResourcesNotReady,
                        cq40Backend.FallbackReason);
                    Assert.True(
                        PreviewSparseCloudTemplateAssetLoader.TryLoad(
                            out var sparseTemplates,
                            out var sparseTemplateReason),
                        sparseTemplateReason);
                    Assert.Equal(12, sparseTemplates.Templates.Count);

                    Assert.False(
                        GlSparseCloudVolumeResources.TryCreate(
                            gl,
                            caps,
                            failAfterTextureAllocation: 2,
                            out var failedSparseResources,
                            out var injectedAllocationReason));
                    Assert.Null(failedSparseResources);
                    Assert.Contains(
                        "injected-failure-after-2-textures",
                        injectedAllocationReason,
                        StringComparison.Ordinal);

                    Assert.True(
                        GlSparseCloudVolumeResources.TryCreate(
                            gl,
                            caps,
                            out var sparseResources,
                            out var sparseAllocationReason),
                        sparseAllocationReason);
                    Assert.NotNull(sparseResources);
                    using (sparseResources)
                    {
                        Assert.True(sparseResources.IsAllocated);
                        Assert.False(sparseResources.IsSamplingReady);
                        Assert.Equal(0, sparseResources.PublishedGenerationId);
                        Assert.NotEqual(0u, sparseResources.AtlasTextureHandle);
                        var textureHandles = new HashSet<uint>
                        {
                            sparseResources.AtlasTextureHandle,
                        };
                        for (var level = 0;
                             level <
                             PreviewSparseCloudVolumeContract.ClipmapCount;
                             level++)
                        {
                            Assert.True(
                                textureHandles.Add(
                                    sparseResources
                                        .GetActivePageTableHandle(level)));
                            Assert.True(
                                textureHandles.Add(
                                    sparseResources
                                        .GetBuildPageTableHandle(level)));
                        }

                        Assert.Equal(7, textureHandles.Count);
                        Assert.True(
                            sparseResources.MemoryAccounting.IsWithinBudget);

                        var activeBefore =
                            sparseResources.GetActivePageTableHandle(0);
                        var buildBefore =
                            sparseResources.GetBuildPageTableHandle(0);
                        var clipmaps =
                            new PreviewSparseCloudClipmapController();
                        var clipmapUpdate = clipmaps.Update(
                            new Vector3(4f, 24f, -8f),
                            Vector3.UnitZ,
                            cloudVerticalCenterWorldY: 30f,
                            ReadOnlySpan<Vector4>.Empty,
                            frame: 0);
                        Assert.Equal(
                            PreviewSparseCloudVolumeContract
                                .MaximumEnteringBricksPerFrame,
                            clipmapUpdate.Entering.Count);
                        Assert.True(
                            clipmaps.TryMarkResident(
                                clipmapUpdate.Entering[0],
                                physicalBrickIndex: 7));
                        Assert.True(
                            sparseResources.TryStagePageTables(
                                clipmaps,
                                atlasGenerationId: 7,
                                out var stagingReason),
                            stagingReason);
                        Assert.True(sparseResources.HasPendingPublication);
                        Assert.False(
                            sparseResources.TryStagePageTables(
                                clipmaps,
                                out var duplicateStagingReason));
                        Assert.Equal(
                            "publication-pending",
                            duplicateStagingReason);

                        gl.Finish();
                        Assert.True(
                            sparseResources.TryPublishCompleted(
                                out var published,
                                out var publicationReason),
                            publicationReason);
                        Assert.True(published);
                        Assert.False(sparseResources.HasPendingPublication);
                        Assert.Equal(
                            1,
                            sparseResources.PublishedGenerationId);
                        Assert.Equal(
                            clipmaps.TableRevision,
                            sparseResources.PublishedPlanRevision);
                        Assert.Equal(
                            7,
                            sparseResources.PublishedAtlasGenerationId);
                        Assert.Equal(
                            clipmaps.ResidentCount,
                            sparseResources.PublishedResidentCount);
                        Assert.Equal(
                            buildBefore,
                            sparseResources.GetActivePageTableHandle(0));
                        Assert.Equal(
                            activeBefore,
                            sparseResources.GetBuildPageTableHandle(0));
                        Assert.Equal(
                            clipmaps.GetOrigin(0),
                            sparseResources.GetActiveOrigin(0));
                        Assert.False(sparseResources.IsSamplingReady);

                        var activePages = ReadR16UiTexture3D(
                            gl,
                            sparseResources.GetActivePageTableHandle(0));
                        var firstEntering = clipmapUpdate.Entering[0];
                        var firstLocal = new Int3(
                            firstEntering.X - clipmaps.GetOrigin(0).X,
                            firstEntering.Y - clipmaps.GetOrigin(0).Y,
                            firstEntering.Z - clipmaps.GetOrigin(0).Z);
                        Assert.Equal(
                            PreviewSparseCloudVolumeContract
                                .EncodePhysicalBrickIndex(7),
                            activePages[
                                PreviewSparseCloudVolumeContract
                                    .PageTableLinearIndex(firstLocal)]);
                        var secondEntering = clipmapUpdate.Entering[1];
                        var secondLocal = new Int3(
                            secondEntering.X - clipmaps.GetOrigin(0).X,
                            secondEntering.Y - clipmaps.GetOrigin(0).Y,
                            secondEntering.Z - clipmaps.GetOrigin(0).Z);
                        Assert.Equal(
                            PreviewSparseCloudVolumeContract.RequestedPage,
                            activePages[
                                PreviewSparseCloudVolumeContract
                                    .PageTableLinearIndex(secondLocal)]);
                    }

                    Assert.Equal(GLEnum.NoError, gl.GetError());
                }

                var compile = new GlShaderCompileContext(gl, useOpenGlEs: false, caps.Vendor, caps.Renderer);
                using var clouds = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_clouds.frag",
                    out var cloudError,
                    "cloud-flat-layer-live-smoke");
                Assert.True(clouds.IsValid, "Flat cloud-layer shader failed to compile: " + cloudError);
                using var highClouds = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_clouds.frag",
                    out var highCloudError,
                    "cloud-flat-layer-high-cq3.7-live-smoke",
                    new Dictionary<string, int>
                    {
                        ["GENESIS_CLOUD_QUALITY"] = PreviewVolumetricQuality.High,
                    });
                Assert.True(
                    highClouds.IsValid,
                    "CQ3.7 High cloud specialization failed to compile: " +
                    highCloudError);

                using var temporal = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_clouds_temporal.frag",
                    out var temporalError,
                    "cloud-temporal-live-smoke");
                Assert.True(temporal.IsValid, "Cloud temporal shader failed to compile: " + temporalError);

                using var upsample = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_clouds_upsample.frag",
                    out var upsampleError,
                    "cloud-upsample-live-smoke");
                Assert.True(upsample.IsValid, "Cloud upsample shader failed to compile: " + upsampleError);

                using var repair = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_clouds_repair.frag",
                    out var repairError,
                    "cloud-edge-repair-live-smoke");
                Assert.True(repair.IsValid, "Cloud edge-repair shader failed to compile: " + repairError);

                using var cloudLightSlice = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_cloud_light_cache_slice.frag",
                    out var cloudLightSliceError,
                    "cq3.1-cloud-light-slice-live-smoke");
                Assert.True(
                    cloudLightSlice.IsValid,
                    "CQ3.1 cloud-light fragment-slice shader failed to compile: " +
                    cloudLightSliceError);

                string? cloudLightComputeError = null;
                using var cloudLightCompute = caps.CanUseComputeCloudLightingCache
                    ? compile.CreateComputeProgram(
                        "genesis_cloud_light_cache.comp",
                        out cloudLightComputeError,
                        "cq3.2-cloud-light-compute-live-smoke")
                    : new GlShaderProgram(gl, 0);
                if (caps.CanUseComputeCloudLightingCache)
                {
                    Assert.True(
                        cloudLightCompute.IsValid,
                        "CQ3.2 cloud-light compute shader failed to compile: " +
                        cloudLightComputeError);
                }

                using var cloudLightLookup = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_cloud_light_cache_lookup.frag",
                    out var cloudLightLookupError,
                    "cq3.1-cloud-light-lookup-live-smoke");
                Assert.True(
                    cloudLightLookup.IsValid,
                    "CQ3.1 cloud-light lookup shader failed to compile: " +
                    cloudLightLookupError);

                using var cloudGroundTransmittance = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_cloud_ground_transmittance.frag",
                    out var cloudGroundTransmittanceError,
                    "cq3.5-ground-transmittance-live-smoke");
                Assert.True(
                    cloudGroundTransmittance.IsValid,
                    "CQ3.5 ground transmittance shader failed to compile: " +
                    cloudGroundTransmittanceError);

                using var fallbackComposite = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_godrays_composite.frag",
                    out var fallbackCompositeError,
                    "cloud-final-fallback-live-smoke");
                Assert.True(fallbackComposite.IsValid,
                    "Cloud final-composite fallback shader failed to compile: " + fallbackCompositeError);

                using var volumeIntegrate = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_volume_integrate.frag",
                    out var volumeIntegrateError,
                    "cloud-shared-volume-integrate-live-smoke");
                Assert.True(volumeIntegrate.IsValid,
                    "Cloud-aware volume integrate shader failed to compile: " + volumeIntegrateError);

                using var volumeIntegrateLite = compile.CreateProgram(
                    "genesis_godrays.vert",
                    "genesis_volume_integrate_lite.frag",
                    out var volumeIntegrateLiteError,
                    "cloud-shared-volume-integrate-lite-live-smoke");
                Assert.True(volumeIntegrateLite.IsValid,
                    "Cloud-aware lite volume integrate shader failed to compile: " + volumeIntegrateLiteError);

                while (gl.GetError() != GLEnum.NoError)
                {
                }

                using var stbnTexture = new GlTexture3D(gl);
                stbnTexture.UploadR8(
                    PreviewCloudSpatiotemporalBlueNoiseGenerator.Width,
                    PreviewCloudSpatiotemporalBlueNoiseGenerator.Height,
                    PreviewCloudSpatiotemporalBlueNoiseGenerator.FrameCount,
                    PreviewCloudSpatiotemporalBlueNoiseGenerator.GenerateR8());
                Assert.Equal(
                    GLEnum.NoError,
                    gl.GetError());
                ValidateCloudDensityTextureMipState(gl);

                using var temporalTarget = new GlCloudTemporalRenderTarget(gl);
                using var temporalHistory = new GlCloudTemporalRenderTarget(gl);
                Assert.True(temporalTarget.EnsureSize(16, 16),
                    "Cloud temporal MRT failed to initialize.");
                Assert.True(temporalHistory.EnsureSize(16, 16),
                    "Cloud temporal history MRT failed to initialize.");
                Assert.True(temporalHistory.CopyFrom(temporalTarget),
                    "Cloud temporal MRT history copy failed.");

                var floatingProfile = GlCloudRenderFormatProfile.Select(
                    caps, PreviewVolumetricQuality.Medium);
                Assert.True(floatingProfile.UsesDirectMetadata);
                using var floatingTarget = new GlCloudTemporalRenderTarget(gl, floatingProfile);
                using var floatingHistory = new GlCloudTemporalRenderTarget(gl, floatingProfile);
                Assert.True(floatingTarget.EnsureSize(16, 16),
                    "CQ1 RGBA16F/RG32F cloud MRT failed to initialize.");
                Assert.True(floatingHistory.EnsureSize(16, 16),
                    "CQ1 RGBA16F/RG32F cloud history MRT failed to initialize.");
                floatingTarget.Clear();
                Assert.True(floatingHistory.CopyFrom(floatingTarget),
                    "CQ1 floating-point cloud MRT history copy failed.");
                Assert.False(floatingHistory.CopyFrom(temporalTarget),
                    "Cloud histories with different metadata ABIs must not be copied.");

                var momentProfile = GlCloudRenderFormatProfile.Select(
                    caps, PreviewVolumetricQuality.High);
                Assert.True(momentProfile.UsesTemporalMoments);
                using var momentTarget = new GlCloudTemporalRenderTarget(gl, momentProfile);
                using var momentHistory = new GlCloudTemporalRenderTarget(gl, momentProfile);
                Assert.True(momentTarget.EnsureSize(16, 16),
                    "CQ1.6 RG16F moment MRT failed to initialize.");
                Assert.True(momentHistory.EnsureSize(16, 16),
                    "CQ1.6 RG16F moment history failed to initialize.");
                Assert.Equal(3, momentTarget.AttachmentCount);
                Assert.NotEqual(0u, momentTarget.MomentTextureHandle);
                Assert.True(momentHistory.CopyFrom(momentTarget),
                    "CQ1.6 moment history copy failed.");

                var cinematicTraceSize = PreviewCloudTraceSizing.Resolve(
                    575, 455, PreviewVolumetricQuality.Cinematic);
                Assert.Equal((384, 304), (cinematicTraceSize.Width, cinematicTraceSize.Height));
                Assert.True(momentTarget.EnsureSize(
                    cinematicTraceSize.Width, cinematicTraceSize.Height));
                Assert.True(momentHistory.EnsureSize(
                    cinematicTraceSize.Width, cinematicTraceSize.Height));
                Assert.True(momentHistory.CopyFrom(momentTarget),
                    "CQ1.7 odd-viewport Cinematic target copy failed.");
                var highTraceSize = PreviewCloudTraceSizing.Resolve(
                    575, 455, PreviewVolumetricQuality.High);
                Assert.Equal((287, 227), (highTraceSize.Width, highTraceSize.Height));
                Assert.True(momentTarget.EnsureSize(highTraceSize.Width, highTraceSize.Height));
                Assert.True(momentHistory.EnsureSize(highTraceSize.Width, highTraceSize.Height));
                Assert.True(momentHistory.CopyFrom(momentTarget),
                    "CQ1.7 Cinematic-to-High target resize/copy failed.");

                ValidateCloudDepthOrdering(gl, upsample);
                ValidateDirectCloudDepthOrdering(gl, upsample);
                ValidateLinearCloudRadiance(gl, upsample);
                ValidateCloudDirectDiscOcclusion(gl, upsample);
                ValidateCloudTemporalMoments(gl, temporal);
                ValidateCloudEdgeRepair(gl, repair, stbnTexture);
                ValidateCloudLightFragmentReference(
                    gl,
                    cloudLightSlice,
                    cloudLightLookup,
                    cloudGroundTransmittance,
                    cloudLightCompute.IsValid ? cloudLightCompute : null);
            }

            return true;
        }, TimeSpan.FromSeconds(30));
    }

    private static unsafe void UploadSparseTraversalFixture(
        GL gl,
        GlSparseCloudVolumeResources resources)
    {
        var pageTable = new ushort[
            PreviewSparseCloudVolumeContract.PageTableEntryCount];
        var localPage = new Int3(16, 8, 16);
        pageTable[
            PreviewSparseCloudVolumeContract.PageTableLinearIndex(
                localPage)] =
            PreviewSparseCloudVolumeContract.EncodePhysicalBrickIndex(0);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
        gl.BindTexture(
            TextureTarget.Texture3D,
            resources.GetActivePageTableHandle(0));
        fixed (ushort* pages = pageTable)
        {
            gl.TexSubImage3D(
                TextureTarget.Texture3D,
                0,
                0,
                0,
                0,
                PreviewSparseCloudVolumeContract.PageTableWidth,
                PreviewSparseCloudVolumeContract.PageTableHeight,
                PreviewSparseCloudVolumeContract.PageTableDepth,
                PixelFormat.RedInteger,
                PixelType.UnsignedShort,
                pages);
        }

        var size = PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        var brick = new byte[size * size * size * 2];
        const int densityPlaneX = 6;
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var index = ((z * size + y) * size + x) * 2;
                    brick[index] =
                        x == densityPlaneX ? (byte)255 : (byte)0;
                    brick[index + 1] =
                        checked((byte)Math.Abs(x - densityPlaneX));
                }
            }
        }

        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.BindTexture(
            TextureTarget.Texture3D,
            resources.AtlasTextureHandle);
        fixed (byte* texels = brick)
        {
            gl.TexSubImage3D(
                TextureTarget.Texture3D,
                0,
                0,
                0,
                0,
                (uint)size,
                (uint)size,
                (uint)size,
                PixelFormat.RG,
                PixelType.UnsignedByte,
                texels);
        }

        gl.BindTexture(TextureTarget.Texture3D, 0);
        gl.MemoryBarrier(
            (uint)MemoryBarrierMask.TextureUpdateBarrierBit |
            (uint)MemoryBarrierMask.TextureFetchBarrierBit);
    }

    private static float[] DispatchSparseTraversalFixture(
        GL gl,
        GlShaderProgram program,
        GlSparseCloudVolumeResources resources)
    {
        var outputBuffer = gl.GenBuffer();
        try
        {
            var zero = new float[8];
            gl.BindBuffer(
                BufferTargetARB.ShaderStorageBuffer,
                outputBuffer);
            gl.BufferData<float>(
                BufferTargetARB.ShaderStorageBuffer,
                zero,
                BufferUsageARB.DynamicRead);
            gl.BindBufferBase(
                BufferTargetARB.ShaderStorageBuffer,
                0,
                outputBuffer);

            program.Use();
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(
                TextureTarget.Texture3D,
                resources.AtlasTextureHandle);
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(
                TextureTarget.Texture3D,
                resources.GetActivePageTableHandle(0));
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(
                TextureTarget.Texture3D,
                resources.GetActivePageTableHandle(1));
            gl.ActiveTexture(TextureUnit.Texture3);
            gl.BindTexture(
                TextureTarget.Texture3D,
                resources.GetActivePageTableHandle(2));
            gl.Uniform1(program.GetUniformLocation("uSparseCloudAtlas"), 0);
            gl.Uniform1(program.GetUniformLocation("uSparseCloudPageL0"), 1);
            gl.Uniform1(program.GetUniformLocation("uSparseCloudPageL1"), 2);
            gl.Uniform1(program.GetUniformLocation("uSparseCloudPageL2"), 3);
            gl.Uniform3(
                program.GetUniformLocation("uSparseCloudOriginL0"),
                0,
                0,
                0);
            gl.Uniform3(
                program.GetUniformLocation("uSparseCloudOriginL1"),
                0,
                0,
                0);
            gl.Uniform3(
                program.GetUniformLocation("uSparseCloudOriginL2"),
                0,
                0,
                0);
            gl.Uniform3(
                program.GetUniformLocation("uValidationRayOrigin"),
                256f,
                136f,
                264f);
            gl.Uniform3(
                program.GetUniformLocation("uValidationRayDirection"),
                1f,
                0f,
                0f);
            gl.Uniform1(
                program.GetUniformLocation("uValidationTStart"),
                0f);
            gl.Uniform1(
                program.GetUniformLocation("uValidationTEnd"),
                16f);
            gl.Uniform1(
                program.GetUniformLocation("uValidationFineStep"),
                1f);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(
                (uint)MemoryBarrierMask.ShaderStorageBarrierBit);
            gl.Finish();
            var output = new float[8];
            gl.BindBuffer(
                BufferTargetARB.ShaderStorageBuffer,
                outputBuffer);
            gl.GetBufferSubData<float>(
                BufferTargetARB.ShaderStorageBuffer,
                0,
                output);
            return output;
        }
        finally
        {
            gl.BindBufferBase(
                BufferTargetARB.ShaderStorageBuffer,
                0,
                0);
            gl.BindBuffer(
                BufferTargetARB.ShaderStorageBuffer,
                0);
            gl.UseProgram(0);
            gl.DeleteBuffer(outputBuffer);
            for (var unit = 0; unit < 4; unit++)
            {
                gl.ActiveTexture(TextureUnit.Texture0 + unit);
                gl.BindTexture(TextureTarget.Texture3D, 0);
            }

            gl.ActiveTexture(TextureUnit.Texture0);
        }
    }

    private static void ValidateCloudLightFragmentReference(
        GL gl,
        GlShaderProgram program,
        GlShaderProgram lookupProgram,
        GlShaderProgram groundTransmittanceProgram,
        GlShaderProgram? computeProgram)
    {
        var profile = new PreviewCloudLightingCacheProfile(
            "CQ3.1 test",
            PreviewCloudLightingCacheProfiles.StorageFormat,
            new PreviewCloudLightCascadeProfile(8, 8, 24, 64f, 1),
            new PreviewCloudLightCascadeProfile(8, 8, 16, 128f, 1),
            PreviewCloudLightingCacheProfiles.NearOverlapFraction,
            0);
        Assert.True(
            GlCloudLightFroxelCache.TryCreate(
                gl,
                profile,
                out var cache,
                out var allocationDiagnostic),
            allocationDiagnostic);
        Assert.NotNull(cache);

        var vao = CreateFullscreenQuad(gl, out var vbo);
        try
        {
            using (cache)
            {
                var basis = PreviewCloudLightBasisBuilder.Build(new Vector3(0f, -1f, 0f));
                var nearTransform = PreviewCloudLightCascadeTransform.Create(
                    basis,
                    profile.Near,
                    Vector3.Zero,
                    -40f,
                    0f);
                var farTransform = PreviewCloudLightCascadeTransform.Create(
                    basis,
                    profile.Far,
                    Vector3.Zero,
                    -40f,
                    0f);
                var generator = new GlCloudLightFragmentSliceGenerator(
                    gl,
                    program,
                    vao);
                var inputs = new GlCloudLightSliceGenerationInputs(
                    nearTransform,
                    farTransform,
                    new PreviewCloudLightAltitudeBounds(1f, 20f, 30f, 32f, 4f),
                    new Vector3(
                        0f,
                        -3.2f -
                            PreviewStageConstants.CloudLegacyAltitudeReferenceRadius,
                        0f),
                    PreviewStageConstants.CloudLegacyAltitudeReferenceRadius,
                    Density: 1f,
                    CoverageScale: 1f,
                    VolumeSize: 48f,
                    WindOffset: Vector3.Zero,
                    CirrusStrength: 0f,
                    CirrusWindOffset: Vector2.Zero,
                    CirrusWindDirection: Vector2.UnitX,
                    Quality: 3,
                    DensityAssetVersion: 2,
                    CloudNoiseTexture: 0,
                    DetailNoiseTexture: 0,
                    CoverageTexture: 0,
                    ReferenceDensity: 0.5f);
                Assert.True(
                    generator.TryGenerate(
                        cache!,
                        inputs,
                        restoreViewportWidth: 64,
                        restoreViewportHeight: 64,
                        out var generationDiagnostic),
                    generationDiagnostic);
                Assert.True(cache!.IsReferenceReady);

                if (computeProgram is { IsValid: true })
                {
                    Assert.True(
                        GlCloudLightFroxelCache.TryCreate(
                            gl,
                            profile,
                            out var computeCache,
                            out var computeAllocationDiagnostic),
                        computeAllocationDiagnostic);
                    Assert.NotNull(computeCache);
                    using (computeCache)
                    {
                        var computeGenerator = new GlCloudLightComputeGenerator(
                            gl,
                            computeProgram);
                        Assert.True(
                            computeGenerator.TryGenerate(
                                computeCache!,
                                inputs,
                                out var computeDiagnostic),
                            computeDiagnostic);
                        Assert.True(computeCache!.IsReferenceReady);
                        AssertCloudLightCascadeParity(cache.Near, computeCache.Near);
                        AssertCloudLightCascadeParity(cache.Far, computeCache.Far);
                    }
                }

                var previousOpticalDepth = -1f;
                var expectedDelta = 0.5f * nearTransform.DepthSliceWorldSize * 0.18f;
                for (var layer = 0; layer < profile.Near.Depth; layer++)
                {
                    var readback = new float[profile.Near.Width * profile.Near.Height * 2];
                    Assert.True(
                        cache.Near.TryReadLayer(layer, readback, out var readDiagnostic),
                        readDiagnostic);
                    var center = ((profile.Near.Height / 2 * profile.Near.Width) +
                        profile.Near.Width / 2) * 2;
                    var opticalDepth = readback[center];
                    var skyVisibility = readback[center + 1];
                    Assert.True(float.IsFinite(opticalDepth));
                    Assert.True(float.IsFinite(skyVisibility));
                    Assert.True(opticalDepth >= previousOpticalDepth);
                    Assert.InRange(
                        opticalDepth,
                        expectedDelta * (layer + 1) - 0.02f,
                        expectedDelta * (layer + 1) + 0.02f);
                    var expectedVisibility = MathF.Exp(
                        -expectedDelta * 0.35f);
                    Assert.InRange(
                        skyVisibility,
                        expectedVisibility - 0.01f,
                        expectedVisibility + 0.01f);
                    previousOpticalDepth = opticalDepth;
                }

                ValidateCloudLightLookup(
                    gl,
                    lookupProgram,
                    cache,
                    nearTransform,
                    farTransform,
                    vao,
                    expectedDelta * (profile.Near.Depth + 1f) * 0.5f);
                ValidateCloudGroundTransmittance(
                    gl,
                    groundTransmittanceProgram,
                    cache,
                    vao,
                    expectedTransmittance: MathF.Exp(-0.5f * 40f * 0.18f));
            }
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteBuffer(vbo);
            gl.DeleteVertexArray(vao);
        }
    }

    private static void ValidateCloudGroundTransmittance(
        GL gl,
        GlShaderProgram program,
        GlCloudLightFroxelCache cache,
        uint vao,
        float expectedTransmittance)
    {
        var profile = new PreviewCloudGroundTransmittanceProfile(
            cache.Profile.Far.Width,
            cache.Profile.Far.Height,
            cache.Profile.Far.WorldSpan,
            CombineNearAndFar: true);
        var allocationReadFramebuffer =
            gl.GetInteger(GetPName.ReadFramebufferBinding);
        var allocationDrawFramebuffer =
            gl.GetInteger(GetPName.DrawFramebufferBinding);
        var allocationActiveTexture =
            gl.GetInteger(GetPName.ActiveTexture);
        var allocationTexture2D =
            gl.GetInteger(GetPName.TextureBinding2D);
        Assert.True(
            GlCloudGroundTransmittanceTarget.TryCreate(
                gl,
                profile,
                out var target,
                out var allocationDiagnostic),
            allocationDiagnostic);
        Assert.NotNull(target);
        Assert.Equal(
            allocationReadFramebuffer,
            gl.GetInteger(GetPName.ReadFramebufferBinding));
        Assert.Equal(
            allocationDrawFramebuffer,
            gl.GetInteger(GetPName.DrawFramebufferBinding));
        Assert.Equal(
            allocationActiveTexture,
            gl.GetInteger(GetPName.ActiveTexture));
        Assert.Equal(
            allocationTexture2D,
            gl.GetInteger(GetPName.TextureBinding2D));

        using (target)
        {
            var publisher = new GlCloudGroundTransmittancePublisher(
                gl,
                program,
                vao);
            var priorReadFramebuffer =
                gl.GetInteger(GetPName.ReadFramebufferBinding);
            var priorDrawFramebuffer =
                gl.GetInteger(GetPName.DrawFramebufferBinding);
            var priorProgram =
                gl.GetInteger(GetPName.CurrentProgram);
            var priorVertexArray =
                gl.GetInteger(GetPName.VertexArrayBinding);
            var priorActiveTexture =
                gl.GetInteger(GetPName.ActiveTexture);
            var priorViewport = new int[4];
            gl.GetInteger(GetPName.Viewport, priorViewport);
            Assert.True(
                publisher.TryPublish(
                    cache,
                    target!,
                    groundWorldY: 0f,
                    out var publishDiagnostic),
                publishDiagnostic);
            Assert.Equal(
                priorReadFramebuffer,
                gl.GetInteger(GetPName.ReadFramebufferBinding));
            Assert.Equal(
                priorDrawFramebuffer,
                gl.GetInteger(GetPName.DrawFramebufferBinding));
            Assert.Equal(
                priorProgram,
                gl.GetInteger(GetPName.CurrentProgram));
            Assert.Equal(
                priorVertexArray,
                gl.GetInteger(GetPName.VertexArrayBinding));
            Assert.Equal(
                priorActiveTexture,
                gl.GetInteger(GetPName.ActiveTexture));
            var restoredViewport = new int[4];
            gl.GetInteger(GetPName.Viewport, restoredViewport);
            Assert.Equal(priorViewport, restoredViewport);
            Assert.True(target!.IsCurrent(cache));
            Assert.NotEqual(0u, target.TextureHandle);

            var values = new float[profile.Width * profile.Height];
            Assert.True(
                target.TryRead(values, out var readDiagnostic),
                readDiagnostic);
            var center =
                profile.Height / 2 * profile.Width +
                profile.Width / 2;
            Assert.InRange(
                values[center],
                expectedTransmittance - 0.01f,
                expectedTransmittance + 0.01f);
            Assert.All(values, value =>
            {
                Assert.True(float.IsFinite(value));
                Assert.InRange(value, 0f, 1f);
            });
        }
    }

    private static void AssertCloudLightCascadeParity(
        GlCloudLightCascadeTarget fragment,
        GlCloudLightCascadeTarget compute)
    {
        Assert.Equal(fragment.Profile, compute.Profile);
        for (var layer = 0; layer < fragment.Profile.Depth; layer++)
        {
            var fragmentValues =
                new float[fragment.Profile.Width * fragment.Profile.Height * 2];
            var computeValues =
                new float[compute.Profile.Width * compute.Profile.Height * 2];
            Assert.True(
                fragment.TryReadLayer(
                    layer,
                    fragmentValues,
                    out var fragmentReadDiagnostic),
                fragmentReadDiagnostic);
            Assert.True(
                compute.TryReadLayer(
                    layer,
                    computeValues,
                    out var computeReadDiagnostic),
                computeReadDiagnostic);

            for (var index = 0; index < fragmentValues.Length; index += 2)
            {
                var opticalDifference = MathF.Abs(
                    computeValues[index] - fragmentValues[index]);
                var visibilityDifference = MathF.Abs(
                    computeValues[index + 1] - fragmentValues[index + 1]);
                var opticalTolerance = HalfUlpTolerance(fragmentValues[index]);
                var visibilityTolerance = HalfUlpTolerance(fragmentValues[index + 1]);
                Assert.True(
                    opticalDifference <= opticalTolerance,
                    $"CQ3.2 optical-depth parity failed at layer={layer}, texel={index / 2}: " +
                    $"fragment={fragmentValues[index]}, compute={computeValues[index]}, " +
                    $"difference={opticalDifference}, twoHalfUlp={opticalTolerance}.");
                Assert.True(
                    visibilityDifference <= visibilityTolerance,
                    $"CQ3.2 visibility parity failed at layer={layer}, texel={index / 2}: " +
                    $"fragment={fragmentValues[index + 1]}, compute={computeValues[index + 1]}, " +
                    $"difference={visibilityDifference}, twoHalfUlp={visibilityTolerance}.");
            }
        }
    }

    private static float HalfUlpTolerance(float value)
    {
        var half = (Half)Math.Clamp(value, 0f, (float)Half.MaxValue);
        var bits = BitConverter.HalfToInt16Bits(half);
        var next = BitConverter.Int16BitsToHalf((short)(bits + 1));
        return MathF.Max(
            MathF.Abs((float)next - (float)half) * 2f,
            1e-6f);
    }

    private static unsafe void ValidateCloudLightLookup(
        GL gl,
        GlShaderProgram program,
        GlCloudLightFroxelCache cache,
        in PreviewCloudLightCascadeTransform nearTransform,
        in PreviewCloudLightCascadeTransform farTransform,
        uint vao,
        float expectedCenterOpticalDepth)
    {
        var framebuffer = gl.GenFramebuffer();
        var outputTexture = gl.GenTexture();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, outputTexture);
            gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba32f,
                1,
                1,
                0,
                PixelFormat.Rgba,
                PixelType.Float,
                (void*)0);
            gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)GLEnum.Nearest);
            gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)GLEnum.Nearest);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                outputTexture,
                0);
            gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            Assert.Equal(
                GLEnum.FramebufferComplete,
                gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            program.Use();
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2DArray, cache.Near.ArrayTextureHandle);
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2DArray, cache.Far.ArrayTextureHandle);
            SetUniform1(gl, program, "uNearCache", 0);
            SetUniform1(gl, program, "uFarCache", 1);

            var world = nearTransform.UnitToWorld(new Vector3(0.5f, 0.5f, 0.5f));
            SetUniform3(gl, program, "uWorldPosition", world.X, world.Y, world.Z);
            SetUniform3(
                gl,
                program,
                "uBasisRight",
                nearTransform.Basis.Right.X,
                nearTransform.Basis.Right.Y,
                nearTransform.Basis.Right.Z);
            SetUniform3(
                gl,
                program,
                "uBasisUp",
                nearTransform.Basis.Up.X,
                nearTransform.Basis.Up.Y,
                nearTransform.Basis.Up.Z);
            SetUniform3(
                gl,
                program,
                "uBasisForward",
                nearTransform.Basis.Forward.X,
                nearTransform.Basis.Forward.Y,
                nearTransform.Basis.Forward.Z);
            SetUniform2(
                gl,
                program,
                "uNearPlaneCenter",
                nearTransform.PlaneCenterX,
                nearTransform.PlaneCenterY);
            SetUniform2(
                gl,
                program,
                "uFarPlaneCenter",
                farTransform.PlaneCenterX,
                farTransform.PlaneCenterY);
            SetUniform1(gl, program, "uNearWorldSpan", nearTransform.Profile.WorldSpan);
            SetUniform1(gl, program, "uFarWorldSpan", farTransform.Profile.WorldSpan);
            SetUniform1(gl, program, "uNearLightDepthMin", nearTransform.LightDepthMin);
            SetUniform1(gl, program, "uFarLightDepthMin", farTransform.LightDepthMin);
            SetUniform1(gl, program, "uNearLightDepthSpan", nearTransform.LightDepthSpan);
            SetUniform1(gl, program, "uFarLightDepthSpan", farTransform.LightDepthSpan);
            SetUniform1(gl, program, "uNearDepth", nearTransform.Profile.Depth);
            SetUniform1(gl, program, "uFarDepth", farTransform.Profile.Depth);
            SetUniform1(
                gl,
                program,
                "uNearOverlapFraction",
                PreviewCloudLightingCacheProfiles.NearOverlapFraction);

            float[] ReadLookup(Vector3 sampleWorld, int hasNear, int hasFar)
            {
                SetUniform3(
                    gl,
                    program,
                    "uWorldPosition",
                    sampleWorld.X,
                    sampleWorld.Y,
                    sampleWorld.Z);
                SetUniform1(gl, program, "uHasNear", hasNear);
                SetUniform1(gl, program, "uHasFar", hasFar);
                gl.Viewport(0, 0, 1, 1);
                gl.Disable(EnableCap.Blend);
                gl.Disable(EnableCap.DepthTest);
                gl.BindVertexArray(vao);
                gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
                gl.BindVertexArray(0);
                var result = new float[4];
                fixed (float* pointer = result)
                {
                    gl.ReadPixels(
                        0,
                        0,
                        1,
                        1,
                        PixelFormat.Rgba,
                        PixelType.Float,
                        pointer);
                }

                Assert.Equal(GLEnum.NoError, gl.GetError());
                return result;
            }

            var output = ReadLookup(world, hasNear: 1, hasFar: 1);
            Assert.InRange(
                output[0],
                expectedCenterOpticalDepth - 0.03f,
                expectedCenterOpticalDepth + 0.03f);
            Assert.InRange(output[1], 0f, 1f);
            Assert.InRange(output[2], 0.999f, 1.001f);
            Assert.InRange(output[3], 0f, 0.001f);

            var overlap = ReadLookup(
                nearTransform.UnitToWorld(new Vector3(0.95f, 0.5f, 0.5f)),
                hasNear: 1,
                hasFar: 1);
            Assert.InRange(overlap[0], 0f, 8f);
            Assert.InRange(overlap[1], 0f, 1f);
            Assert.InRange(overlap[2], 0.45f, 0.55f);
            Assert.InRange(overlap[3], 0.45f, 0.55f);

            var farOnly = ReadLookup(
                nearTransform.UnitToWorld(new Vector3(1.10f, 0.5f, 0.5f)),
                hasNear: 1,
                hasFar: 1);
            Assert.InRange(farOnly[0], 0f, 8f);
            Assert.InRange(farOnly[1], 0f, 1f);
            Assert.InRange(farOnly[2], 0f, 0.001f);
            Assert.InRange(farOnly[3], 0.999f, 1.001f);

            var outsideFar = ReadLookup(
                nearTransform.UnitToWorld(new Vector3(1.60f, 0.5f, 0.5f)),
                hasNear: 1,
                hasFar: 1);
            Assert.InRange(outsideFar[0], 0f, 0.001f);
            Assert.InRange(outsideFar[1], 0.999f, 1.001f);
            Assert.InRange(outsideFar[2], 0f, 0.001f);
            Assert.InRange(outsideFar[3], 0f, 0.001f);

            var missingNear = ReadLookup(world, hasNear: 0, hasFar: 1);
            Assert.InRange(missingNear[2], 0f, 0.001f);
            Assert.InRange(missingNear[3], 0.999f, 1.001f);

            var missingFar = ReadLookup(world, hasNear: 1, hasFar: 0);
            Assert.InRange(missingFar[2], 0.999f, 1.001f);
            Assert.InRange(missingFar[3], 0f, 0.001f);
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteTexture(outputTexture);
            gl.DeleteFramebuffer(framebuffer);
        }
    }

    private static void ValidateCloudDensityTextureMipState(GL gl)
    {
        const int size = 8;
        var rgba = RepeatPixel(size * size, size, 31, 97, 173, 229);

        using var shape = new GlTexture3D(gl);
        shape.UploadRgba(size, rgba);
        shape.Bind(0);
        gl.GetTexParameter(
            TextureTarget.Texture3D,
            GetTextureParameter.TextureMinFilter,
            out int shapeMinFilter);
        gl.GetTexParameter(
            TextureTarget.Texture3D,
            GetTextureParameter.TextureWrapS,
            out int shapeWrapS);
        gl.GetTexParameter(
            TextureTarget.Texture3D,
            GetTextureParameter.TextureWrapT,
            out int shapeWrapT);
        gl.GetTexParameter(
            TextureTarget.Texture3D,
            GetTextureParameter.TextureWrapRExt,
            out int shapeWrapR);
        gl.GetTexLevelParameter(
            TextureTarget.Texture3D,
            3,
            GetTextureParameter.TextureWidth,
            out int shapeMipWidth);
        gl.GetTexLevelParameter(
            TextureTarget.Texture3D,
            3,
            GetTextureParameter.TextureHeight,
            out int shapeMipHeight);
        gl.GetTexLevelParameter(
            TextureTarget.Texture3D,
            3,
            GetTextureParameter.TextureDepthExt,
            out int shapeMipDepth);

        Assert.Equal((int)GLEnum.LinearMipmapLinear, shapeMinFilter);
        Assert.Equal((int)GLEnum.Repeat, shapeWrapS);
        Assert.Equal((int)GLEnum.Repeat, shapeWrapT);
        Assert.Equal((int)GLEnum.Repeat, shapeWrapR);
        Assert.Equal((1, 1, 1), (shapeMipWidth, shapeMipHeight, shapeMipDepth));

        using var weather = new GlTexture2D(
            gl,
            nearestFilter: false,
            mipmapped: true);
        weather.UploadRgba(size, size, RepeatPixel(size, size, 43, 109, 181, 239),
            nearestFilter: false);
        weather.Bind(1);
        gl.GetTexParameter(
            TextureTarget.Texture2D,
            GetTextureParameter.TextureMinFilter,
            out int weatherMinFilter);
        gl.GetTexParameter(
            TextureTarget.Texture2D,
            GetTextureParameter.TextureWrapS,
            out int weatherWrapS);
        gl.GetTexParameter(
            TextureTarget.Texture2D,
            GetTextureParameter.TextureWrapT,
            out int weatherWrapT);
        gl.GetTexLevelParameter(
            TextureTarget.Texture2D,
            3,
            GetTextureParameter.TextureWidth,
            out int weatherMipWidth);
        gl.GetTexLevelParameter(
            TextureTarget.Texture2D,
            3,
            GetTextureParameter.TextureHeight,
            out int weatherMipHeight);

        Assert.Equal((int)GLEnum.LinearMipmapLinear, weatherMinFilter);
        Assert.Equal((int)GLEnum.Repeat, weatherWrapS);
        Assert.Equal((int)GLEnum.Repeat, weatherWrapT);
        Assert.Equal((1, 1), (weatherMipWidth, weatherMipHeight));
        Assert.Equal(GLEnum.NoError, gl.GetError());
    }

    private static void ValidateCloudDepthOrdering(GL gl, GlShaderProgram upsample)
    {
        const int cloudSize = 4;
        const int outputSize = 8;
        ReadOnlySpan<byte> clear = [7, 11, 19, 255];
        using var source = new GlCloudTemporalRenderTarget(gl);
        using var output = new GlPixelRenderHarness(gl, outputSize, outputSize);
        Assert.True(source.EnsureSize(cloudSize, cloudSize));

        var cloudColor = RepeatPixel(cloudSize, cloudSize, 220, 110, 48, 255);
        UploadRgba8(gl, source.ColorTextureHandle, cloudSize, cloudSize, cloudColor);

        var sceneDepthTexture = gl.GenTexture();
        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            // With identity inverse VP and camera z=-1, depth 0.5 reconstructs an opaque
            // receiver roughly one unit down the center ray.
            AllocateRgba8(gl, sceneDepthTexture, outputSize, outputSize,
                RepeatPixel(outputSize, outputSize, 128, 0, 0, 255));
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

            ConfigureUpsampleUniforms(gl, upsample, cloudSize, directMetadata: false);
            var frontData = RepeatPackedDistance(cloudSize, cloudSize, 0.05f);
            UploadRgba8(gl, source.DataTextureHandle, cloudSize, cloudSize, frontData);
            var front = output.Capture("cloud-in-front-of-scene", _ =>
                DrawUpsample(gl, upsample, quadVao, source, sceneDepthTexture));

            var behindData = RepeatPackedDistance(cloudSize, cloudSize, 10f);
            UploadRgba8(gl, source.DataTextureHandle, cloudSize, cloudSize, behindData);
            var behind = output.Capture("cloud-behind-scene", _ =>
                DrawUpsample(gl, upsample, quadVao, source, sceneDepthTexture));

            Assert.True(front.CountPixelsOutside(clear, tolerance: 2) >= front.PixelCount * 3 / 4,
                "Cloud in front of opaque depth was incorrectly erased.");
            Assert.Equal(0, behind.CountPixelsOutside(clear, tolerance: 2));
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.DeleteTexture(sceneDepthTexture);
        }
    }

    private static void ValidateDirectCloudDepthOrdering(GL gl, GlShaderProgram upsample)
    {
        const int cloudSize = 4;
        const int outputSize = 8;
        ReadOnlySpan<byte> clear = [7, 11, 19, 255];
        using var source = new GlCloudTemporalRenderTarget(
            gl, GlCloudRenderFormatProfile.DesktopFloatingPoint);
        using var output = new GlPixelRenderHarness(gl, outputSize, outputSize);
        Assert.True(source.EnsureSize(cloudSize, cloudSize));

        var cloudColor = RepeatPixel(cloudSize, cloudSize, 220, 110, 48, 255);
        UploadRgba8(gl, source.ColorTextureHandle, cloudSize, cloudSize, cloudColor);

        var sceneDepthTexture = gl.GenTexture();
        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            AllocateRgba8(gl, sceneDepthTexture, outputSize, outputSize,
                RepeatPixel(outputSize, outputSize, 128, 0, 0, 255));
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

            ConfigureUpsampleUniforms(gl, upsample, cloudSize, directMetadata: true);
            UploadRg32f(gl, source.DataTextureHandle, cloudSize, cloudSize,
                RepeatDirectMetadata(cloudSize, cloudSize, 0.05f, 0.5f));
            var front = output.Capture("direct-cloud-in-front-of-scene", _ =>
                DrawUpsample(gl, upsample, quadVao, source, sceneDepthTexture));

            UploadRg32f(gl, source.DataTextureHandle, cloudSize, cloudSize,
                RepeatDirectMetadata(cloudSize, cloudSize, 10f, 0.5f));
            var behind = output.Capture("direct-cloud-behind-scene", _ =>
                DrawUpsample(gl, upsample, quadVao, source, sceneDepthTexture));

            Assert.True(front.CountPixelsOutside(clear, tolerance: 2) >= front.PixelCount * 3 / 4,
                "Direct-metadata cloud in front of opaque depth was incorrectly erased.");
            Assert.Equal(0, behind.CountPixelsOutside(clear, tolerance: 2));
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.DeleteTexture(sceneDepthTexture);
        }
    }

    private static void ValidateLinearCloudRadiance(GL gl, GlShaderProgram upsample)
    {
        const int cloudSize = 4;
        const int outputSize = 8;
        using var source = new GlCloudTemporalRenderTarget(
            gl, GlCloudRenderFormatProfile.DesktopFloatingPoint);
        using var history = new GlCloudTemporalRenderTarget(
            gl, GlCloudRenderFormatProfile.DesktopFloatingPoint);
        using var output = new GlPixelRenderHarness(gl, outputSize, outputSize);
        Assert.True(source.EnsureSize(cloudSize, cloudSize));
        Assert.True(history.EnsureSize(cloudSize, cloudSize));

        UploadRgba32f(gl, source.ColorTextureHandle, cloudSize, cloudSize,
            RepeatRgbaFloat(cloudSize, cloudSize, 2.5f, 0.75f, 0.25f, 1f));
        UploadRg32f(gl, source.DataTextureHandle, cloudSize, cloudSize,
            RepeatDirectMetadata(cloudSize, cloudSize, 0.05f, 0.5f));
        Assert.True(history.CopyFrom(source), "Floating-point cloud history copy failed.");
        var copied = ReadTextureRgbaFloat(gl, history.ColorTextureHandle, cloudSize, cloudSize);
        Assert.InRange(copied[0], 2.45f, 2.55f);

        var sceneDepthTexture = gl.GenTexture();
        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            AllocateRgba8(gl, sceneDepthTexture, outputSize, outputSize,
                RepeatPixel(outputSize, outputSize, 255, 0, 0, 255));

            ConfigureUpsampleUniforms(
                gl,
                upsample,
                cloudSize,
                directMetadata: true,
                hasSceneDepth: false,
                hdrPresent: true,
                applyCloudEncoding: true);
            var hdr = output.Capture("linear-cloud-final-hdr", _ =>
                DrawUpsample(gl, upsample, quadVao, history, sceneDepthTexture));

            ConfigureUpsampleUniforms(
                gl,
                upsample,
                cloudSize,
                directMetadata: true,
                hasSceneDepth: false,
                hdrPresent: false,
                applyCloudEncoding: true);
            var sdr = output.Capture("linear-cloud-final-sdr", _ =>
                DrawUpsample(gl, upsample, quadVao, history, sceneDepthTexture));

            var hdrRed = hdr.GetRgbaSpan()[0];
            var sdrRed = sdr.GetRgbaSpan()[0];
            // HDR uses presentEncodeScRgb (ACES + headroom); SDR uses soft-knee + sRGB.
            // Hot linear input (2.5) should land near display white on both paths.
            Assert.InRange(hdrRed, (byte)230, (byte)255);
            Assert.InRange(sdrRed, (byte)230, (byte)254);
            Assert.NotEqual(hdrRed, sdrRed);
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.DeleteTexture(sceneDepthTexture);
        }
    }

    private static void ValidateCloudDirectDiscOcclusion(
        GL gl,
        GlShaderProgram upsample)
    {
        const int cloudSize = 4;
        const int outputSize = 8;
        using var source = new GlCloudTemporalRenderTarget(
            gl,
            GlCloudRenderFormatProfile.DesktopFloatingPoint);
        using var output = new GlCloudTemporalRenderTarget(
            gl,
            GlCloudRenderFormatProfile.DesktopFloatingPoint);
        Assert.True(source.EnsureSize(cloudSize, cloudSize));
        Assert.True(output.EnsureSize(outputSize, outputSize));

        UploadRgba32f(
            gl,
            source.ColorTextureHandle,
            cloudSize,
            cloudSize,
            RepeatRgbaFloat(cloudSize, cloudSize, 0.18f, 0.2f, 0.22f, 0.6f));
        UploadRg32f(
            gl,
            source.DataTextureHandle,
            cloudSize,
            cloudSize,
            RepeatDirectMetadata(cloudSize, cloudSize, 0.05f, 0.5f));

        var sceneDepthTexture = gl.GenTexture();
        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            AllocateRgba8(
                gl,
                sceneDepthTexture,
                outputSize,
                outputSize,
                RepeatPixel(outputSize, outputSize, 255, 0, 0, 255));
            ConfigureUpsampleUniforms(
                gl,
                upsample,
                cloudSize,
                directMetadata: true,
                hasSceneDepth: false,
                hdrPresent: true,
                applyCloudEncoding: true);

            output.Clear();
            output.BindDraw(includeMoments: false);
            gl.Disable(EnableCap.Blend);
            DrawUpsample(
                gl,
                upsample,
                quadVao,
                source,
                sceneDepthTexture);
            Assert.Equal(GLEnum.NoError, gl.GetError());

            var pixels = ReadTextureRgbaFloat(
                gl,
                output.ColorTextureHandle,
                outputSize,
                outputSize);
            var centerIndex = (3 * outputSize + 3) * 4;
            var centerAlpha = pixels[centerIndex + 3];
            var cornerAlpha = pixels[3];
            Assert.InRange(centerAlpha, 0.995f, 1.001f);
            Assert.InRange(cornerAlpha, 0.58f, 0.62f);
            // Sealed disc alpha must keep matching premultiplied RGB so the sun is
            // covered by cloud radiance, not a dark cookie-cutter hole.
            Assert.True(
                pixels[centerIndex] > pixels[0] * 1.4f &&
                pixels[centerIndex + 1] > pixels[1] * 1.4f &&
                pixels[centerIndex + 2] > pixels[2] * 1.4f,
                "Direct-disc seal must rescale premultiplied RGB with composite alpha.");

            SetUniform1(gl, upsample, "uSunDiscVisibility", 0f);
            output.Clear();
            output.BindDraw(includeMoments: false);
            DrawUpsample(
                gl,
                upsample,
                quadVao,
                source,
                sceneDepthTexture);
            var disabledPixels = ReadTextureRgbaFloat(
                gl,
                output.ColorTextureHandle,
                outputSize,
                outputSize);
            var disabledCenterAlpha =
                disabledPixels[((3 * outputSize + 3) * 4) + 3];
            Assert.InRange(disabledCenterAlpha, 0.58f, 0.62f);
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.DeleteTexture(sceneDepthTexture);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    private static void ValidateCloudTemporalMoments(GL gl, GlShaderProgram temporal)
    {
        const int cloudSize = 4;
        var profile = GlCloudRenderFormatProfile.DesktopFloatingPointMoments;
        using var current = new GlCloudTemporalRenderTarget(gl, profile);
        using var resolve = new GlCloudTemporalRenderTarget(gl, profile);
        using var history = new GlCloudTemporalRenderTarget(gl, profile);
        Assert.True(current.EnsureSize(cloudSize, cloudSize));
        Assert.True(resolve.EnsureSize(cloudSize, cloudSize));
        Assert.True(history.EnsureSize(cloudSize, cloudSize));

        // Premultiplied RGB corresponds to straight linear radiance (0.8, 0.4, 0.2).
        UploadRgba32f(gl, current.ColorTextureHandle, cloudSize, cloudSize,
            RepeatRgbaFloat(cloudSize, cloudSize, 0.4f, 0.2f, 0.1f, 0.5f));
        UploadRg32f(gl, current.DataTextureHandle, cloudSize, cloudSize,
            RepeatDirectMetadata(cloudSize, cloudSize, 1f, 0.5f));
        history.Clear();
        var invalidHistoryMoments = ReadTextureRgFloat(
            gl, history.MomentTextureHandle, cloudSize, cloudSize);
        Assert.InRange(invalidHistoryMoments[0], -1.01f, -0.99f);

        resolve.BindDraw();
        resolve.Clear();
        resolve.BindDraw();
        temporal.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, current.ColorTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, current.DataTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, history.ColorTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture3);
        gl.BindTexture(TextureTarget.Texture2D, history.DataTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture4);
        gl.BindTexture(TextureTarget.Texture2D, history.MomentTextureHandle);
        SetUniform1(gl, temporal, "uCurrentClouds", 0);
        SetUniform1(gl, temporal, "uCurrentCloudData", 1);
        SetUniform1(gl, temporal, "uHistoryClouds", 2);
        SetUniform1(gl, temporal, "uHistoryCloudData", 3);
        SetUniform1(gl, temporal, "uHistoryCloudMoments", 4);
        SetUniform1(gl, temporal, "uTemporalWeight", 0.72f);
        SetUniform1(gl, temporal, "uMomentSigma", 1.5f);
        SetUniform1(gl, temporal, "uMomentMinBand", 0.015f);
        SetUniform1(gl, temporal, "uHistoryConfidence", 0f);
        SetUniform1(gl, temporal, "uHasHistory", 0);
        SetUniform1(gl, temporal, "uHasMoments", 1);
        SetUniform1(gl, temporal, "uCloudDataDirect", 1);
        SetUniform2(gl, temporal, "uTexelSize", 1f / cloudSize, 1f / cloudSize);
        SetUniform2(gl, temporal, "uWindDelta", 0f, 0f);
        SetUniform2(gl, temporal, "uCirrusWindDelta", 0f, 0f);
        SetUniform3(gl, temporal, "uCameraPos", 0f, 0f, 0f);
        SetUniform3(gl, temporal, "uPrevCameraPos", 0f, 0f, 0f);
        SetIdentityMatrix(gl, temporal, "uInvViewProj");
        SetIdentityMatrix(gl, temporal, "uPrevViewProj");

        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            while (gl.GetError() != GLEnum.NoError)
            {
            }

            gl.BindVertexArray(quadVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            gl.BindVertexArray(0);
            Assert.Equal(GLEnum.NoError, gl.GetError());

            var moments = ReadTextureRgFloat(
                gl, resolve.MomentTextureHandle, cloudSize, cloudSize);
            const float expectedLuminance = 0.4706f;
            Assert.InRange(moments[0], expectedLuminance - 0.005f, expectedLuminance + 0.005f);
            Assert.InRange(
                moments[1],
                expectedLuminance * expectedLuminance - 0.01f,
                expectedLuminance * expectedLuminance + 0.01f);

            Assert.True(history.CopyFrom(resolve));
            var copied = ReadTextureRgFloat(
                gl, history.MomentTextureHandle, cloudSize, cloudSize);
            Assert.InRange(copied[0], expectedLuminance - 0.005f, expectedLuminance + 0.005f);

            // Exercise the accepted-history branch as well as initial moment generation.
            resolve.BindDraw();
            resolve.Clear();
            resolve.BindDraw();
            temporal.Use();
            SetUniform1(gl, temporal, "uHasHistory", 1);
            SetUniform1(gl, temporal, "uHistoryConfidence", 0.5f);
            while (gl.GetError() != GLEnum.NoError)
            {
            }

            gl.BindVertexArray(quadVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            gl.BindVertexArray(0);
            Assert.Equal(GLEnum.NoError, gl.GetError());
            var accumulated = ReadTextureRgFloat(
                gl, resolve.MomentTextureHandle, cloudSize, cloudSize);
            Assert.All(accumulated, value => Assert.True(float.IsFinite(value)));
            Assert.True(accumulated[0] >= 0f);
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    private static void ValidateCloudEdgeRepair(
        GL gl,
        GlShaderProgram repair,
        GlTexture3D texture3d)
    {
        const int sourceSize = 4;
        const int outputSize = 8;
        var profile = GlCloudRenderFormatProfile.DesktopFloatingPoint;
        using var source = new GlCloudTemporalRenderTarget(gl, profile);
        using var output = new GlCloudTemporalRenderTarget(gl, profile);
        Assert.True(source.EnsureSize(sourceSize, sourceSize));
        Assert.True(output.EnsureSize(outputSize, outputSize));

        var colors = new float[sourceSize * sourceSize * 4];
        var metadata = new float[sourceSize * sourceSize * 2];
        for (var y = 0; y < sourceSize; y++)
        {
            for (var x = 0; x < sourceSize; x++)
            {
                var colorIndex = (y * sourceSize + x) * 4;
                var alpha = x < sourceSize / 2 ? 0.05f : 0.65f;
                colors[colorIndex] = 0.3f * alpha;
                colors[colorIndex + 1] = 0.4f * alpha;
                colors[colorIndex + 2] = 0.5f * alpha;
                colors[colorIndex + 3] = alpha;
                var dataIndex = (y * sourceSize + x) * 2;
                metadata[dataIndex] = x < sourceSize / 2 ? 110f : 114f;
                metadata[dataIndex + 1] = 0.5f;
            }
        }

        UploadRgba32f(gl, source.ColorTextureHandle, sourceSize, sourceSize, colors);
        UploadRg32f(gl, source.DataTextureHandle, sourceSize, sourceSize, metadata);
        output.Clear();
        output.BindDraw(includeMoments: false);
        repair.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, source.ColorTextureHandle);
        SetUniform1(gl, repair, "uClouds", 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
        SetUniform1(gl, repair, "uCloudData", 1);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
        SetUniform1(gl, repair, "uSceneDepth", 2);
        texture3d.Bind(3);
        SetUniform1(gl, repair, "uCloudNoise", 3);
        texture3d.Bind(4);
        SetUniform1(gl, repair, "uDetailNoise", 4);
        texture3d.Bind(5);
        SetUniform1(gl, repair, "uCloudStbn", 5);
        gl.ActiveTexture(TextureUnit.Texture6);
        gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
        SetUniform1(gl, repair, "uCoverageMap", 6);
        gl.ActiveTexture(TextureUnit.Texture7);
        gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
        SetUniform1(gl, repair, "uSkyViewLut", 7);

        SetUniform2(gl, repair, "uCloudTexelSize", 1f / sourceSize, 1f / sourceSize);
        SetIdentityMatrix(gl, repair, "uInvViewProj");
        SetUniform3(gl, repair, "uCameraPos", 0f, 0f, -1f);
        SetUniform3(gl, repair, "uSunDir", -0.3f, -0.8f, -0.4f);
        SetUniform1(gl, repair, "uSunIntensity", 1f);
        SetUniform1(gl, repair, "uGroundWorldY", -100f);
        SetUniform1(gl, repair, "uPlanetRadius", 1f);
        SetUniform1(gl, repair, "uLayerHeight", 4.8f);
        SetUniform1(gl, repair, "uVolumeHeight", 60f);
        SetUniform1(gl, repair, "uDensity", 0.75f);
        SetUniform1(gl, repair, "uCoverageScale", 1.2f);
        SetUniform1(gl, repair, "uVolumeSize", 178f);
        SetUniform3(gl, repair, "uWindOffset", 0f, 0f, 0f);
        SetUniform1(gl, repair, "uCirrusStrength", 0.13f);
        SetUniform2(gl, repair, "uCirrusWindOffset", 0f, 0f);
        SetUniform2(gl, repair, "uCirrusWindDir", 0.8f, 0.6f);
        SetUniform1(gl, repair, "uMarchSteps", 0);
        SetUniform1(gl, repair, "uHasSceneDepth", 0);
        SetUniform1(gl, repair, "uHasCloudNoise", 0);
        SetUniform1(gl, repair, "uHasDetailNoise", 0);
        SetUniform1(gl, repair, "uHasCloudStbn", 0);
        SetUniform1(gl, repair, "uHasCoverageMap", 0);
        SetUniform1(gl, repair, "uHasSkyLut", 0);
        SetUniform1(gl, repair, "uSourceCloudDataDirect", 1);
        SetUniform1(gl, repair, "uCloudFrameIndex", 7);
        SetUniform1(gl, repair, "uDensityAssetVersion", 2);

        var quadVao = CreateFullscreenQuad(gl, out var quadVbo);
        try
        {
            while (gl.GetError() != GLEnum.NoError)
            {
            }

            gl.BindVertexArray(quadVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            gl.BindVertexArray(0);
            Assert.Equal(GLEnum.NoError, gl.GetError());
            var repaired = ReadTextureRgbaFloat(
                gl,
                output.ColorTextureHandle,
                outputSize,
                outputSize);
            Assert.All(repaired, value => Assert.True(float.IsFinite(value)));

            // The full-resolution repair target is later composited over the already
            // presented scene. Prove an opaque scene hit clears repaired cloud output,
            // rather than allowing the optional retrace to cover terrain or a subject.
            var sceneDepth = gl.GenTexture();
            try
            {
                AllocateR32f(
                    gl,
                    sceneDepth,
                    outputSize,
                    outputSize,
                    Enumerable.Repeat(0.5f, outputSize * outputSize).ToArray());
                gl.ActiveTexture(TextureUnit.Texture2);
                gl.BindTexture(TextureTarget.Texture2D, sceneDepth);
                SetUniform1(gl, repair, "uHasSceneDepth", 1);
                output.Clear();
                output.BindDraw(includeMoments: false);
                repair.Use();
                gl.BindVertexArray(quadVao);
                gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
                gl.BindVertexArray(0);
                Assert.Equal(GLEnum.NoError, gl.GetError());

                var occluded = ReadTextureRgbaFloat(
                    gl,
                    output.ColorTextureHandle,
                    outputSize,
                    outputSize);
                for (var i = 3; i < occluded.Length; i += 4)
                {
                    Assert.InRange(occluded[i], 0f, 1e-4f);
                }
            }
            finally
            {
                SetUniform1(gl, repair, "uHasSceneDepth", 0);
                gl.DeleteTexture(sceneDepth);
            }
        }
        finally
        {
            gl.DeleteBuffer(quadVbo);
            gl.DeleteVertexArray(quadVao);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    private static void DrawUpsample(
        GL gl,
        GlShaderProgram program,
        uint quadVao,
        GlCloudTemporalRenderTarget source,
        uint sceneDepthTexture)
    {
        program.Use();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, source.ColorTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, source.DataTextureHandle);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, sceneDepthTexture);
        gl.BindVertexArray(quadVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.BindVertexArray(0);
    }

    private static void ConfigureUpsampleUniforms(
        GL gl,
        GlShaderProgram program,
        int cloudSize,
        bool directMetadata,
        bool hasSceneDepth = true,
        bool hdrPresent = true,
        bool applyCloudEncoding = false)
    {
        program.Use();
        SetUniform1(gl, program, "uClouds", 0);
        SetUniform1(gl, program, "uCloudData", 1);
        SetUniform1(gl, program, "uSceneDepth", 2);
        SetUniform1(gl, program, "uHasSceneDepth", hasSceneDepth ? 1 : 0);
        SetUniform1(gl, program, "uCloudDataDirect", directMetadata ? 1 : 0);
        SetUniform1(gl, program, "uCloudExposure", 1f);
        SetUniform1(gl, program, "uHdrPresent", hdrPresent ? 1 : 0);
        SetUniform1(gl, program, "uHdrPaperWhiteNits", 80f);
        SetUniform1(gl, program, "uHdrPeakNits", 400f);
        SetUniform1(gl, program, "uApplyCloudEncoding", applyCloudEncoding ? 1 : 0);
        SetUniform1(gl, program, "uCloudSourceFullResolution", 0);
        SetUniform2(gl, program, "uCloudTexelSize", 1f / cloudSize, 1f / cloudSize);
        SetUniform3(gl, program, "uCameraPos", 0f, 0f, -1f);
        SetUniform3(gl, program, "uSunDir", 0f, 0f, -1f);
        SetUniform1(gl, program, "uSunCosDiscEdge", 0.98f);
        SetUniform1(gl, program, "uSunDiscVisibility", 1f);
        var matrixLoc = program.GetUniformLocation("uInvViewProj");
        if (matrixLoc >= 0)
        {
            var identity = Matrix4x4.Identity;
            gl.UniformMatrix4(matrixLoc, 1, false, in identity.M11);
        }
    }

    private static byte[] RepeatPackedDistance(int width, int height, float distance)
    {
        var normalized = Math.Max(distance, 0f) / (Math.Max(distance, 0f) + 256f);
        var encodedY = normalized * 255f - MathF.Floor(normalized * 255f);
        var encodedX = normalized - encodedY / 255f;
        return RepeatPixel(width, height,
            (byte)Math.Clamp((int)MathF.Round(encodedX * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(encodedY * 255f), 0, 255),
            128,
            255);
    }

    private static byte[] RepeatPixel(int width, int height, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return pixels;
    }

    private static float[] RepeatDirectMetadata(
        int width,
        int height,
        float distance,
        float cloudKind)
    {
        var pixels = new float[width * height * 2];
        for (var i = 0; i < pixels.Length; i += 2)
        {
            pixels[i] = distance;
            pixels[i + 1] = cloudKind;
        }

        return pixels;
    }

    private static float[] RepeatRgbaFloat(
        int width,
        int height,
        float red,
        float green,
        float blue,
        float alpha)
    {
        var pixels = new float[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = red;
            pixels[i + 1] = green;
            pixels[i + 2] = blue;
            pixels[i + 3] = alpha;
        }

        return pixels;
    }

    private static unsafe void UploadRgba8(GL gl, uint texture, int width, int height, byte[] pixels)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (byte* ptr = pixels)
        {
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height,
                PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
        }
    }

    private static unsafe void AllocateRgba8(GL gl, uint texture, int width, int height, byte[] pixels)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (byte* ptr = pixels)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
        }
    }

    private static unsafe void UploadRg32f(GL gl, uint texture, int width, int height, float[] pixels)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (float* ptr = pixels)
        {
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height,
                PixelFormat.RG, PixelType.Float, ptr);
        }
    }

    private static unsafe void AllocateR32f(
        GL gl,
        uint texture,
        int width,
        int height,
        float[] pixels)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (float* ptr = pixels)
        {
            gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.R32f,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.Red,
                PixelType.Float,
                ptr);
        }

        gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)GLEnum.Nearest);
        gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)GLEnum.Nearest);
    }

    private static unsafe void UploadRgba32f(GL gl, uint texture, int width, int height, float[] pixels)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (float* ptr = pixels)
        {
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height,
                PixelFormat.Rgba, PixelType.Float, ptr);
        }
    }

    private static unsafe float[] ReadTextureRgbaFloat(
        GL gl,
        uint texture,
        int width,
        int height)
    {
        var framebuffer = gl.GenFramebuffer();
        var pixels = new float[width * height * 4];
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texture,
                0);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            Assert.Equal(
                GLEnum.FramebufferComplete,
                gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            fixed (float* ptr = pixels)
            {
                gl.ReadPixels(
                    0,
                    0,
                    (uint)width,
                    (uint)height,
                    PixelFormat.Rgba,
                    PixelType.Float,
                    ptr);
            }

            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
        }

        return pixels;
    }

    private static unsafe float[] ReadTextureRgFloat(
        GL gl,
        uint texture,
        int width,
        int height)
    {
        var framebuffer = gl.GenFramebuffer();
        var pixels = new float[width * height * 2];
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texture,
                0);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            Assert.Equal(
                GLEnum.FramebufferComplete,
                gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            fixed (float* ptr = pixels)
            {
                gl.ReadPixels(
                    0,
                    0,
                    (uint)width,
                    (uint)height,
                    PixelFormat.RG,
                    PixelType.Float,
                    ptr);
            }

            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
        }

        return pixels;
    }

    private static unsafe ushort[] ReadR16UiTexture3D(
        GL gl,
        uint texture)
    {
        var previous =
            (uint)Math.Max(0, gl.GetInteger(GetPName.TextureBinding3D));
        var values = new ushort[
            PreviewSparseCloudVolumeContract.PageTableEntryCount];
        try
        {
            gl.BindTexture(TextureTarget.Texture3D, texture);
            fixed (ushort* pointer = values)
            {
                gl.GetTexImage(
                    TextureTarget.Texture3D,
                    0,
                    PixelFormat.RedInteger,
                    PixelType.UnsignedShort,
                    pointer);
            }

            Assert.Equal(GLEnum.NoError, gl.GetError());
            return values;
        }
        finally
        {
            gl.BindTexture(TextureTarget.Texture3D, previous);
        }
    }

    private static unsafe byte[] ReadRg8Texture3D(
        GL gl,
        uint texture,
        int size)
    {
        var previous =
            (uint)Math.Max(0, gl.GetInteger(GetPName.TextureBinding3D));
        var values = new byte[checked(size * size * size * 2)];
        try
        {
            gl.BindTexture(TextureTarget.Texture3D, texture);
            fixed (byte* pointer = values)
            {
                gl.GetTexImage(
                    TextureTarget.Texture3D,
                    0,
                    PixelFormat.RG,
                    PixelType.UnsignedByte,
                    pointer);
            }

            Assert.Equal(GLEnum.NoError, gl.GetError());
            return values;
        }
        finally
        {
            gl.BindTexture(TextureTarget.Texture3D, previous);
        }
    }

    private static byte[] ExtractSparseBrick(
        ReadOnlySpan<byte> atlas,
        int physicalBrickIndex)
    {
        var atlasSize = PreviewSparseCloudVolumeContract.AtlasTexelSize;
        var brickSize = PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        var brickCoordinate =
            PreviewSparseCloudVolumeContract.PhysicalBrickAtlasCoordinate(
                physicalBrickIndex);
        var result = new byte[brickSize * brickSize * brickSize * 2];
        for (var z = 0; z < brickSize; z++)
        {
            for (var y = 0; y < brickSize; y++)
            {
                for (var x = 0; x < brickSize; x++)
                {
                    var atlasX = brickCoordinate.X * brickSize + x;
                    var atlasY = brickCoordinate.Y * brickSize + y;
                    var atlasZ = brickCoordinate.Z * brickSize + z;
                    var source =
                        ((atlasZ * atlasSize + atlasY) * atlasSize + atlasX) *
                        2;
                    var destination = ((z * brickSize + y) * brickSize + x) * 2;
                    result[destination] = atlas[source];
                    result[destination + 1] = atlas[source + 1];
                }
            }
        }

        return result;
    }

    private static void AssertSparseBrickSharedXBorderMatches(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right)
    {
        var size = PreviewSparseCloudVolumeContract.PhysicalBrickSize;
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                var leftIndex = ((z * size + y) * size + (size - 1)) * 2;
                var rightIndex = ((z * size + y) * size + 1) * 2;
                Assert.Equal(left[leftIndex], right[rightIndex]);
                Assert.Equal(left[leftIndex + 1], right[rightIndex + 1]);
            }
        }
    }

    private static uint CreateFullscreenQuad(GL gl, out uint vbo)
    {
        var vao = gl.GenVertexArray();
        vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        ReadOnlySpan<float> vertices =
        [
            -1f, -1f, 1f, -1f, 1f, 1f,
            -1f, -1f, 1f, 1f, -1f, 1f,
        ];
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);
        unsafe
        {
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }

        gl.BindVertexArray(0);
        return vao;
    }

    [Fact]
    public void HiddenWglContext_CompilesScreenSpaceAoShaders()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("AUTOPBR_RUN_LIVE_GL_SMOKE"),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var diagnostics = new List<string>();
        using var context = PreviewDesktopWglContext.TryCreate(
            [new GlVersion(GlProfileType.OpenGL, 4, 6)],
            IntPtr.Zero,
            diagnostics.Add,
            probePresentationAdapter: false);
        Assert.NotNull(context);

        context!.Invoke(() =>
        {
            using (context.BindOnOwnerThread())
            {
                var gl = context.Gl;
                var caps = PreviewGlCapabilities.FromGl(gl, useOpenGlEs: false, context.VersionString);
                Assert.True(caps.CanUseScreenSpaceAo);

                var compile = new GlShaderCompileContext(gl, useOpenGlEs: false, caps.Vendor, caps.Renderer);
                string? err;
                using var ssao = compile.CreateProgram(
                    "genesis_godrays.vert", "genesis_ssao.frag", out err, "ssao-live-smoke");
                Assert.True(ssao.IsValid, err ?? "SSAO link failed");

                using var gtao = compile.CreateProgram(
                    "genesis_godrays.vert", "genesis_gtao.frag", out err, "gtao-live-smoke");
                Assert.True(gtao.IsValid, err ?? "GTAO link failed");

                using var blur = compile.CreateProgram(
                    "genesis_godrays.vert", "genesis_ao_bilateral.frag", out err, "ao-blur-live-smoke");
                Assert.True(blur.IsValid, err ?? "AO bilateral link failed");

                using var temporal = compile.CreateProgram(
                    "genesis_godrays.vert", "genesis_ao_temporal.frag", out err, "ao-temporal-live-smoke");
                Assert.True(temporal.IsValid, err ?? "AO temporal link failed");

                using var composite = compile.CreateProgram(
                    "genesis_godrays.vert", "genesis_ao_composite.frag", out err, "ao-composite-live-smoke");
                Assert.True(composite.IsValid, err ?? "AO composite link failed");

                using var capture = new GlSceneCaptureTarget(gl, useOpenGlEs: false);
                Assert.True(capture.EnsureSize(64, 64));
                Assert.True(capture.HasViewNormals);
                Assert.True(capture.IsValid);
            }
        });
    }

    private static void SetUniform1(GL gl, GlShaderProgram program, string name, int value)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0)
        {
            gl.Uniform1(location, value);
        }
    }

    private static void SetUniform1(GL gl, GlShaderProgram program, string name, float value)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0)
        {
            gl.Uniform1(location, value);
        }
    }

    private static void SetUniform2(GL gl, GlShaderProgram program, string name, float x, float y)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0)
        {
            gl.Uniform2(location, x, y);
        }
    }

    private static void SetUniform3(GL gl, GlShaderProgram program, string name, float x, float y, float z)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0)
        {
            gl.Uniform3(location, x, y, z);
        }
    }

    private static void SetIdentityMatrix(GL gl, GlShaderProgram program, string name)
    {
        var location = program.GetUniformLocation(name);
        if (location >= 0)
        {
            var identity = Matrix4x4.Identity;
            gl.UniformMatrix4(location, 1, false, in identity.M11);
        }
    }
}
