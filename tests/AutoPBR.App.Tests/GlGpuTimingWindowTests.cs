using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlGpuTimingWindowTests
{
    [Fact]
    public void FormatCloudDiagnostic_ReportsBoundedP50AndP95()
    {
        var window = new GlGpuTimingWindow(capacity: 4);
        for (var i = 1; i <= 5; i++)
        {
            window.Add(new GlGpuTimingSnapshot(
                SetupMs: 0,
                ShadowMs: 0,
                SceneMs: 0,
                PostMs: 0,
                OverlayMs: 0,
                CloudTraceMs: i,
                CloudTemporalMs: 0,
                CloudUpsampleMs: i * 0.1,
                GodRayInjectMs: 0,
                GodRayIntegrateMs: 0,
                GodRayResolveMs: 0,
                TaaMs: 0));
        }

        var diagnostic = window.FormatCloudDiagnostic();
        Assert.Equal(4, window.Count);
        Assert.Contains("trace p50=3ms p95=5ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("composite p50=0.3ms p95=0.5ms", diagnostic, StringComparison.Ordinal);
    }
}
