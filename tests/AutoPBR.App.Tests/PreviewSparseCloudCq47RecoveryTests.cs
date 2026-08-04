using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class PreviewSparseCloudCq47RecoveryTests
{
    [Fact]
    public void Allocator_RecyclesOrphanedGenerationAndRequestedPages()
    {
        var allocator = new PreviewSparseCloudBrickAllocator();
        var generating = new PreviewSparseCloudLogicalBrickKey(0, 1, 0, 0);
        var requested = new PreviewSparseCloudLogicalBrickKey(1, 2, 0, 0);

        Assert.True(allocator.TryRequest(generating, 1, 1f, out _));
        Assert.True(allocator.MarkGenerating(generating));
        Assert.Equal(1, allocator.GeneratingCount);
        Assert.True(
            allocator.TryRecycleOrphanedGeneration(generating, 4, 2));
        Assert.False(allocator.TryGet(generating, out _));
        Assert.Equal(0, allocator.GeneratingCount);
        Assert.Equal(1, allocator.RecycledCount);
        Assert.Equal(
            PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount,
            allocator.FreeCount);

        Assert.True(allocator.TryRequest(requested, 3, 0.5f, out _));
        Assert.True(allocator.TryRecycleUnreferenced(requested));
        Assert.False(allocator.TryGet(requested, out _));
        Assert.Equal(2, allocator.RecycledCount);
    }

    [Fact]
    public void Allocator_KeepsActiveReferencedBricksUntilPublicationDropsThem()
    {
        var allocator = new PreviewSparseCloudBrickAllocator();
        var key = new PreviewSparseCloudLogicalBrickKey(0, 4, -1, 2);
        Assert.True(allocator.TryRequest(key, 1, 1f, out var record));
        Assert.True(allocator.MarkGenerating(key));
        Assert.True(allocator.MarkResident(key, 7, 2));
        Assert.True(allocator.SetActiveReferenceCount(key, 1));
        Assert.False(allocator.TryRecycleUnreferenced(key));
        Assert.Equal(1, allocator.AllocatedCount);

        allocator.SyncActiveReferences(new HashSet<int>());
        Assert.Equal(0, allocator.GetPhysicalRecord(record.PhysicalBrickIndex).ActiveReferenceCount);
        Assert.True(allocator.TryRecycleUnreferenced(key));
        Assert.Equal(0, allocator.AllocatedCount);
        Assert.Equal(1, allocator.RecycledCount);
    }

    [Fact]
    public void DiagnosticsCounters_CaptureResidencyAndIdentityEvents()
    {
        var allocator = new PreviewSparseCloudBrickAllocator();
        var controller = new PreviewSparseCloudClipmapController();
        var counters = new PreviewSparseCloudDiagnosticsCounters();
        Assert.True(
            allocator.TryRequest(
                new PreviewSparseCloudLogicalBrickKey(0, 0, 0, 0),
                1,
                1f,
                out _));
        counters.CaptureResidency(allocator, controller, pendingRetireCount: 3);
        counters.NoteIdentityPromotion();
        counters.NoteIdentityDemotion();
        counters.NoteCq3PreparationStall();
        counters.AccumulateTraversalSample(4, 8, 2, 1);
        var diagnostic = counters.FormatDiagnostic();
        Assert.Contains("util=1", diagnostic, StringComparison.Ordinal);
        Assert.Contains("pendingRetire=3", diagnostic, StringComparison.Ordinal);
        Assert.Contains("promote=1", diagnostic, StringComparison.Ordinal);
        Assert.Contains("demote=1", diagnostic, StringComparison.Ordinal);
        Assert.Contains("cq3Stall=1", diagnostic, StringComparison.Ordinal);
        Assert.Contains("steps=page:4/dist:8/fine:2/fallback:1/frames:1", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugViewEnum_PreservesCq2ValuesAndAppendsSparseInspectors()
    {
        Assert.Equal(16, (int)PreviewCloudDebugView.AssetProfile);
        Assert.Equal(17, (int)PreviewCloudDebugView.SparseClipmapLevel);
        Assert.Equal(25, (int)PreviewCloudDebugView.SparseCascadeBlend);
        Assert.Equal(26, Enum.GetValues<PreviewCloudDebugView>().Length);
    }
}
