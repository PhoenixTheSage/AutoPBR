using System.Globalization;

namespace AutoPBR.App.Rendering.OpenGL;

internal sealed class GlGpuTimingWindow(int capacity = 120)
{
    private readonly Queue<double> _cloudTrace = new(Math.Max(capacity, 1));
    private readonly Queue<double> _cloudComposite = new(Math.Max(capacity, 1));
    private readonly int _capacity = Math.Max(capacity, 1);

    public int Count => _cloudTrace.Count;

    public void Add(in GlGpuTimingSnapshot snapshot)
    {
        if (snapshot is not { CloudTraceMs: > 0.0 } and not { CloudUpsampleMs: > 0.0 })
        {
            return;
        }

        AddBounded(_cloudTrace, snapshot.CloudTraceMs);
        AddBounded(_cloudComposite, snapshot.CloudUpsampleMs);
    }

    public string FormatCloudDiagnostic()
    {
        var trace = Summarize(_cloudTrace);
        var composite = Summarize(_cloudComposite);
        return string.Format(
            CultureInfo.InvariantCulture,
            "cloudWindow={0} frames, trace p50={1:0.###}ms p95={2:0.###}ms, " +
            "composite p50={3:0.###}ms p95={4:0.###}ms",
            Count,
            trace.P50,
            trace.P95,
            composite.P50,
            composite.P95);
    }

    private void AddBounded(Queue<double> samples, double value)
    {
        if (samples.Count >= _capacity)
        {
            samples.Dequeue();
        }

        samples.Enqueue(Math.Max(value, 0.0));
    }

    private static (double P50, double P95) Summarize(Queue<double> samples)
    {
        if (samples.Count == 0)
        {
            return default;
        }

        var sorted = samples.ToArray();
        Array.Sort(sorted);
        return (Percentile(sorted, 0.50), Percentile(sorted, 0.95));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}
