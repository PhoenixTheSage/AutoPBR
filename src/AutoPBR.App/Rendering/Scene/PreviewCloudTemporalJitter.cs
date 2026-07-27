namespace AutoPBR.App.Rendering.Scene;

/// <summary>Eight-frame base-2 low-discrepancy sequence used by the cloud ray marcher.</summary>
internal static class PreviewCloudTemporalJitter
{
    private static readonly float[] Samples = [0.5f, 0.25f, 0.75f, 0.125f, 0.625f, 0.375f, 0.875f, 0.0625f];

    public static int Period => Samples.Length;

    public static float Sample(int frameIndex) => Samples[Math.Abs(frameIndex % Samples.Length)];

    public static int AdvanceFrame(int frameIndex, bool temporalSamplingDisabled, int period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        return temporalSamplingDisabled ? frameIndex : (frameIndex + 1) % period;
    }
}
