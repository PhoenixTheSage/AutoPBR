using AutoPBR.App.Lang;

namespace AutoPBR.App.Rendering.Abstractions;

/// <summary>GPU preview initialization state for loading overlay and diagnostics.</summary>
public sealed class PreviewGpuInitProgress
{
    public bool ShaderSourcesReady { get; init; }
    public bool CoreReady { get; init; }
    public bool GodRaysReady { get; init; }
    public bool CloudsReady { get; init; }
    public bool PreviewTaaReady { get; init; }
    public bool ScreenSpaceAoReady { get; init; }
    public bool IsFullyReady { get; init; }
    public string Phase { get; init; } = PreviewGpuInitPhases.Starting;
    /// <summary>Combined init progress in [0, 1] for the preview loading bar.</summary>
    public double ProgressFraction { get; init; }
    public double ElapsedMs { get; init; }

    public static PreviewGpuInitProgress Starting => new() { Phase = PreviewGpuInitPhases.Starting };

    public static PreviewGpuInitProgress Ready(double elapsedMs) => new()
    {
        CoreReady = true,
        GodRaysReady = true,
        CloudsReady = true,
        PreviewTaaReady = true,
        ScreenSpaceAoReady = true,
        IsFullyReady = true,
        Phase = PreviewGpuInitPhases.Ready,
        ElapsedMs = elapsedMs,
    };
}
