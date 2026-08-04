namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CQ4.7 bounded CPU-side sparse diagnostics. GPU traversal step totals are sampled only when
/// sparse debug views are active so production frames avoid per-pixel atomics and sync readback.
/// </summary>
internal sealed class PreviewSparseCloudDiagnosticsCounters
{
    public int RequestedPages { get; private set; }
    public int ResidentPages { get; private set; }
    public int GeneratingPages { get; private set; }
    public int FreeBricks { get; private set; }
    public int AtlasUtilization { get; private set; }
    public int Overflow { get; private set; }
    public int Recycled { get; private set; }
    public int OrphanedGenerationsRecycled { get; private set; }
    public int PendingRetire { get; private set; }
    public int IdentityPromotions { get; private set; }
    public int IdentityDemotions { get; private set; }
    public int Cq3PreparationStalls { get; private set; }
    public long PageSteps { get; private set; }
    public long DistanceSteps { get; private set; }
    public long FineSteps { get; private set; }
    public long FallbackQueries { get; private set; }
    public int TraversalSampleFrames { get; private set; }

    public void CaptureResidency(
        PreviewSparseCloudBrickAllocator allocator,
        PreviewSparseCloudClipmapController controller,
        int pendingRetireCount)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(controller);
        RequestedPages = controller.RequestedCount;
        ResidentPages = controller.ResidentCount;
        GeneratingPages = allocator.GeneratingCount;
        FreeBricks = allocator.FreeCount;
        AtlasUtilization = allocator.AllocatedCount;
        Overflow = allocator.OverflowCount;
        Recycled = allocator.RecycledCount;
        PendingRetire = Math.Max(0, pendingRetireCount);
    }

    public void NoteOrphanedGenerationRecycled(int count) =>
        OrphanedGenerationsRecycled = SaturatingAdd(
            OrphanedGenerationsRecycled,
            Math.Max(0, count));

    public void NoteIdentityPromotion() =>
        IdentityPromotions = SaturatingAdd(IdentityPromotions, 1);

    public void NoteIdentityDemotion() =>
        IdentityDemotions = SaturatingAdd(IdentityDemotions, 1);

    public void NoteCq3PreparationStall() =>
        Cq3PreparationStalls = SaturatingAdd(Cq3PreparationStalls, 1);

    public void AccumulateTraversalSample(
        int pageSteps,
        int distanceSteps,
        int fineSteps,
        int fallbackQueries)
    {
        PageSteps = SaturatingAdd(PageSteps, Math.Max(0, pageSteps));
        DistanceSteps = SaturatingAdd(DistanceSteps, Math.Max(0, distanceSteps));
        FineSteps = SaturatingAdd(FineSteps, Math.Max(0, fineSteps));
        FallbackQueries = SaturatingAdd(
            FallbackQueries,
            Math.Max(0, fallbackQueries));
        TraversalSampleFrames = SaturatingAdd(TraversalSampleFrames, 1);
    }

    public string FormatDiagnostic() =>
        $"req={RequestedPages};res={ResidentPages};gen={GeneratingPages};" +
        $"free={FreeBricks};util={AtlasUtilization};overflow={Overflow};" +
        $"recycled={Recycled};orphanRecycled={OrphanedGenerationsRecycled};" +
        $"pendingRetire={PendingRetire};" +
        $"promote={IdentityPromotions};demote={IdentityDemotions};" +
        $"cq3Stall={Cq3PreparationStalls};" +
        $"steps=page:{PageSteps}/dist:{DistanceSteps}/fine:{FineSteps}/" +
        $"fallback:{FallbackQueries}/frames:{TraversalSampleFrames}";

    private static int SaturatingAdd(int value, int delta) =>
        value > int.MaxValue - delta ? int.MaxValue : value + delta;

    private static long SaturatingAdd(long value, long delta) =>
        value > long.MaxValue - delta ? long.MaxValue : value + delta;
}
