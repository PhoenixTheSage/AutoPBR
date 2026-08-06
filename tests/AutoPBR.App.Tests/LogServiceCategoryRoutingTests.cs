using AutoPBR.App.Services;

namespace AutoPBR.App.Tests;

public sealed class LogServiceCategoryRoutingTests
{
    [Theory]
    [InlineData("[3D preview] Moon billboard shader: error: ...", AppLogCategory.Shaders)]
    [InlineData("[3D preview] Genesis tessellation program ready (triangle patches).", AppLogCategory.Shaders)]
    [InlineData("[3D preview] Terrain residency gpuResident=12, desiredTotal=40", AppLogCategory.Terrain)]
    [InlineData("[3D preview] Volumetric cloud shader: link failed", AppLogCategory.Clouds)]
    [InlineData("[3D preview] P8 GPU timings: Main=1.2ms", AppLogCategory.GpuTimings)]
    [InlineData("[3D preview] Occlusion debug: culled=10", AppLogCategory.GpuTimings)]
    [InlineData("[3D preview] Frame fingerprint ABCD1234 (64x64 center crop)", AppLogCategory.GpuTimings)]
    [InlineData("[3D preview] Entity draw contract: pass=main path=entity/cow.png", AppLogCategory.Entities)]
    [InlineData("[3D preview] GPU runtime: path=entity/cow.png stride=16", AppLogCategory.Entities)]
    [InlineData("[3D preview] GPU WARN: path=entity/cow.png stride=12 expected=16", AppLogCategory.Entities)]
    [InlineData("[3D preview] Desktop OpenGL 4.x sidecar active; presentation uses ANGLE compositor pacing.", AppLogCategory.PreviewGl)]
    [InlineData("[3D preview] Creating desktop WGL sidecar on the dedicated WGL owner thread...", AppLogCategory.PreviewGl)]
    public void ClassifyPreviewDiagnostic_RoutesExpectedCategory(string message, AppLogCategory expected)
    {
        Assert.Equal(expected, LogService.ClassifyPreviewDiagnostic(message));
    }

    [Theory]
    [InlineData("[3D preview] Terrain residency gpuResident=12", false)]
    [InlineData("[3D preview] P8 GPU timings: Main=1.2ms", false)]
    [InlineData("[3D preview] Occlusion debug: culled=10", false)]
    [InlineData("[3D preview] Frame fingerprint ABCD1234 (64x64 center crop)", false)]
    [InlineData("[3D preview] Entity draw contract: pass=main path=entity/cow.png", false)]
    [InlineData("[3D preview] GPU runtime: path=entity/cow.png stride=16", false)]
    [InlineData("[3D preview] Creating desktop WGL sidecar on the dedicated WGL owner thread...", false)]
    [InlineData("[3D preview] Resolved ANGLE D3D11 device via QueryInterface.", false)]
    [InlineData("[3D preview] Desktop WGL sidecar init failed: access denied", true)]
    [InlineData("[3D preview] Sidecar GPU bootstrap failed: InvalidOperationException: boom", true)]
    [InlineData("[3D preview] Moon billboard shader: link failed", true)]
    [InlineData("[3D preview] GPU WARN: path=entity/cow.png stride=12 expected=16", true)]
    [InlineData("[3D preview] Render exception contained (NullReferenceException: x). Emergency log: C:\\logs\\x", true)]
    [InlineData("[3D preview] Atmosphere LUT pass error: 1282. Atmosphere fallback engaged.", true)]
    [InlineData("[3D preview] Desktop OpenGL 4.x sidecar active; presentation uses ANGLE compositor pacing.", true)]
    [InlineData("[3D preview] D3D11/WGL interop active; async GPU present (timed mutex + timed GPU drain).", true)]
    [InlineData("[3D preview] Material texture-array path disabled for this session; using texture-unit fallback.", true)]
    public void IsUserVisiblePreviewDiagnostic_FiltersSpam(string message, bool expectedVisible)
    {
        Assert.Equal(expectedVisible, LogService.IsUserVisiblePreviewDiagnostic(message));
    }

    [Fact]
    public void IsUserVisiblePreviewDiagnostic_HidesCloudLightingCacheDumpWithComputeFailureNone()
    {
        // Regression: bare "Failure" used to match computeFailure=none and spam the UI log.
        var message =
            "[3D preview] Flat continuous-world volumetric clouds active (sceneDepth=True, " +
            "cloudLightCache=near=ok;far-generated-cq3.6-compute;cascades=Far;" +
            "nearGeneration=1;farGeneration=1;fixture=cq2-density;" +
            "access=texture-fetch;scroll=0,0,0;planeReuse=0.00;mode=full-refresh;" +
            "computeFailure=none;nearDepth=-1787.18..812.36;farDepth=-4016.93..2869.24;" +
            "centerWeights=0.50/0.50/0.00;lifecycle=cq3.6;frame=0;requested=Both;result=generated;" +
            "historyConfidence=live-via-overlay (first-report-snapshot=1/8), " +
            "previewTaa=True, warmupDraws=3, noiseTex=True, coverageMap=True).";

        Assert.False(LogService.IsUserVisiblePreviewDiagnostic(message));
        Assert.Equal(AppLogCategory.Clouds, LogService.ClassifyPreviewDiagnostic(message));
    }

    [Fact]
    public void IsUserVisiblePreviewDiagnostic_HidesHiZOcclusionEnableNotice()
    {
        // Regression: mid-sentence "unavailable;" made this one-shot enable notice look like a fault.
        var message =
            "[3D preview] P5.3 Hi-Z occlusion culling enabled: opaque-terrain half-res depth prepass + max-depth pyramid; " +
            "Hi-Z culls shaded subject batches when voxel DDA atlas is unavailable; prepass omits subject/cutout and is not blitted " +
            "(avoids early-Z holes); GLES/ANGLE and alpha stay frustum/LOD only.";

        Assert.False(LogService.IsUserVisiblePreviewDiagnostic(message));
        Assert.True(LogService.IsUserVisiblePreviewDiagnostic(
            "[3D preview] Hi-Z build compute failed: link failed"));
        Assert.True(LogService.IsUserVisiblePreviewDiagnostic(
            "[3D preview] Atmosphere LUT targets unavailable; procedural sky only."));
    }

    [Fact]
    public void GetCategoryLogPath_UsesStableFileNamesUnderLogsDirectory()
    {
        Assert.Equal(
            Path.Combine(LogService.LogsDirectory, "shaders.log"),
            LogService.GetCategoryLogPath(AppLogCategory.Shaders));
        Assert.Equal(
            Path.Combine(LogService.LogsDirectory, "terrain.log"),
            LogService.GetCategoryLogPath(AppLogCategory.Terrain));
        Assert.Equal(
            Path.Combine(LogService.LogsDirectory, "AutoPBR_emergency.log"),
            LogService.GetCategoryLogPath(AppLogCategory.Emergency));
    }

    [Fact]
    public void WritePreviewDiagnostic_PersistsEvenWhenNotUserVisible()
    {
        var marker = $"terrain-residency-test-{Guid.NewGuid():N}";
        var message = $"[3D preview] Terrain residency {marker}";
        Assert.False(LogService.WritePreviewDiagnostic(message));

        var path = LogService.GetCategoryLogPath(AppLogCategory.Terrain);
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains(marker, text, StringComparison.Ordinal);
    }
}
