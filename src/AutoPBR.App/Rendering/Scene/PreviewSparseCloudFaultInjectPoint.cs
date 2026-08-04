namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CQ4.7 test-only fault injection points for sparse recovery coverage. Production keeps
/// <see cref="None"/>.
/// </summary>
internal enum PreviewSparseCloudFaultInjectPoint
{
    None = 0,
    Dispatch = 1,
    Barrier = 2,
    Fence = 3,
    Status = 4,
    Publication = 5,
    CacheGeneration = 6,
    ContextLoss = 7,
}
