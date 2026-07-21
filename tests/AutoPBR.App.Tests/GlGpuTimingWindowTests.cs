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
            window.Add(new GlGpuTimingSnapshot(0, 0, 0, 0, 0, i, 0, i * 0.1));
        }

        var diagnostic = window.FormatCloudDiagnostic();
        Assert.Equal(4, window.Count);
        Assert.Contains("trace p50=3ms p95=5ms", diagnostic, StringComparison.Ordinal);
        Assert.Contains("composite p50=0.3ms p95=0.5ms", diagnostic, StringComparison.Ordinal);
    }
}
