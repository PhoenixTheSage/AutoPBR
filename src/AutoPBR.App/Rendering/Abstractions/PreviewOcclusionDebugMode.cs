namespace AutoPBR.App.Rendering.Abstractions;

/// <summary>Debug instrumentation for GPU occlusion culling (Hi-Z / voxel DDA).</summary>
public enum PreviewOcclusionDebugMode
{
    /// <summary>No counter readback or occlusion HUD.</summary>
    Off = 0,

    /// <summary>Enable compact diagnostic atomics, HUD occlusion counts, periodic log.</summary>
    Stats = 1,

    /// <summary>Stats plus more verbose HUD labeling (does not force Hi-Z when DDA is active).</summary>
    TintCulled = 2,
}
