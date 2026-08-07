using AutoPBR.App.Lang;
using AutoPBR.App.Controls;
using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

using System.Numerics;

namespace AutoPBR.App.Tests;

public sealed partial class PreviewRenderingTests
{
    [Fact]
    public void RenderSettingsDefaultsAreUsable()
    {
        var s = new PreviewRenderSettings();
        Assert.Equal(1f, s.NormalStrength);
        Assert.True(s.EnableParallax);
        Assert.True(s.NearestTextureFilter);
        Assert.True(s.ShowBackgroundGrid);
        Assert.True(s.ShowGroundMesh);
        Assert.True(s.ShowCornerAxes);
        Assert.True(s.DrawPreviewSubject);
        Assert.Equal(PreviewEntityAlphaMode.Cutout, s.EntityAlphaMode);
        Assert.True(s.EnableEntityLabPbrShading);
        Assert.False(s.EnableEntityParallax);
    }

    [Fact]
    public void RenderSettingsGenesisDefaultsAreSensible()
    {
        var s = new PreviewRenderSettings();
        Assert.True(s.EnableSss);
        Assert.True(s.EnableParallaxShadow);
        Assert.Equal(64, s.ParallaxTraceLayers);
        Assert.Equal(5, s.ParallaxRefineSteps);
        Assert.Equal(24, s.ParallaxShadowSamples);
        Assert.Equal(1.25f, s.ParallaxShadowSoftness);
        Assert.Equal(0.45f, s.ParallaxMaxUvShift);
        Assert.Equal(0.0, PreviewStageConstants.ParallaxHeightStrengthMin);
        Assert.Equal(4.0, PreviewStageConstants.ParallaxHeightStrengthMax);
        Assert.Equal(0.05, PreviewStageConstants.ParallaxMaxUvShiftMin);
        Assert.Equal(4.0, PreviewStageConstants.ParallaxMaxUvShiftMax);
        Assert.True(s.EnableIbl);
        Assert.True(s.EnableAtmosphericSky);
        Assert.Equal(2.6f, s.AtmosphereTurbidity);
        Assert.Equal(10f, s.AtmosphereSunIntensity);
        Assert.Equal(1.35f, s.AtmosphereHorizonFalloff);
        Assert.Equal(0.35f, s.AtmosphereSunDiscStrength);
        Assert.Equal(1f, s.AtmosphereSunDiscBrightness);
        Assert.Equal(1.35f, s.AtmosphereMoonDiscStrength);
        Assert.Equal(1f, s.AtmosphereMoonDiscSize);
        Assert.Equal(0.7f, s.AtmosphereMoonGlowStrength);
        Assert.Equal(1.25f, s.AtmosphereMoonTextureSharpness);
        Assert.Equal(1f, s.MoonWorldLightIntensity);
        Assert.Equal(1f, s.SssStrength);
        Assert.Equal(0.6f, s.IblStrength);
        Assert.Equal(1f, s.EmissionStrength);
    }

    [Fact]
    public void RenderSettingsShadowDefaultsAreSensible()
    {
        var s = new PreviewRenderSettings();
        Assert.True(s.EnableShadows);
        Assert.Equal(4096, s.ShadowMapResolution);
        Assert.Equal(128f, s.ShadowDistance);
        Assert.Equal(0.002f, s.ShadowMinBias);
        Assert.Equal(0.012f, s.ShadowMaxBias);
        Assert.Equal(1.0f, s.ShadowSoftnessTexels);
        Assert.Equal(1.0f, s.ShadowStrength);
        // Phase 3 stub: persisted boolean only, defaults to false in Phase 2.
        Assert.False(s.EnableShadowCascades);
    }

    [Fact]
    public void RenderSettingsShadowDistance_ClampsToSupportedRange()
    {
        Assert.Equal(32f, OpenGlPreviewBackend.ShadowDistanceMin);
        Assert.Equal(256f, OpenGlPreviewBackend.ShadowDistanceMax);
        Assert.Equal(128f, OpenGlPreviewBackend.ShadowDistanceDefault);
        Assert.InRange(
            Math.Clamp(16f, OpenGlPreviewBackend.ShadowDistanceMin, OpenGlPreviewBackend.ShadowDistanceMax),
            32f,
            256f);
        Assert.InRange(
            Math.Clamp(512f, OpenGlPreviewBackend.ShadowDistanceMin, OpenGlPreviewBackend.ShadowDistanceMax),
            32f,
            256f);
    }

    [Fact]
    public void ShadowPass_PreparesTerrainCullAndSubjectUploadsOncePerFrame()
    {
        var shadow = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassShadow.cs");
        var ground = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.GroundTerrain.cs");

        Assert.Contains("PrepareTerrainShadowCasterSelections(", shadow, StringComparison.Ordinal);
        Assert.Contains("PrepareShadowSubjectGpuUploads(ref frame);", shadow, StringComparison.Ordinal);
        Assert.Contains("_frameSubjectGpuUploadsReady = false;", shadow, StringComparison.Ordinal);
        Assert.Contains("nearCasterDist = nearHalf + casterPad", shadow, StringComparison.Ordinal);
        Assert.Contains("midCasterDist = midHalf + casterPad", shadow, StringComparison.Ordinal);
        Assert.Contains("Parallel.Invoke(", ground, StringComparison.Ordinal);
        Assert.Contains("TryPrepareTerrainShadowCasterSelectionsGpu(", ground, StringComparison.Ordinal);
        Assert.Contains("TryDrawTerrainShadowCastersGpu(", ground, StringComparison.Ordinal);
        Assert.Contains("TerrainShadowRequiresCutoutSupport()", ground, StringComparison.Ordinal);
        Assert.Contains("PreviewTerrainGrassSlots.VegetationBase", ground, StringComparison.Ordinal);
        Assert.Contains(
            "CPU Select (per-material albedo discard)",
            ground,
            StringComparison.Ordinal);
        Assert.DoesNotContain("TryEnsureTerrainShadowGpuCutoutPath", ground, StringComparison.Ordinal);
        Assert.Contains("MultiDrawIndirectCount", ground, StringComparison.Ordinal);
        Assert.Contains("GlTerrainMeshPool", ground, StringComparison.Ordinal);
        Assert.Contains("EnsureFrameSubjectGpuUploads(ref frame);", shadow, StringComparison.Ordinal);
        Assert.Contains(
            "_frameSubjectUseMaterialDrawRecords = TryUploadGenesisMaterialDrawRecords(ref frame);",
            shadow,
            StringComparison.Ordinal);
        Assert.Contains(
            "_shadowSubjectUseMaterialDrawRecords = _frameSubjectUseMaterialDrawRecords;",
            shadow,
            StringComparison.Ordinal);

        // Shadows-off must skip caster AABB/VP fit (keeps light/model setup only).
        var earlyOut = shadow.IndexOf("if (!frame.Settings.EnableShadows)", StringComparison.Ordinal);
        var casterFit = shadow.IndexOf("TryGetShadowCasterBoundsForFrame", StringComparison.Ordinal);
        Assert.True(earlyOut >= 0 && casterFit > earlyOut,
            "EnableShadows early-out must precede terrain caster fit");
    }

    [Fact]
    public void RenderSettingsVolumetricDefaultsAreSensible()
    {
        var s = new PreviewRenderSettings();
        Assert.True(s.EnableGodRays);
        Assert.True(s.EnableVolumeGodRays);
        Assert.False(s.EnableVolumetricClouds);
        Assert.Equal(1, s.VolumetricQuality);
        Assert.Equal(0.45f, s.GodRayStrength);
        Assert.False(s.LogVolumetricTiming);
        Assert.False(s.LogPreviewTaaDiagnostics);
        Assert.False(s.LogGpuPassTimings);
        Assert.False(s.ShowExpandedGpuTimingHud);
    }

    [Fact]
    public void ScenePass_GroundParallaxUsesGlobalToggleNotEntityGate()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassScene.cs");

        Assert.Contains("var groundParallax = frame.Settings.EnableParallax && _grassGroundHasHeight;", source, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.EnableParallaxAo, groundParallax && frame.Settings.EnableParallaxAo ? 1 : 0);", source, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.EnableParallaxShadow, groundParallax && frame.Settings.EnableParallaxShadow ? 1 : 0);", source, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.EnableParallax, frame.EnableParallaxEff ? 1 : 0);", source, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.EnableParallaxAo, frame.EnableParallaxAoEff ? 1 : 0);", source, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.EnableParallaxShadow, frame.EnableParallaxShadowEff ? 1 : 0);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenePass_GroundDrawDisablesTessellationAndMaterialDrawRecords()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassScene.cs");

        var groundBlockStart = source.IndexOf(
            "if (frame.Settings.ShowGroundMesh &&",
            StringComparison.Ordinal);
        Assert.True(groundBlockStart >= 0);
        // Prefer the chunked ground draw; fall back if the marker moves again.
        var groundDraw = source.IndexOf("DrawGroundTerrainChunks(", groundBlockStart, StringComparison.Ordinal);
        if (groundDraw < 0)
        {
            groundDraw = source.IndexOf("_groundMesh.Draw(_mainProgramUsesTessellation);", groundBlockStart, StringComparison.Ordinal);
        }

        Assert.True(groundDraw > groundBlockStart);
        var groundBlock = source[groundBlockStart..groundDraw];

        Assert.Contains("SetIntLoc(u.EnableTessellationDisplacement, 0);", groundBlock, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.GenesisUseMaterialDrawRecord, 0);", groundBlock, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.GenesisUseMaterialTextureArray, 0);", groundBlock, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.GenesisDrawRecordIndex, 0);", groundBlock, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.IsGroundPass, 1);", groundBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void TerrainMeshPool_GrowthIsTransactionalAndBounded()
    {
        var pool = LoadSource(
            ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "GlTerrainMeshPool.cs");
        var ground = LoadSource(
            ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.GroundTerrain.cs");

        Assert.Contains("TerrainMeshPoolBudgetDefaultBytes", pool, StringComparison.Ordinal);
        Assert.Contains("ConfigureBudgetCeiling", pool, StringComparison.Ordinal);
        Assert.Contains("TryRaiseBudgetCeiling", pool, StringComparison.Ordinal);
        Assert.Contains("targetBytes > _maxTotalBufferBytes", pool, StringComparison.Ordinal);
        Assert.Contains("TryCreateReplacementBuffer(", pool, StringComparison.Ordinal);
        Assert.Contains("RestoreLiveBindings();", pool, StringComparison.Ordinal);
        Assert.Contains("vertsFromFree", pool, StringComparison.Ordinal);
        Assert.Contains("indicesFromFree", pool, StringComparison.Ordinal);
        Assert.Contains("GrowCapacity(_vertexFloatCapacity", pool, StringComparison.Ordinal);
        Assert.Contains("GrowCapacity(_indexCapacity", pool, StringComparison.Ordinal);
        Assert.Contains("ConstrainGrowthToBudget(", pool, StringComparison.Ordinal);
        Assert.Contains("GrowCapacityConservatively(", pool, StringComparison.Ordinal);
        Assert.Contains("AllocationFailureCount++", pool, StringComparison.Ordinal);
        Assert.Contains("TryPreallocateFixedCapacity", pool, StringComparison.Ordinal);
        Assert.Contains("preserving existing terrain", ground, StringComparison.Ordinal);
        Assert.Contains("_terrainDeferredChunks", ground, StringComparison.Ordinal);
        Assert.Contains("DeferRemainingTerrainChunksAtPoolLimit();", ground, StringComparison.Ordinal);
        Assert.Contains(
            "arena.VertexCapacityBytes,",
            ground,
            StringComparison.Ordinal);
        var initStart = ground.IndexOf("private void InitTerrainStreaming", StringComparison.Ordinal);
        var initEnd = ground.IndexOf("private void EnsureTerrainGpuFullMeshBaker", initStart, StringComparison.Ordinal);
        Assert.True(initStart >= 0 && initEnd > initStart);
        Assert.DoesNotContain(
            "EnsureTerrainMeshPool(gl);",
            ground[initStart..initEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "arena.IndexCapacityBytes)",
            ground,
            StringComparison.Ordinal);

        var preallocate = ground.IndexOf(
            "TryPreallocateFixedCapacity(",
            StringComparison.Ordinal);
        var preallocateBudget = ground.LastIndexOf(
            "ConfigureBudgetCeiling(",
            preallocate,
            StringComparison.Ordinal);
        var firstUpload = ground.IndexOf(
            "UploadTerrainChunk(frame.Gl, cpu);",
            StringComparison.Ordinal);
        Assert.True(
            preallocate >= 0 && firstUpload > preallocate,
            "The arena-sized GL backing store must be allocated before streamed uploads begin.");
        Assert.True(
            preallocateBudget >= 0 && preallocateBudget < preallocate,
            "The hardware-aware pool ceiling must be configured before fixed preallocation.");

        var replacementUpload = ground.IndexOf(
            "var replacement = pool.Upload(cpu.InterleavedVertices, cpu.Indices, staging);",
            StringComparison.Ordinal);
        Assert.True(replacementUpload >= 0, "Replacement upload through staging must exist.");
        var oldAllocationFree = ground.IndexOf(
            "pool.Free(existing.Allocation);",
            replacementUpload,
            StringComparison.Ordinal);
        Assert.True(
            oldAllocationFree > replacementUpload,
            "A replacement upload must succeed before the last visible allocation is freed.");
        Assert.Contains("AllowLiveBufferGrowth", ground, StringComparison.Ordinal);
        Assert.Contains("TryAdmitTerrainArenaReservation", ground, StringComparison.Ordinal);
        Assert.Contains("HasCoarserGpuUnderlayForFade", ground, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredTerrainUnpark_PreservesReadyQueueOwnership()
    {
        var ground = LoadSource(
            ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.GroundTerrain.cs");

        var releaseStart = ground.IndexOf(
            "private void ReleaseDeferredTerrainMarks()",
            StringComparison.Ordinal);
        var releaseEnd = ground.IndexOf(
            "private bool UpdateTerrainPoolPressureLatch()",
            releaseStart,
            StringComparison.Ordinal);
        Assert.True(releaseStart >= 0 && releaseEnd > releaseStart);
        Assert.DoesNotContain(
            "_terrainStreamer.NotifyUnloaded(",
            ground[releaseStart..releaseEnd],
            StringComparison.Ordinal);

        var unparkStart = ground.IndexOf(
            "private void UnparkDeferredInsideScheduleWindow()",
            StringComparison.Ordinal);
        var unparkEnd = ground.IndexOf(
            "private void EmitTerrainPoolLimitDiagnostic(",
            unparkStart,
            StringComparison.Ordinal);
        Assert.True(unparkStart >= 0 && unparkEnd > unparkStart);
        Assert.DoesNotContain(
            "_terrainStreamer.NotifyUnloaded(",
            ground[unparkStart..unparkEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "_terrainDeferredChunks.Remove(key);",
            ground[unparkStart..unparkEnd],
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerrainDrawSubmission_RotatesIndirectStorageAndSkipsShadowFadeProofs()
    {
        var ground = LoadSource(
            ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.GroundTerrain.cs");

        Assert.Contains("TerrainIndirectCommandRingSize = 16", ground, StringComparison.Ordinal);
        Assert.Contains("AcquireTerrainIndirectCommandBuffer()", ground, StringComparison.Ordinal);
        Assert.Contains("resolveTransitions: !shadowPass", ground, StringComparison.Ordinal);
        Assert.Contains(
            "var desired = resolveTransitions ? _terrainStreamer?.SnapshotDesired() : null;",
            ground,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScenePass_StreamingSafetyUnderlayIsStartupOnlyTwoSidedWithoutPom()
    {
        var bootstrap = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Bootstrap.cs");
        var scenePass = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassScene.cs");

        Assert.Contains("PreviewMeshFactory.CreatePreviewGroundPlane(", bootstrap, StringComparison.Ordinal);
        Assert.Contains("worldY: PreviewStageConstants.GroundPlaneWorldY - 0.015f", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("HasTerrainCameraChunk(frame.Eye)", scenePass, StringComparison.Ordinal);
        Assert.Contains("safetyUnderlay=startup-only", LoadSource(
            ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.GroundTerrain.cs"), StringComparison.Ordinal);

        var fallbackStart = scenePass.IndexOf(
            "if (!HasTerrainChunksToDraw && _groundMesh is { IndexCount: > 0 })",
            StringComparison.Ordinal);
        Assert.True(fallbackStart >= 0);
        var fallbackEnd = scenePass.IndexOf(
            "TryEnsureGroundTextureArrays(frame.Gl);",
            fallbackStart,
            StringComparison.Ordinal);
        Assert.True(fallbackEnd > fallbackStart);
        var fallbackBlock = scenePass[fallbackStart..fallbackEnd];
        var disableCull = fallbackBlock.IndexOf(
            "frame.Gl.Disable(EnableCap.CullFace);",
            StringComparison.Ordinal);
        var draw = fallbackBlock.IndexOf(
            "_groundMesh.Draw(_mainProgramUsesTessellation);",
            StringComparison.Ordinal);
        var restoreCull = fallbackBlock.IndexOf(
            "frame.Gl.Enable(EnableCap.CullFace);",
            draw,
            StringComparison.Ordinal);
        Assert.True(disableCull >= 0);
        Assert.True(draw > disableCull);
        Assert.True(restoreCull > draw);
        Assert.Contains("SetIntLoc(u.EnableParallax, 0);", fallbackBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void GenesisProgram_IdleFramesSkipTessellationProgram()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.GenesisProgram.cs");

        Assert.Contains("frame.EnableTessellationDisplacementEff &&", source, StringComparison.Ordinal);
        Assert.Contains("frame.Settings.DrawPreviewSubject;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NeedsContinuousRendering_KeepsFramesWhileTerrainStreamingCatchUp()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.cs");

        Assert.Contains("private bool _terrainStreamingNeedsFrames = true;", source, StringComparison.Ordinal);
        Assert.Contains(
            "(!_settings.DrawPreviewSubject || _terrainStreamingNeedsFrames)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWglContinuousFrames_ReserveCompositorTime()
    {
        Assert.Equal(
            0,
            PreviewNativeWglPresenter.ResolveContinuousFrameYieldMilliseconds(
                TimeSpan.FromMilliseconds(10)));
        Assert.Equal(
            4,
            PreviewNativeWglPresenter.ResolveContinuousFrameYieldMilliseconds(
                TimeSpan.FromMilliseconds(25)));
        Assert.Equal(
            8,
            PreviewNativeWglPresenter.ResolveContinuousFrameYieldMilliseconds(
                TimeSpan.FromMilliseconds(50)));
        Assert.Equal(
            16,
            PreviewNativeWglPresenter.ResolveContinuousFrameYieldMilliseconds(
                TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void PostCoreTerrainWarmup_StartsCloudsAfterFirstTerrainButSkipsDependentPostPasses()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.cs");

        Assert.Contains("ResolveStartupFrameSettings(settings)", source, StringComparison.Ordinal);
        Assert.Contains("ResolveTerrainInitProgressFraction() >= 1.0", source, StringComparison.Ordinal);
        Assert.Contains("_terrainStartupReadyLatched = true", source, StringComparison.Ordinal);
        Assert.Contains(
            "EnableVolumetricClouds = settings.EnableVolumetricClouds && HasTerrainChunksToDraw",
            source,
            StringComparison.Ordinal);
        Assert.Contains("EnablePreviewTaa = false", source, StringComparison.Ordinal);
        Assert.Contains("EnableScreenSpaceAo = false", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PostCoreTerrainWarmup_DoesNotReenterAfterCameraMovement()
    {
        var initSource = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Init.cs");
        var terrainSource = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.GroundTerrain.cs");

        Assert.Contains("var terrainFrac = _terrainStartupReadyLatched", initSource, StringComparison.Ordinal);
        Assert.Contains("!_terrainStartupReadyLatched &&", initSource, StringComparison.Ordinal);
        Assert.Contains("!_terrainStartupReadyLatched;", terrainSource, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWglBackendFrameRequests_BypassAvaloniaDispatcher()
    {
        var backend = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.cs");
        var requestStart = backend.IndexOf("private void RequestPreviewFrame()", StringComparison.Ordinal);
        var requestEnd = backend.IndexOf("public void Resize(", requestStart, StringComparison.Ordinal);
        Assert.True(requestStart >= 0);
        Assert.True(requestEnd > requestStart);
        var request = backend[requestStart..requestEnd];
        Assert.Contains("if (nativeWglActive)", request, StringComparison.Ordinal);
        Assert.True(
            request.IndexOf("request();", StringComparison.Ordinal) <
            request.IndexOf("Dispatcher.UIThread.Post", StringComparison.Ordinal));

        var control = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Controls",
            "GlPbrPreviewControl.cs");
        Assert.Contains(
            "_backend.SetRequestPreviewFrame(RequestPreviewFrameFromBackend);",
            control,
            StringComparison.Ordinal);
        Assert.Contains("if (Dispatcher.UIThread.CheckAccess())", control, StringComparison.Ordinal);
    }

    [Fact]
    public void TerrainInitProgress_CountsOnlyCameraLocalFullChunks()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Init.cs");

        Assert.Contains("var cameraChunk = _terrainStreamer.CameraChunk;", source, StringComparison.Ordinal);
        Assert.Contains("key.ChebyshevDistanceToChunk(cameraChunk) <= near", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GlRender_AllowsFrameWhenGroundTexturesMissingButDirty()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.cs");

        Assert.Contains("!_grassGroundMaterialDirty", source, StringComparison.Ordinal);
        Assert.Contains(
            "Ground textures missing and nothing queued to re-upload",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResourcePackPreviewRenderer_SkipsEmulatedRebakeForErrorPlaceholder()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.Preview",
            "ResourcePackPreviewRenderer.cs");

        Assert.Contains("useEmulatedEntityPipeline", source, StringComparison.Ordinal);
        Assert.Contains(
            "meshProvenance.Kind != PreviewMeshDriverKind.ErrorPlaceholder",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "meshProvenance.Kind == PreviewMeshDriverKind.ErrorPlaceholder",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UploadGroundMaterial_MarksGrassGroundReady()
    {
        var lifecycle = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Lifecycle.cs");

        var uploadStart = lifecycle.IndexOf("private void UploadGroundMaterial(", StringComparison.Ordinal);
        Assert.True(uploadStart >= 0);
        var uploadEnd = lifecycle.IndexOf("private void UploadMaterial(", uploadStart, StringComparison.Ordinal);
        Assert.True(uploadEnd > uploadStart);
        var upload = lifecycle[uploadStart..uploadEnd];
        Assert.Contains("_grassGroundReady = _grassGroundAlbedo is not null;", upload, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterGlPreview_SchedulesGroundTextureRefreshAfterIdleScenePush()
    {
        var viewModel = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "ViewModels",
            "MainWindowViewModel.Preview.cs");

        var registerStart = viewModel.IndexOf("internal void RegisterGlPreview(", StringComparison.Ordinal);
        Assert.True(registerStart >= 0);
        var registerEnd = viewModel.IndexOf("private void OnPreviewGpuInitProgressChanged", registerStart, StringComparison.Ordinal);
        Assert.True(registerEnd > registerStart);
        var register = viewModel[registerStart..registerEnd];
        Assert.Contains("Apply3DPreviewIfNeeded();", register, StringComparison.Ordinal);
        Assert.Contains("SchedulePreviewGroundTextureRefresh();", register, StringComparison.Ordinal);
        Assert.True(
            register.IndexOf("Apply3DPreviewIfNeeded();", StringComparison.Ordinal) <
            register.IndexOf("SchedulePreviewGroundTextureRefresh();", StringComparison.Ordinal));
    }

    [Fact]
    public void OnPreviewGpuInitProgressChanged_RepushesSceneOnlyOnReadyTransitions()
    {
        var viewModel = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "ViewModels",
            "MainWindowViewModel.Preview.cs");

        var handlerStart = viewModel.IndexOf(
            "private void OnPreviewGpuInitProgressChanged(PreviewGpuInitProgress progress)",
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0);
        var handlerEnd = viewModel.IndexOf("private void ApplyPreviewGpuInitOverlay(", handlerStart, StringComparison.Ordinal);
        Assert.True(handlerEnd > handlerStart);
        var handler = viewModel[handlerStart..handlerEnd];
        Assert.Contains("coreBecameReady", handler, StringComparison.Ordinal);
        Assert.Contains("fullyBecameReady", handler, StringComparison.Ordinal);
        Assert.Contains("(coreBecameReady || fullyBecameReady) && IsPreview3D", handler, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if ((progress.CoreReady || progress.IsFullyReady) && IsPreview3D)",
            handler,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RaiseGpuInitProgress_SkipsUnchangedReadinessNotifications()
    {
        var init = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Init.cs");

        var raiseStart = init.IndexOf("private void RaiseGpuInitProgress(", StringComparison.Ordinal);
        Assert.True(raiseStart >= 0);
        var raiseEnd = init.IndexOf("private double ComputeTierProgressFraction(", raiseStart, StringComparison.Ordinal);
        Assert.True(raiseEnd > raiseStart);
        var raise = init[raiseStart..raiseEnd];
        Assert.Contains("var changed =", raise, StringComparison.Ordinal);
        Assert.Contains("if (changed)", raise, StringComparison.Ordinal);
        Assert.Contains("GpuInitProgressChanged?.Invoke(progress);", raise, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenePass_EntityParallaxCanBeDisabledPerBatchWithoutAffectingBlocks()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassScene.cs");

        Assert.Contains("var batchAllowsParallax = !frame.EntityEmulatedPreview || batch.EnableParallax;", source, StringComparison.Ordinal);
        Assert.Contains("var batchParallax = frame.EnableParallaxEff && batchAllowsParallax && bHasH;", source, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.EnableParallax, batchParallax ? 1 : 0);", source, StringComparison.Ordinal);
        Assert.Contains("UploadMaterial(frame.Gl, slot, nearest: true);", source, StringComparison.Ordinal);
        Assert.Contains("SetFloatLoc(u.ParallaxUvScale, 1f);", source, StringComparison.Ordinal);
        Assert.Contains("? EntityTextureAtlasScale(slot)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EntityParallaxUvScale", source, StringComparison.Ordinal);
        Assert.DoesNotContain("16f / atlasMax", source, StringComparison.Ordinal);
        Assert.Contains("SetIntLoc(u.EnableTessellationDisplacement,", source, StringComparison.Ordinal);
        Assert.Contains("batchAllowsParallax &&", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupPass_ComputesEffectiveShaderFlagsBeforeSceneProgramSelection()
    {
        var setup = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassSetup.cs");
        var shadow = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassShadow.cs");
        var render = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.cs");
        var scene = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassScene.cs");

        Assert.Contains("ApplyEffectiveFrameRenderFlags(ref frame);", setup, StringComparison.Ordinal);
        Assert.Contains("frame.EnableTessellationDisplacementEff = PreviewEntityEmulatedShaderGating.EffectiveTessellationDisplacement", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("EffectiveTessellationDisplacement", shadow, StringComparison.Ordinal);
        Assert.True(
            render.IndexOf("GlRenderPassSetup(ref frame);", StringComparison.Ordinal) <
            render.IndexOf("GlRenderPassScene(ref frame);", StringComparison.Ordinal));
        Assert.True(
            scene.IndexOf("EnsureGenesisProgramForFrame(ref frame);", StringComparison.Ordinal) <
            scene.IndexOf("SyncGodRayToggleState", StringComparison.Ordinal));
    }

    [Fact]
    public void Lifecycle_DisposesRoadmapGpuBuffersOnFullTeardown()
    {
        var lifecycle = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Lifecycle.cs");

        Assert.Contains("DisposeMaterialTextureArrays();", lifecycle, StringComparison.Ordinal);
        Assert.Contains("DisposeGpuTimerProfiler();", lifecycle, StringComparison.Ordinal);
        Assert.Contains("DestroyImageHistogramResources();", lifecycle, StringComparison.Ordinal);
        Assert.Contains("DisposeGenesisMaterialDrawRecordBuffer();", lifecycle, StringComparison.Ordinal);
        Assert.Contains("DisposeGenesisIndirectDrawCommands();", lifecycle, StringComparison.Ordinal);
        Assert.Contains("AbandonMaterialTextureArrays();", lifecycle, StringComparison.Ordinal);
        Assert.Contains("AbandonGpuTimerProfiler();", lifecycle, StringComparison.Ordinal);
        Assert.Contains("AbandonImageHistogramResources();", lifecycle, StringComparison.Ordinal);
        Assert.Contains("AbandonGenesisIndirectDrawCommands();", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionJitter_ShiftsClipSpaceBySubpixelNdc()
    {
        var projection = PreviewGlMatrices.CreatePerspectiveFieldOfViewOpenGl(
            60f * (MathF.PI / 180f),
            16f / 9f,
            0.1f,
            100f);
        var jittered = PreviewGlMatrices.ApplyProjectionJitter(projection, new Vector2(0.002f, -0.003f));
        var viewPos = new Vector4(0f, 0f, -5f, 1f);
        var baseClip = Vector4.Transform(viewPos, Matrix4x4.Transpose(projection));
        var jitteredClip = Vector4.Transform(viewPos, Matrix4x4.Transpose(jittered));

        Assert.Equal(baseClip.W, jitteredClip.W, 0.0001f);
        Assert.Equal(baseClip.X + 0.002f * baseClip.W, jitteredClip.X, 0.0001f);
        Assert.Equal(baseClip.Y - 0.003f * baseClip.W, jitteredClip.Y, 0.0001f);
    }

    [Fact]
    public void ScenePass_AppliesProjectionJitterOnlyWhenPreviewTaaActive()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassScene.cs");

        Assert.Contains("SyncPreviewTaaToggleState(frame.Settings);", source, StringComparison.Ordinal);
        Assert.Contains("if (IsPreviewTaaActive(frame.Settings))", source, StringComparison.Ordinal);
        Assert.Contains("PreviewGlMatrices.ApplyProjectionJitter", source, StringComparison.Ordinal);
        Assert.Contains("CurrentPreviewTaaJitter(jitterW, jitterH, frame.Settings)", source, StringComparison.Ordinal);
        Assert.Contains("frame.GodRayCaptureActive && frame.SceneCaptureW > 0 ? frame.SceneCaptureW : frame.Vw", source, StringComparison.Ordinal);
        Assert.Contains("frame.Proj = frame.UnjitteredProj;", source, StringComparison.Ordinal);
        Assert.Contains("frame.PreviewTaaJitterNdc", source, StringComparison.Ordinal);
        Assert.Contains("SetMatrixLoc(u.TaaCurrViewProj, taaCurrentViewProj);", source, StringComparison.Ordinal);
        Assert.Contains("ResolvePreviewTaaPrevViewProj(taaCurrentViewProj)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewTaa_JitteredModesKeepRenderingToAccumulateSamples()
    {
        var backend = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.cs");
        var taa = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Taa.cs");
        Assert.Contains("ShouldContinuouslyAccumulatePreviewTaa(_settings)", backend, StringComparison.Ordinal);
        Assert.Contains("private bool ShouldContinuouslyAccumulatePreviewTaa", taa, StringComparison.Ordinal);
        Assert.Contains("if (!IsPreviewTaaActive(settings))", taa, StringComparison.Ordinal);
        Assert.Contains("taa.TemporalWeight > 0f && taa.JitterScale > 0f", taa, StringComparison.Ordinal);
    }

    [Fact]
    public void PostPass_AppliesPreviewTaaAfterFullSceneComposite()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassPost.cs");

        var godRays = source.IndexOf("DrawGodRayComposite(ref frame)", StringComparison.Ordinal);
        var clouds = source.IndexOf("CompositeCloudRenderTargetToDefault(ref frame)", StringComparison.Ordinal);
        var deferredRays = source.IndexOf("CompositePendingGodRays(ref frame)", StringComparison.Ordinal);
        var axes = source.IndexOf("DrawCornerAxes(", StringComparison.Ordinal);
        var taa = source.IndexOf("DrawPreviewTaa(ref frame);", StringComparison.Ordinal);
        var fingerprint = source.IndexOf("MaybeLogPreviewFingerprint(ref frame);", StringComparison.Ordinal);

        Assert.True(godRays >= 0);
        Assert.True(clouds >= 0);
        Assert.True(deferredRays > clouds);
        Assert.True(axes >= 0);
        Assert.True(taa > godRays);
        Assert.True(taa > clouds);
        Assert.True(taa > deferredRays);
        Assert.True(taa > axes);
        Assert.True(fingerprint > taa);
    }

    [Fact]
    public void FroxelCloudDensity_UsesDetailedSignalOrDisabledFallback()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Volume.cs");

        Assert.Contains("if (!settings.EnableVolumetricClouds)", source, StringComparison.Ordinal);
        Assert.Contains("ResolveSharedCloudTransmittanceTarget(settings) is not null ? 0f : settings.CloudDensity",
            source, StringComparison.Ordinal);
        Assert.Contains("BindSharedCloudTransmittance(frame, _volumeIntegrateProgram, iu)",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorRenderTarget_DefaultFramebufferCopyReadsBackBuffer()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "GlColorRenderTarget.cs");
        var readback = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "GlFramebufferReadback.cs");

        Assert.Contains(
            "gl.ReadBuffer(readFramebuffer == 0 ? ReadBufferMode.Back : ReadBufferMode.ColorAttachment0);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("public byte[]? TryReadRgb8", source, StringComparison.Ordinal);
        Assert.Contains("out GLEnum error", source, StringComparison.Ordinal);
        Assert.Contains("GlFramebufferReadback.TryReadRgb8", source, StringComparison.Ordinal);
        Assert.Contains("DrainErrors(gl);", readback, StringComparison.Ordinal);
        Assert.Contains("GLEnum.Rgba", readback, StringComparison.Ordinal);
        Assert.Contains("var rgb = new byte[width * height * 3];", readback, StringComparison.Ordinal);
    }

    [Fact]
    public void SceneCapture_ProvidesTaaSignalAttachment()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "GlSceneCaptureTarget.cs");

        Assert.Contains("TaaSignalTextureHandle", source, StringComparison.Ordinal);
        Assert.Contains("FramebufferAttachment.ColorAttachment1", source, StringComparison.Ordinal);
        Assert.Contains("DrawBufferMode.ColorAttachment1", source, StringComparison.Ordinal);
        Assert.Contains("InternalFormat.Rgba8", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewTaa_BindsSceneCaptureSignalWhenAvailable()
    {
        var source = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Taa.cs");

        Assert.Contains("var hasTaaSignal", source, StringComparison.Ordinal);
        Assert.Contains("TextureUnit.Texture3", source, StringComparison.Ordinal);
        Assert.Contains("TaaSignalTextureHandle", source, StringComparison.Ordinal);
        Assert.Contains("SetIntOnProgramLoc(_taaResolveProgram, tu.HasTaaSignal, hasTaaSignal ? 1 : 0);", source, StringComparison.Ordinal);
        Assert.Contains("SetVec2OnProgramLoc(_taaResolveProgram, tu.CaptureTexelSize, captureTexelSize);", source, StringComparison.Ordinal);
        Assert.Contains("frame.SceneCaptureW > 0 ? frame.SceneCaptureW : w", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewTaa_ModeDropdownIsWiredToSettingsAndResolveUniforms()
    {
        var view = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Controls",
            "ShaderPreviewTab.axaml");
        var viewModel = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "ViewModels",
            "MainWindowViewModel.Preview.cs");
        var settings = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "Abstractions",
            "PreviewRenderSettings.cs");
        var settingsSnapshot = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "Abstractions",
            "PreviewRenderSettingsSnapshot.cs");
        var userSettings = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Models",
            "UserSettings.cs");
        var synchronizer = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Services",
            "UserSettingsSynchronizer.cs");
        var render = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.cs");
        var godRays = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.GodRays.cs");
        var taa = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Taa.cs");
        var colorTarget = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "GlColorRenderTarget.cs");
        var previewControl = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Controls",
            "GlPbrPreviewControl.cs");
        var sceneCapture = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "GlSceneCaptureTarget.cs");
        var shaderCache = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "GlslPreparedSourceCache.cs");

        var postPassSettings = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.PostPassSettings.cs");

        Assert.Contains("Preview3DTaaModeOptions", view, StringComparison.Ordinal);
        Assert.Contains("FlyoutSection Header=\"{Binding Strings.Preview3DTaaSection}\"", view, StringComparison.Ordinal);
        Assert.Contains("SelectedIndex=\"{Binding Preview3DTaaMode, Mode=TwoWay}\"", view, StringComparison.Ordinal);
        var shaders = view.IndexOf("Preview3DShadersSection", StringComparison.Ordinal);
        var taaSection = view.IndexOf("Preview3DTaaSection", StringComparison.Ordinal);
        var taaToggle = view.IndexOf("IsChecked=\"{Binding Preview3DEnablePreviewTaa, Mode=TwoWay}\"", StringComparison.Ordinal);
        var pomToggle = view.IndexOf("IsChecked=\"{Binding Preview3DEnableParallax, Mode=TwoWay}\"", StringComparison.Ordinal);
        Assert.True(shaders >= 0);
        Assert.True(taaSection > shaders);
        Assert.True(taaToggle > shaders && taaToggle < taaSection);
        Assert.True(pomToggle > shaders && pomToggle < taaSection);
        Assert.True(
            taaSection <
            view.IndexOf("SelectedIndex=\"{Binding Preview3DTaaMode, Mode=TwoWay}\"", StringComparison.Ordinal));
        Assert.Contains("[ObservableProperty] private int _preview3DTaaMode;", viewModel, StringComparison.Ordinal);
        Assert.Contains("[ObservableProperty] private bool _preview3DTaaForceFxaa;", viewModel, StringComparison.Ordinal);
        Assert.True(
            viewModel.IndexOf("Preview3DTaaModeLessJitter", StringComparison.Ordinal) <
            viewModel.IndexOf("Preview3DTaaModeBalanced", StringComparison.Ordinal));
        Assert.Contains("OnDebouncedPreviewTaaGpuSettingChanged", viewModel, StringComparison.Ordinal);
        Assert.Contains("ScheduleDebouncedPreviewTaaGpuRefresh", viewModel, StringComparison.Ordinal);
        Assert.Contains("PreviewTaaGpuDebounceMs", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("OnPreview3DTaaFxaaStrengthScaleChanged(double value) => OnPreview3DGpuSettingChanged", viewModel, StringComparison.Ordinal);
        Assert.Contains("PreviewTaaMode = Math.Clamp(Preview3DTaaMode, 0, 4)", viewModel, StringComparison.Ordinal);
        Assert.Contains("PreviewTaaFxaaStrengthScale = (float)Math.Clamp(Preview3DTaaFxaaStrengthScale, 0.0, 5.0)", viewModel, StringComparison.Ordinal);
        Assert.Contains("PreviewTaaForceFxaa = Preview3DTaaForceFxaa", viewModel, StringComparison.Ordinal);
        Assert.Contains("public int PreviewTaaMode { get; init; }", settings, StringComparison.Ordinal);
        Assert.Contains("0 = less jitter", settings, StringComparison.Ordinal);
        Assert.Contains("public float PreviewTaaFxaaStrengthScale { get; init; } = 1f;", settings, StringComparison.Ordinal);
        Assert.Contains("public bool PreviewTaaForceFxaa { get; init; }", settings, StringComparison.Ordinal);
        Assert.Contains("public double Preview3DTaaFxaaStrengthScale { get; set; } = 1.0;", userSettings, StringComparison.Ordinal);
        Assert.Contains("public bool Preview3DTaaForceFxaa { get; set; }", userSettings, StringComparison.Ordinal);
        Assert.Contains("public int? Preview3DTaaMode { get; set; }", userSettings, StringComparison.Ordinal);
        Assert.Contains("ResolvePreview3DTaaMode(settings)", synchronizer, StringComparison.Ordinal);
        Assert.Contains("DefaultPreview3DTaaMode = 0", synchronizer, StringComparison.Ordinal);
        Assert.Contains("settings.Preview3DTaaFxaaStrengthScale = Math.Clamp(vm.Preview3DTaaFxaaStrengthScale, 0.0, 5.0);", synchronizer, StringComparison.Ordinal);
        Assert.Contains("PreviewTaaFxaaStrengthScale = s.PreviewTaaFxaaStrengthScale", settingsSnapshot, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(settings.PreviewTaaFxaaStrengthScale, 0f, 5f)", taa, StringComparison.Ordinal);
        Assert.Contains("ConfigureDefaultFramebufferColorOutput(gl, defaultFbo);", render, StringComparison.Ordinal);
        Assert.Contains("private void ConfigureDefaultFramebufferColorOutput", godRays, StringComparison.Ordinal);
        Assert.Contains("DrawBufferMode.Back", godRays, StringComparison.Ordinal);
        Assert.Contains("DrawBufferMode.ColorAttachment0", godRays, StringComparison.Ordinal);
        Assert.Contains("ResolveEffectivePreviewTaa", taa, StringComparison.Ordinal);
        Assert.Contains("ResolvePreviewTaa(settings.VolumetricQuality, settings.PreviewTaaMode)", taa, StringComparison.Ordinal);
        Assert.Contains("SetFloatOnProgramLoc(_taaResolveProgram, tu.StableTemporalBoost, taa.StableTemporalBoost);", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("SetFloatOnProgramLoc(_taaResolveProgram, tu.TaaSharpenStrength, taa.SharpenStrength);", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("SetFloatOnProgramLoc(_taaResolveProgram, tu.DepthEdgeHistoryFloor, taa.DepthEdgeHistoryFloor);", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("SetFloatOnProgramLoc(_taaResolveProgram, tu.EdgeAaBlend, taa.EdgeAaBlend);", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("SetVec2OnProgramLoc(_taaResolveProgram, tu.CurrentJitterPixels,", taa, StringComparison.Ordinal);
        Assert.Contains("SetFloatOnProgramLoc(_taaResolveProgram, tu.SourceFilterStrength, taa.SourceFilterStrength);", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("SetFloatOnProgramLoc(_taaResolveProgram, tu.SilhouetteHistoryWeight, taa.SilhouetteHistoryWeight);", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("SetFloatOnProgramLoc(_taaResolveProgram, tu.FxaaEdgeStrength, taa.FxaaEdgeStrength);", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("SetFloatOnProgramLoc(_taaResolveProgram, tu.FxaaLumaEdgeStrength,", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("SetFloatOnProgramLoc(_taaResolveProgram, tu.FxaaLumaThreshold,", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("SetIntOnProgramLoc(_taaResolveProgram, tu.ForceFxaa, settings.PreviewTaaForceFxaa ? 1 : 0);", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("ComputePreviewTaaSettingsKey", taa, StringComparison.Ordinal);
        Assert.Contains("gl.Disable(EnableCap.CullFace);", taa, StringComparison.Ordinal);
        Assert.Contains("gl.Disable(EnableCap.ScissorTest);", taa, StringComparison.Ordinal);
        Assert.Contains("gl.ColorMask(true, true, true, true);", taa, StringComparison.Ordinal);
        Assert.Contains("TAA resolve draw GL error", taa, StringComparison.Ordinal);
        Assert.Contains("_taaResolveTarget", taa, StringComparison.Ordinal);
        Assert.Contains("var resolveTarget = _taaResolveTarget!;", taa, StringComparison.Ordinal);
        Assert.Contains("resolveTarget.EnsureSize(w, h, useFloat)", taa, StringComparison.Ordinal);
        Assert.Contains("resolveTarget.BindDraw();", taa, StringComparison.Ordinal);
        Assert.Contains("TryPresentPreviewTaaResolveToDefault", taa, StringComparison.Ordinal);
        Assert.Contains("encodeHdr: false", taa, StringComparison.Ordinal);
        Assert.Contains("bool encodeHdr = true)", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("encodeHdr && settings.HdrPresentActive", postPassSettings, StringComparison.Ordinal);
        Assert.Contains("scratchTarget.CopyColorFromFramebuffer(readFbo, w, h, frame.VpX, frame.VpY)", taa, StringComparison.Ordinal);
        Assert.Contains("resolveTarget.BlitColorToFramebuffer(readFbo, frame.VpX, frame.VpY, w, h)", taa, StringComparison.Ordinal);
        Assert.Contains("historyTarget.CopyColorFrom(resolveTarget)", taa, StringComparison.Ordinal);
        Assert.Contains("MaybeLogPreviewTaaDiagnostics", taa, StringComparison.Ordinal);
        Assert.Contains("if (!frame.Settings.LogPreviewTaaDiagnostics)", taa, StringComparison.Ordinal);
        Assert.Contains("[3D preview] TAA resolve: view=", taa, StringComparison.Ordinal);
        Assert.Contains("sceneCapture={sceneCaptureSize}", taa, StringComparison.Ordinal);
        Assert.Contains("resolveSize={resolveSize}", taa, StringComparison.Ordinal);
        Assert.Contains("jitterPx=", taa, StringComparison.Ordinal);
        Assert.Contains("fxaaLuma=", taa, StringComparison.Ordinal);
        Assert.Contains("fxaaThreshold=", taa, StringComparison.Ordinal);
        Assert.Contains("forceFxaa=", taa, StringComparison.Ordinal);
        Assert.Contains("EmitPreviewTaaShaderDiagnostic", taa, StringComparison.Ordinal);
        Assert.Contains("Preview TAA shader ready: resolveSource=", taa, StringComparison.Ordinal);
        Assert.Contains("MaybeLogPreviewTaaReadbackDiagnostics", taa, StringComparison.Ordinal);
        Assert.Contains("LogPreviewTaaDiagnostics = Preview3DLogVerbosePreviewDiagnostics", viewModel, StringComparison.Ordinal);
        Assert.Contains("[3D preview] TAA readback:", taa, StringComparison.Ordinal);
        Assert.Contains("scratch={PixelHashText", taa, StringComparison.Ordinal);
        Assert.Contains("read-failed({ReadbackErrorText(error)})", taa, StringComparison.Ordinal);
        Assert.Contains("resolveDelta={DeltaText(resolveDelta)}", taa, StringComparison.Ordinal);
        Assert.Contains("presentDelta={DeltaText(presentDelta)}", taa, StringComparison.Ordinal);
        Assert.Contains("rawPresentedDelta={DeltaText(rawPresentedDelta)}", taa, StringComparison.Ordinal);
        Assert.Contains("resolveMax={DeltaText(resolveMaxDelta)}", taa, StringComparison.Ordinal);
        Assert.Contains("resolveChanged={PercentText(resolveChangedPct)}", taa, StringComparison.Ordinal);
        Assert.Contains("presentMax={DeltaText(presentMaxDelta)}", taa, StringComparison.Ordinal);
        Assert.Contains("MaxAbsRgbDelta", taa, StringComparison.Ordinal);
        Assert.Contains("ChangedPixelPercent", taa, StringComparison.Ordinal);
        Assert.Contains("BlitColorToFramebuffer", colorTarget, StringComparison.Ordinal);
        Assert.Contains("TryReadRgb8", colorTarget, StringComparison.Ordinal);
        Assert.Contains("ConfigureDrawFramebufferColorAttachment(drawFramebuffer)", colorTarget, StringComparison.Ordinal);
        Assert.Contains("public int Width => _width;", sceneCapture, StringComparison.Ordinal);
        Assert.Contains("public int Height => _height;", sceneCapture, StringComparison.Ordinal);
        Assert.Contains("ComputePreparedSourceFingerprint", shaderCache, StringComparison.Ordinal);
        Assert.Contains("GetShaderSourceOrigin", shaderCache, StringComparison.Ordinal);
        Assert.Contains("TryFindSourceShaderPath", shaderCache, StringComparison.Ordinal);
        Assert.Contains("File.ReadAllText(sourcePath)", shaderCache, StringComparison.Ordinal);
        Assert.Contains("TryGetPreviewViewportInfo", previewControl, StringComparison.Ordinal);
        Assert.Contains("Viewport: {pixelWidth}x{pixelHeight}px", viewModel, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding Preview3DTaaFxaaStrengthScale, Mode=TwoWay}\"", view, StringComparison.Ordinal);
        Assert.Contains("Maximum=\"5.00\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked=\"{Binding Preview3DTaaForceFxaa, Mode=TwoWay}\"", view, StringComparison.Ordinal);
        Assert.Equal("TXAA", LocalizedStrings.Preview3DTaaSection);
    }

    [Fact]
    public void RenderSettingsPreviewTaaDefaultsToLessJitter()
    {
        var settings = new PreviewRenderSettings();
        Assert.Equal(0, settings.PreviewTaaMode);

        var profile = PreviewVolumetricQuality.ResolvePreviewTaa(1, settings.PreviewTaaMode);
        Assert.Equal(0.82f, profile.TemporalWeight, 0.0001f);
        Assert.Equal(0.52f, profile.JitterScale, 0.0001f);
    }

    [Fact]
    public void CinematicVolumetricQuality_IsWiredThroughPersistenceUiAndDiagnostics()
    {
        var viewModel = LoadSource(ThisFilePath(),
            "src", "AutoPBR.App", "ViewModels", "MainWindowViewModel.Preview.cs");
        var synchronizer = LoadSource(ThisFilePath(),
            "src", "AutoPBR.App", "Services", "UserSettingsSynchronizer.cs");
        var clouds = LoadSource(ThisFilePath(),
            "src", "AutoPBR.App", "Rendering", "OpenGL", "OpenGlPreviewBackend.VolumetricClouds.cs");

        Assert.Contains("LocalizedStrings.Preview3DVolumetricQualityCinematic", viewModel, StringComparison.Ordinal);
        Assert.Contains("VolumetricQuality = PreviewVolumetricQuality.Clamp(Preview3DVolumetricQuality)", viewModel,
            StringComparison.Ordinal);
        Assert.Contains("PreviewVolumetricQuality.Clamp(settings.Preview3DVolumetricQuality)", synchronizer,
            StringComparison.Ordinal);
        Assert.Contains("PreviewVolumetricQuality.Clamp(vm.Preview3DVolumetricQuality)", synchronizer,
            StringComparison.Ordinal);
        Assert.Contains("volumetricPreset={PreviewVolumetricQuality.GetName", clouds, StringComparison.Ordinal);
        Assert.Equal("Cinematic", LocalizedStrings.Preview3DVolumetricQualityCinematic);
    }

    [Fact]
    public void PreviewTaa_EdgeModesSupersampleSceneCaptureBeforeResolve()
    {
        var frame = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "GlRenderFrame.cs");
        var godRays = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.GodRays.cs");
        var godRaysCoordinator = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "GodRaysPassCoordinator.cs");
        var scenePass = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassScene.cs");
        var taa = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Taa.cs");

        Assert.Contains("public int SceneCaptureW;", frame, StringComparison.Ordinal);
        Assert.Contains("public int SceneCaptureH;", frame, StringComparison.Ordinal);
        Assert.Contains("public float SceneCaptureScale;", frame, StringComparison.Ordinal);
        Assert.Contains("PreviewTaaSsaaMaxDimension", godRaysCoordinator, StringComparison.Ordinal);
        Assert.Contains("ResolveSceneCaptureScale", godRaysCoordinator, StringComparison.Ordinal);
        Assert.Contains("AlignEvenCaptureDimension", godRaysCoordinator, StringComparison.Ordinal);
        Assert.Contains("hard horizontal lighting split", godRaysCoordinator, StringComparison.Ordinal);
        Assert.Contains("ResolveSceneCaptureSize(ref frame, out var captureW, out var captureH, out var captureScale)", godRays, StringComparison.Ordinal);
        var sceneCapture = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "GlSceneCaptureTarget.cs");
        Assert.Contains("vpX, vpY, vpX + destW, vpY + destH", sceneCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("0, destH, destW, 0", sceneCapture, StringComparison.Ordinal);
        var ao = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.ScreenSpaceAo.cs");
        Assert.Contains("gl.ClearColor(1f, 1f, 1f, 1f);", ao, StringComparison.Ordinal);
        Assert.Contains("requireViewNormals: requireNormals", godRays, StringComparison.Ordinal);
        Assert.Contains("_sceneCapture.EnsureSize(", godRays, StringComparison.Ordinal);
        Assert.Contains("_sceneCapture.BindDraw(captureW, captureH)", godRays, StringComparison.Ordinal);
        Assert.Contains("Scene capture AA scale:", godRaysCoordinator, StringComparison.Ordinal);
        Assert.Contains("LogPreviewTaaDiagnostics", godRaysCoordinator, StringComparison.Ordinal);
        Assert.Contains("var sceneVpW = frame.GodRayCaptureActive && frame.SceneCaptureW > 0 ? frame.SceneCaptureW : frame.Vw;", scenePass, StringComparison.Ordinal);
        Assert.Contains("var sceneVpH = frame.GodRayCaptureActive && frame.SceneCaptureH > 0 ? frame.SceneCaptureH : frame.Vh;", scenePass, StringComparison.Ordinal);
        Assert.Contains("frame.Gl.Viewport(sceneVpX, sceneVpY, (uint)sceneVpW, (uint)sceneVpH);", scenePass, StringComparison.Ordinal);
        Assert.Contains("captureScale={frame.SceneCaptureScale:0.##}", taa, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewTaa_CapturesPreviousEntityBonePaletteForSkinnedMotion()
    {
        var backend = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.cs");
        var lifecycle = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Lifecycle.cs");
        var setup = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassSetup.cs");
        var taa = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Taa.cs");

        Assert.Contains("EntityPrevSkinningUboBindingPoint = 3", backend, StringComparison.Ordinal);
        Assert.Contains("EntityPrevSkinningBones", lifecycle, StringComparison.Ordinal);
        Assert.Contains("uEntityPrevBonePaletteValid", lifecycle, StringComparison.Ordinal);
        Assert.Contains("UploadPreviousEntitySkinningBoneMatrices(frame.Gl);", setup, StringComparison.Ordinal);
        Assert.Contains("CapturePreviousEntitySkinningBones();", taa, StringComparison.Ordinal);
        Assert.Contains("InvalidatePreviousEntitySkinningBones();", taa, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewTaa_InitializesSceneCaptureWithoutRequiringGodRays()
    {
        var taa = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Taa.cs");
        var godRays = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.GodRays.cs");
        var post = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Render.PassPost.cs");

        Assert.Contains("TryInitSceneCaptureCore(gl, useOpenGlEs", taa, StringComparison.Ordinal);
        Assert.Contains("CanUseTaaSceneCapture", godRays, StringComparison.Ordinal);
        Assert.Contains("CanUseGodRayCapture(frame.Settings) ||", godRays, StringComparison.Ordinal);
        Assert.Contains("CanUseTaaSceneCapture(frame.Settings)", godRays, StringComparison.Ordinal);
        Assert.Contains("frame.Settings.EnableGodRays || frame.Settings.EnableScreenSpaceGodRays", post, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewTaa_InvalidatesHistoryWhenSceneInputsChange()
    {
        var backend = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.cs");
        var taa = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Taa.cs");

        Assert.Contains("private void InvalidatePreviewTaaHistory()", taa);
        Assert.Contains("SetScene(IRenderPreviewScene scene)", backend);
        Assert.Contains("SetMaterial(PreviewMaterial? material)", backend);
        Assert.Contains("SetBlockModelPreview(PreviewModelSubject? subject", backend);
        Assert.True(backend.Split("InvalidatePreviewTaaHistory()").Length >= 5);
    }

    [Fact]
    public void PreviewOpenGl4Setting_IsPersistedAndSynced()
    {
        var userSettings = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Models",
            "UserSettings.cs");
        var synchronizer = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Services",
            "UserSettingsSynchronizer.cs");
        var settingsTab = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Controls",
            "SettingsTab.axaml");
        var engineVm = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "ViewModels",
            "MainWindowViewModel.Settings.Engine.cs");
        var program = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Program.cs");
        var configurator = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "PreviewOpenGlPlatformConfigurator.cs");

        Assert.Contains("public bool PreviewUseOpenGl4 { get; set; }", userSettings, StringComparison.Ordinal);
        Assert.Contains("vm.PreviewUseOpenGl4 = settings.PreviewUseOpenGl4;", synchronizer, StringComparison.Ordinal);
        Assert.Contains("settings.PreviewUseOpenGl4 = vm.PreviewUseOpenGl4;", synchronizer, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding PreviewUseOpenGl4, Mode=TwoWay}\"", settingsTab, StringComparison.Ordinal);
        Assert.Contains("[ObservableProperty] private bool _previewUseOpenGl4;", engineVm, StringComparison.Ordinal);
        Assert.Contains("PreviewOpenGlRestartRequired", engineVm, StringComparison.Ordinal);
        Assert.Contains("PreviewOpenGlPlatformConfigurator.Configure(", program, StringComparison.Ordinal);
        Assert.Contains("CreateWin32PlatformOptions", configurator, StringComparison.Ordinal);
        Assert.Contains("CreateX11PlatformOptions", configurator, StringComparison.Ordinal);
        Assert.Contains("PreviewOpenGlSession.RequestedDesktopGl4 = settings.PreviewUseOpenGl4;", configurator, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewHdrSettings_AreWiredThroughPersistenceAndUi()
    {
        var userSettings = LoadSource(ThisFilePath(), "src", "AutoPBR.App", "Models", "UserSettings.cs");
        var synchronizer = LoadSource(ThisFilePath(), "src", "AutoPBR.App", "Services", "UserSettingsSynchronizer.cs");
        var settingsTab = LoadSource(ThisFilePath(), "src", "AutoPBR.App", "Controls", "SettingsTab.axaml");
        var hdrVm = LoadSource(ThisFilePath(), "src", "AutoPBR.App", "ViewModels", "MainWindowViewModel.Settings.Hdr.cs");
        var renderSettings = LoadSource(ThisFilePath(), "src", "AutoPBR.App", "Rendering", "Abstractions", "PreviewRenderSettings.cs");

        Assert.Contains("public string PreviewHdrMode { get; set; }", userSettings, StringComparison.Ordinal);
        Assert.Contains("public double PreviewHdrPaperWhiteNits { get; set; }", userSettings, StringComparison.Ordinal);
        Assert.Contains("vm.PreviewHdrMode = PreviewHdrPresentPolicy.FormatMode(", synchronizer, StringComparison.Ordinal);
        Assert.Contains("settings.PreviewHdrMode = PreviewHdrPresentPolicy.FormatMode(", synchronizer, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedPreviewHdrModeOption, Mode=TwoWay}\"", settingsTab, StringComparison.Ordinal);
        Assert.Contains("PreviewHdrStatusText", settingsTab, StringComparison.Ordinal);
        Assert.Contains("[ObservableProperty] private string _previewHdrMode", hdrVm, StringComparison.Ordinal);
        Assert.Contains("public bool HdrPresentActive { get; init; }", renderSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewWglPresentation_WiresSwapIntervalToVSyncToggle()
    {
        var previewControl = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Controls",
            "GlPbrPreviewControl.cs");
        var lifecycle = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "OpenGlPreviewBackend.Lifecycle.cs");
        var wglPresentation = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "PreviewWglPresentation.cs");
        var displayRefresh = LoadSource(ThisFilePath(),
            "src",
            "AutoPBR.App",
            "Rendering",
            "OpenGL",
            "PreviewDisplayRefreshRate.cs");

        Assert.Contains("ApplyPresentationVsync();", previewControl, StringComparison.Ordinal);
        Assert.Contains("_backend.ConfigurePresentationVsync(_glInterface, _presentationVsyncEnabled);", previewControl, StringComparison.Ordinal);
        Assert.Contains("ConfigurePresentationVsync(GlInterface glInterface, bool enabled, int? displayRefreshHz = null)", lifecycle, StringComparison.Ordinal);
        Assert.Contains("PreviewWglPresentation.TrySetSwapInterval(glInterface, interval)", lifecycle, StringComparison.Ordinal);
        Assert.Contains("wglSwapIntervalEXT", wglPresentation, StringComparison.Ordinal);
        Assert.Contains("GetDeviceCaps(dc, VRefresh)", displayRefresh, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewRenderExceptions_AreContainedAndPersisted()
    {
        var program = LoadSource(ThisFilePath(),
            "src", "AutoPBR.App", "Program.cs");
        var render = LoadSource(ThisFilePath(),
            "src", "AutoPBR.App", "Rendering", "OpenGL", "OpenGlPreviewBackend.Render.cs");
        var post = LoadSource(ThisFilePath(),
            "src", "AutoPBR.App", "Rendering", "OpenGL", "OpenGlPreviewBackend.Render.PassPost.cs");
        var clouds = LoadSource(ThisFilePath(),
            "src", "AutoPBR.App", "Rendering", "OpenGL", "OpenGlPreviewBackend.VolumetricClouds.cs");

        Assert.Contains("RegisterEmergencyExceptionLogging();", program, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException", program, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.UIThread.UnhandledException", program, StringComparison.Ordinal);
        Assert.Contains("GlRenderCoreUnsafe(framebuffer, pixelWidth, pixelHeight);", render, StringComparison.Ordinal);
        Assert.Contains("HandleUnhandledRenderException(framebuffer, pixelWidth, pixelHeight, ex);", render, StringComparison.Ordinal);
        Assert.Contains("LogService.AppendEmergencyDiagnostic(\"3D preview render exception\"", render, StringComparison.Ordinal);
        Assert.Contains("HandleCloudRuntimeFailure(ref frame, \"trace/temporal\", ex);", post, StringComparison.Ordinal);
        Assert.Contains("HandleCloudRuntimeFailure(ref frame, \"composite\", ex);", post, StringComparison.Ordinal);
        Assert.Contains("!_cloudRuntimeFaulted", clouds, StringComparison.Ordinal);
        Assert.Contains("FormatCloudAltitudeDiagnostic", clouds, StringComparison.Ordinal);
        Assert.Contains("Continuous altitude:", clouds, StringComparison.Ordinal);
    }

    [Fact]
    public void FlyCamera_RightVectorChecksDegeneracyBeforeNormalize()
    {
        var backend = LoadSource(ThisFilePath(),
            "src", "AutoPBR.App", "Rendering", "OpenGL", "OpenGlPreviewBackend.cs");

        var cross = backend.IndexOf("var rightRaw = Vector3.Cross(forward, worldUp);", StringComparison.Ordinal);
        var guard = backend.IndexOf("if (rightRaw.LengthSquared() < 1e-8f)", cross, StringComparison.Ordinal);
        var normalize = backend.IndexOf("Vector3.Normalize(rightRaw)", guard, StringComparison.Ordinal);

        Assert.True(cross >= 0);
        Assert.True(guard > cross);
        Assert.True(normalize > guard);
    }

    private static string ThisFilePath([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "") =>
        sourceFilePath;

    private static string LoadSource(string sourceFilePath, params string[] pathParts)
    {
        var sourceDir = Path.GetDirectoryName(sourceFilePath) ?? string.Empty;
        foreach (var start in new[] { sourceDir, AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var path = Path.Combine([dir.FullName, .. pathParts]);
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                dir = dir.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate source file '{Path.Combine(pathParts)}'.");
    }
}
