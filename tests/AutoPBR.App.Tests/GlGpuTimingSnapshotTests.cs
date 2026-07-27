using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlGpuTimingSnapshotTests
{
    [Fact]
    public void FormatHudLine_DefaultsToTotalOnly()
    {
        var snapshot = new GlGpuTimingSnapshot(
            SetupMs: 0.125,
            ShadowMs: 0.25,
            SceneMs: 1.5,
            PostMs: 0.375,
            OverlayMs: 0.0625);

        Assert.Equal(2.3125, snapshot.TotalMs, precision: 6);
        Assert.Equal("GPU 2.3 ms", snapshot.FormatHudLine());
        Assert.DoesNotContain("|", snapshot.FormatHudLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHudLine_ExpandedListsNonZeroPassesVerticallyWithFullNames()
    {
        var snapshot = new GlGpuTimingSnapshot(
            SetupMs: 0.125,
            ShadowMs: 0.25,
            SceneMs: 1.5,
            PostMs: 0.375,
            OverlayMs: 0.0625,
            CloudTraceMs: 2.0,
            CloudTemporalMs: 0.8,
            CloudUpsampleMs: 0.04,
            GodRayInjectMs: 1.2,
            GodRayIntegrateMs: 3.4,
            GodRayResolveMs: 0.6,
            TaaMs: 0.9);

        var hud = snapshot.FormatHudLine(expanded: true);
        Assert.Equal(
            "GPU 11.3 ms\n" +
            "Setup 0.1 ms\n" +
            "Shadow 0.3 ms\n" +
            "Scene 1.5 ms\n" +
            "Cloud Trace 2.0 ms\n" +
            "Cloud Temporal 0.8 ms\n" +
            "God Ray Inject 1.2 ms\n" +
            "God Ray Integrate 3.4 ms\n" +
            "God Ray Resolve 0.6 ms\n" +
            "TAA 0.9 ms\n" +
            "Post 0.4 ms\n" +
            "Overlay 0.1 ms",
            hud);
        Assert.DoesNotContain("Cloud Upsample", hud, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHudLine_SupportsCpuLabel()
    {
        var snapshot = new GlGpuTimingSnapshot(
            SetupMs: 1.25,
            ShadowMs: 0.0,
            SceneMs: 0.5,
            PostMs: 0.0,
            OverlayMs: 0.0);

        Assert.Equal("CPU 1.8 ms", snapshot.FormatHudLine("CPU", expanded: false));
        Assert.Equal(
            "CPU 1.8 ms\n" +
            "Setup 1.3 ms\n" +
            "Scene 0.5 ms",
            snapshot.FormatHudLine("CPU", expanded: true));
    }

    [Fact]
    public void FormatHudLine_ExpandedListsCpuSceneDetailGroups()
    {
        var snapshot = new GlGpuTimingSnapshot(
            SetupMs: 1.2,
            ShadowMs: 0.8,
            SceneMs: 5.0,
            PostMs: 0.0,
            OverlayMs: 0.0,
            CloudTraceMs: 0.0,
            CloudTemporalMs: 0.0,
            CloudUpsampleMs: 0.0,
            GodRayInjectMs: 0.0,
            GodRayIntegrateMs: 0.0,
            GodRayResolveMs: 0.0,
            TaaMs: 0.0,
            DepthPrepassMs: 0.0,
            HiZMs: 0.0,
            SetupBonesMs: 0.4,
            SetupBoundsMs: 0.3,
            ShadowTerrainCullMs: 0.6,
            TerrainStreamMs: 0.5,
            TerrainDrawMs: 1.5,
            SubjectDrawMs: 2.8);

        // Detail scopes are subsets — TotalMs excludes them.
        Assert.Equal(7.0, snapshot.TotalMs, precision: 6);
        Assert.Equal(
            "CPU 7.0 ms\n" +
            "Setup 1.2 ms\n" +
            "  Bones 0.4 ms\n" +
            "  Bounds 0.3 ms\n" +
            "Shadow 0.8 ms\n" +
            "  Terrain Cull 0.6 ms\n" +
            "Scene 5.0 ms\n" +
            "  Terrain Stream 0.5 ms\n" +
            "  Terrain Draw 1.5 ms\n" +
            "  Subject 2.8 ms",
            snapshot.FormatHudLine("CPU", expanded: true));
    }

    [Fact]
    public void FormatDiagnostic_IncludesSplitPassNames()
    {
        var snapshot = new GlGpuTimingSnapshot(
            SetupMs: 0.01,
            ShadowMs: 0.02,
            SceneMs: 0.03,
            PostMs: 0.04,
            OverlayMs: 0.05,
            CloudTraceMs: 0.06,
            CloudTemporalMs: 0.07,
            CloudUpsampleMs: 0.08,
            GodRayInjectMs: 0.09,
            GodRayIntegrateMs: 0.10,
            GodRayResolveMs: 0.11,
            TaaMs: 0.12);

        var diagnostic = snapshot.FormatDiagnostic();
        Assert.Contains("setup=0.01ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("shadow=0.02ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("depthPrepass=0ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("hiZ=0ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("scene=0.03ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("cloudTrace=0.06ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("cloudTemporal=0.07ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("cloudRepair=0ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("cloudUpsample=0.08ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("godRayInject=0.09ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("godRayIntegrate=0.1ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("godRayResolve=0.11ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("taa=0.12ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("postOther=0.04ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("overlay=0.05ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("total=0.78ms", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudRepairTiming_IsReportedAndIncludedInPostTotal()
    {
        var snapshot = new GlGpuTimingSnapshot(
            SetupMs: 0,
            ShadowMs: 0,
            SceneMs: 0,
            PostMs: 0,
            OverlayMs: 0,
            CloudTraceMs: 0,
            CloudTemporalMs: 0,
            CloudUpsampleMs: 0,
            GodRayInjectMs: 0,
            GodRayIntegrateMs: 0,
            GodRayResolveMs: 0,
            TaaMs: 0,
            CloudRepairMs: 0.25);

        Assert.Equal(0.25, snapshot.PostTotalMs, precision: 6);
        Assert.Equal(0.25, snapshot.TotalMs, precision: 6);
        Assert.Contains("Cloud Repair 0.3 ms", snapshot.FormatHudLine(expanded: true),
            StringComparison.Ordinal);
        Assert.Contains("cloudRepair=0.25ms", snapshot.FormatDiagnostic(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHudLine_LingerKeepsSubThresholdPassVisibleUntilDelayElapses()
    {
        var linger = new GlGpuTimingHudLinger();
        var above = new GlGpuTimingSnapshot(
            SetupMs: 0.0,
            ShadowMs: 0.0,
            SceneMs: 0.0,
            PostMs: 0.0,
            OverlayMs: 0.0,
            CloudTraceMs: 0.0,
            CloudTemporalMs: 0.0,
            CloudUpsampleMs: 0.12,
            GodRayInjectMs: 0.0,
            GodRayIntegrateMs: 0.0,
            GodRayResolveMs: 0.0,
            TaaMs: 0.0);
        var below = above with { CloudUpsampleMs = 0.01 };

        Assert.Contains(
            "Cloud Upsample 0.1 ms",
            above.FormatHudLine("GPU", expanded: true, linger, nowSeconds: 10.0),
            StringComparison.Ordinal);

        var stillVisible = below.FormatHudLine("GPU", expanded: true, linger, nowSeconds: 10.5);
        Assert.Contains("Cloud Upsample 0.0 ms", stillVisible, StringComparison.Ordinal);

        var afterDelay = below.FormatHudLine(
            "GPU",
            expanded: true,
            linger,
            nowSeconds: 10.0 + GlGpuTimingHudLinger.HideDelaySeconds + 0.01);
        Assert.DoesNotContain("Cloud Upsample", afterDelay, StringComparison.Ordinal);
    }
}
