namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Throttles expanded FPS/CPU timing HUD string publishes so Avalonia bitmap rebuilds
/// and GL texture uploads stay near the UI overlay poll rate (~5 Hz).
/// </summary>
internal static class GlTimingHudPublishGate
{
    /// <summary>Matches <c>MainWindowViewModel</c> camera-pose / FPS overlay timer interval.</summary>
    public const double IntervalSeconds = 0.2;

    public static bool ShouldPublish(ref double lastPublishSeconds, double nowSeconds)
    {
        if (nowSeconds - lastPublishSeconds < IntervalSeconds)
        {
            return false;
        }

        lastPublishSeconds = nowSeconds;
        return true;
    }
}
