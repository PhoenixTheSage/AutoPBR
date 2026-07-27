namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Keeps expanded HUD pass lines visible briefly after they drop below the display threshold,
/// so fluctuating near-zero timings stay readable instead of flickering out.
/// </summary>
internal sealed class GlGpuTimingHudLinger
{
    /// <summary>Pass slots used by <c>GlGpuTimerProfiler.FormatHudLine</c>.</summary>
    public const int PassCount = 21;

    /// <summary>Matches <c>0.0</c> ms formatting — values below this round away.</summary>
    public const double MinDisplayMs = 0.05;

    /// <summary>How long a pass stays listed after its last above-threshold sample.</summary>
    public const double HideDelaySeconds = 1.5;

    private readonly double[] _visibleUntilSeconds = new double[PassCount];

    public GlGpuTimingHudLinger()
    {
        Reset();
    }

    public bool ShouldShow(int passId, double ms, double nowSeconds)
    {
        if ((uint)passId >= PassCount)
        {
            return ms >= MinDisplayMs;
        }

        if (ms >= MinDisplayMs)
        {
            _visibleUntilSeconds[passId] = nowSeconds + HideDelaySeconds;
            return true;
        }

        return nowSeconds < _visibleUntilSeconds[passId];
    }

    public void Reset()
    {
        for (var i = 0; i < _visibleUntilSeconds.Length; i++)
        {
            _visibleUntilSeconds[i] = double.NegativeInfinity;
        }
    }
}
