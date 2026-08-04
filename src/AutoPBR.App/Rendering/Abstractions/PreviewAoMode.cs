namespace AutoPBR.App.Rendering.Abstractions;

/// <summary>Screen-space ambient occlusion technique for the Genesis preview.</summary>
public enum PreviewAoMode
{
    /// <summary>Derive SSAO vs GTAO from <see cref="PreviewVolumetricQuality"/>.</summary>
    Auto = 0,

    /// <summary>Hemisphere depth SSAO.</summary>
    Ssao = 1,

    /// <summary>Ground-truth ambient occlusion (multi-slice horizon search).</summary>
    Gtao = 2,
}
