using System.Numerics;
using System.Reflection;

using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

using Avalonia.OpenGL;

namespace AutoPBR.App.Tests;

/// <summary>
/// CQ4.8 acceptance: always-on CPU gates plus an opt-in hidden-WGL matrix gated by
/// <c>AUTOPBR_RUN_CQ4_ACCEPTANCE=1</c>.
/// </summary>
public sealed class PreviewCloudCq4AcceptanceTests
{
    private const string EnableAcceptanceEnv = "AUTOPBR_RUN_CQ4_ACCEPTANCE";
    private const int Width = 1280;
    private const int Height = 720;
    private const int WarmupFrames = 32;
    private const int SampleFrames = 96;
    private static readonly TimeSpan FrameElapsed = TimeSpan.FromSeconds(1.0 / 60.0);

    [Fact]
    public void MemoryAccounting_RemainsUnderSixteenMebibytes()
    {
        var accounting = PreviewSparseCloudVolumeContract.MemoryAccounting;
        Assert.True(
            accounting.IsWithinBudget,
            accounting.FormatDiagnostic());
        Assert.True(
            accounting.TotalBytes < PreviewSparseCloudVolumeContract.MemoryBudgetBytes);
        Assert.True(accounting.TotalBytes >= 12_684_604L);
    }

    [Fact]
    public void OverflowRecovery_RecyclesSlotsWithoutWrappingPhysicalIndices()
    {
        var allocator = new PreviewSparseCloudBrickAllocator();
        var keys = new List<PreviewSparseCloudLogicalBrickKey>();
        for (var index = 0;
             index < PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount;
             index++)
        {
            var key = new PreviewSparseCloudLogicalBrickKey(0, index, 0, 0);
            Assert.True(allocator.TryRequest(key, 1, 1f, out var record));
            Assert.InRange(
                record.PhysicalBrickIndex,
                0,
                PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount - 1);
            keys.Add(key);
        }

        Assert.Equal(0, allocator.FreeCount);
        Assert.False(
            allocator.TryRequest(
                new PreviewSparseCloudLogicalBrickKey(0, int.MaxValue, 0, 0),
                2,
                1f,
                out _));
        Assert.Equal(1, allocator.OverflowCount);

        Assert.True(allocator.MarkGenerating(keys[0]));
        Assert.True(allocator.MarkResident(keys[0], 1, 2));
        Assert.True(allocator.SetActiveReferenceCount(keys[0], 0));
        Assert.True(allocator.TryRecycleUnreferenced(keys[0]));
        Assert.Equal(1, allocator.FreeCount);
        Assert.True(
            allocator.TryRequest(
                new PreviewSparseCloudLogicalBrickKey(2, 99, 0, 0),
                3,
                1f,
                out var recycled));
        Assert.InRange(
            recycled.PhysicalBrickIndex,
            0,
            PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount - 1);
        Assert.Equal(
            PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount,
            allocator.AllocatedCount);
    }

    [Fact]
    public void ClipmapTeleport_RespectsEnteringCapAndRetiresOutsidePages()
    {
        var controller = new PreviewSparseCloudClipmapController();
        var first = controller.Update(
            Vector3.Zero,
            Vector3.UnitZ,
            cloudVerticalCenterWorldY: 120f,
            frustumPlanes: ReadOnlySpan<Vector4>.Empty,
            frame: 1,
            maximumEntering: PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame);
        Assert.InRange(
            first.Entering.Count,
            1,
            PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame);

        var teleported = controller.Update(
            new Vector3(50_000f, 0f, 50_000f),
            Vector3.UnitZ,
            cloudVerticalCenterWorldY: 120f,
            frustumPlanes: ReadOnlySpan<Vector4>.Empty,
            frame: 2,
            maximumEntering: PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame);
        Assert.True(teleported.Teleport || teleported.OriginChanged);
        Assert.True(teleported.Retired.Count > 0);
        Assert.InRange(
            teleported.Entering.Count,
            0,
            PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame);
    }

    [Fact]
    public void HiddenWglContext_Cq48SparseFlyThroughResidencyAndFaultRecovery()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableAcceptanceEnv),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var diagnostics = new List<string>();
        using var context = PreviewDesktopWglContext.TryCreate(
            [
                new GlVersion(GlProfileType.OpenGL, 4, 6),
                new GlVersion(GlProfileType.OpenGL, 3, 3),
            ],
            IntPtr.Zero,
            diagnostics.Add,
            probePresentationAdapter: false);
        Assert.NotNull(context);

        context!.Invoke(
            () =>
            {
                using (context.BindOnOwnerThread())
                {
                    context.EnsureRenderTargetCore(Width, Height);
                    using var backend = new OpenGlPreviewBackend();
                    backend.SetDiagnosticLog(diagnostics.Add);
                    backend.Initialize(new RenderPreviewInitializationOptions());
                    var settings = CreateCinematicSettings();
                    backend.SetRenderSettings(settings);
                    backend.SetScene(BlockPreviewSceneFactory.Create(settings));
                    backend.GlInitNativeWglPresenter(context.GlInterface);
                    var eye = new Vector3(0f, 140f, 0f);
                    backend.SetCameraDebugPose(eye, eye + Vector3.UnitZ * 80f);
                    for (var attempt = 0; attempt < 240; attempt++)
                    {
                        DrawFrame(context, backend);
                        if (attempt >= WarmupFrames)
                        {
                            break;
                        }
                    }

                    var accounting =
                        PreviewSparseCloudVolumeContract.MemoryAccounting;
                    Assert.True(
                        accounting.IsWithinBudget,
                        accounting.FormatDiagnostic());

                    for (var frame = 0; frame < SampleFrames; frame++)
                    {
                        eye += new Vector3(3f, MathF.Sin(frame * 0.05f) * 0.25f, 1.5f);
                        backend.SetCameraDebugPose(eye, eye + Vector3.UnitZ * 80f);
                        DrawFrame(context, backend);
                    }

                    var resourceField = typeof(OpenGlPreviewBackend)
                        .GetField(
                            "_sparseCloudResourceDiagnostic",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(resourceField);
                    var resourceDiagnostic =
                        resourceField!.GetValue(backend) as string ?? string.Empty;
                    Assert.False(string.IsNullOrWhiteSpace(resourceDiagnostic));
                    Assert.DoesNotContain(
                        "runtime-failed",
                        resourceDiagnostic,
                        StringComparison.Ordinal);
                    Assert.Contains(
                        "counters=",
                        resourceDiagnostic,
                        StringComparison.Ordinal);

                    backend.InjectSparseCloudFaultForTests(
                        PreviewSparseCloudFaultInjectPoint.ContextLoss);
                    DrawFrame(context, backend);
                    var faultedField = typeof(OpenGlPreviewBackend).GetField(
                        "_sparseCloudRuntimeFaulted",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(faultedField);
                    Assert.True((bool)faultedField!.GetValue(backend)!);

                    var highSettings = CreateHighSettings();
                    backend.SetRenderSettings(highSettings);
                    DrawFrame(context, backend);
                }
            });
    }

    private static PreviewRenderSettings CreateCinematicSettings() =>
        new()
        {
            AutoRotate = false,
            AnimateTimeOfDay = false,
            TimeOfDayHours = 12f,
            DrawPreviewSubject = false,
            ShowGroundMesh = true,
            ShowBackgroundGrid = false,
            ShowCornerAxes = false,
            ChunkViewDistance = 4,
            LodRingChunks = 4,
            EnableVolumetricClouds = true,
            VolumetricQuality = PreviewVolumetricQuality.Cinematic,
            CloudQuality = PreviewVolumetricQuality.Cinematic,
            CloudDensity = 1.1f,
            CloudCoverageScale = 1.35f,
            CloudLayerHeight = 4.8f,
            CloudVolumeHeight = 60f,
            CloudVolumeSize = 178f,
            CloudWindSpeed = 1.5f,
            CloudWindHeadingDegrees = 35f,
            CloudCirrusStrength = 0.35f,
            CloudDebugView = PreviewCloudDebugView.Off,
            CloudDisableTemporal = false,
            CloudFreezeWind = true,
            EnablePreviewTaa = true,
            PreviewTaaMode = 1,
            EnableShadows = false,
            EnableShadowCascades = false,
            LogGpuPassTimings = true,
            ShowExpandedGpuTimingHud = false,
        };

    private static PreviewRenderSettings CreateHighSettings()
    {
        var settings = CreateCinematicSettings();
        return new PreviewRenderSettings
        {
            AutoRotate = settings.AutoRotate,
            AnimateTimeOfDay = settings.AnimateTimeOfDay,
            TimeOfDayHours = settings.TimeOfDayHours,
            DrawPreviewSubject = settings.DrawPreviewSubject,
            ShowGroundMesh = settings.ShowGroundMesh,
            ShowBackgroundGrid = settings.ShowBackgroundGrid,
            ShowCornerAxes = settings.ShowCornerAxes,
            ChunkViewDistance = settings.ChunkViewDistance,
            LodRingChunks = settings.LodRingChunks,
            EnableVolumetricClouds = true,
            VolumetricQuality = PreviewVolumetricQuality.High,
            CloudQuality = PreviewVolumetricQuality.High,
            CloudDensity = settings.CloudDensity,
            CloudCoverageScale = settings.CloudCoverageScale,
            CloudLayerHeight = settings.CloudLayerHeight,
            CloudVolumeHeight = settings.CloudVolumeHeight,
            CloudVolumeSize = settings.CloudVolumeSize,
            CloudWindSpeed = settings.CloudWindSpeed,
            CloudWindHeadingDegrees = settings.CloudWindHeadingDegrees,
            CloudCirrusStrength = settings.CloudCirrusStrength,
            CloudDebugView = PreviewCloudDebugView.Off,
            CloudDisableTemporal = false,
            CloudFreezeWind = true,
            EnablePreviewTaa = true,
            PreviewTaaMode = 1,
            EnableShadows = false,
            EnableShadowCascades = false,
            LogGpuPassTimings = true,
            ShowExpandedGpuTimingHud = false,
        };
    }

    private static void DrawFrame(
        PreviewDesktopWglContext context,
        OpenGlPreviewBackend backend)
    {
        backend.RenderFrame(FrameElapsed);
        backend.GlRenderNativeWglPresenter(Width, Height, context.RenderFbo);
    }
}
