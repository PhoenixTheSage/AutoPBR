using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class TerrainStreamSchedulerTests
{
    [Fact]
    public void Priority_UsesCoverageDeadlineArrivalRefinementFairnessThenSpeculation()
    {
        var scheduler = CreateScheduler();
        var items = new[]
        {
            Item(0, priority: TerrainStreamPriority.Speculation),
            Item(1, TerrainStreamPriority.AgingFairness),
            Item(2),
            Item(3, TerrainStreamPriority.PredictedArrival, predictedArrival: 40),
            Item(4, TerrainStreamPriority.TransactionDeadline, deadline: 30),
            // LOD coverage repair must beat Full detail: there is no global Full-first phase.
            Item(5, TerrainStreamPriority.CoverageRepair, lodLevel: 2),
        };

        foreach (var item in items)
        {
            RegisterAndEnqueue(scheduler, item);
        }

        var claimed = new List<TerrainStreamPriority>();
        while (scheduler.TryClaimNext(0, out var claim))
        {
            claimed.Add(claim.Item.Priority);
            Assert.Equal(TerrainStreamCompletionStatus.Accepted, scheduler.Complete(claim));
        }

        Assert.Equal(
            new[]
            {
                TerrainStreamPriority.CoverageRepair,
                TerrainStreamPriority.TransactionDeadline,
                TerrainStreamPriority.PredictedArrival,
                TerrainStreamPriority.VisibleRefinement,
                TerrainStreamPriority.AgingFairness,
                TerrainStreamPriority.Speculation,
            },
            claimed);
    }

    [Fact]
    public void Priority_OrdersDeadlinesAndPredictedArrivalsByTime()
    {
        var scheduler = CreateScheduler();
        var laterDeadline = Item(0, TerrainStreamPriority.TransactionDeadline, deadline: 80);
        var earlierDeadline = Item(1, TerrainStreamPriority.TransactionDeadline, deadline: 20);
        var laterArrival = Item(2, TerrainStreamPriority.PredictedArrival, predictedArrival: 90);
        var earlierArrival = Item(3, TerrainStreamPriority.PredictedArrival, predictedArrival: 10);
        foreach (var item in new[] { laterDeadline, earlierDeadline, laterArrival, earlierArrival })
        {
            RegisterAndEnqueue(scheduler, item);
        }

        AssertClaimKey(scheduler, earlierDeadline.Key, 0);
        AssertClaimKey(scheduler, laterDeadline.Key, 0);
        AssertClaimKey(scheduler, earlierArrival.Key, 0);
        AssertClaimKey(scheduler, laterArrival.Key, 0);
    }

    [Fact]
    public void Bounds_ApplyItemAndByteBackpressurePerQueue()
    {
        var scheduler = CreateScheduler(
            cacheRead: new TerrainStreamQueueLimit(2, 10));
        var first = Item(0, bytes: 6);
        var byteOverflow = Item(1, bytes: 5);
        var second = Item(2, bytes: 4);
        var itemOverflow = Item(3, bytes: 0);

        Register(scheduler, first);
        Register(scheduler, byteOverflow);
        Register(scheduler, second);
        Register(scheduler, itemOverflow);
        Assert.Equal(TerrainStreamEnqueueStatus.Accepted, scheduler.Enqueue(first));
        Assert.Equal(TerrainStreamEnqueueStatus.Backpressured, scheduler.Enqueue(byteOverflow));
        Assert.Equal(TerrainStreamEnqueueStatus.Accepted, scheduler.Enqueue(second));
        Assert.Equal(TerrainStreamEnqueueStatus.Backpressured, scheduler.Enqueue(itemOverflow));

        var metrics = scheduler.GetMetrics();
        Assert.Equal(2, metrics.CacheRead.QueuedItems);
        Assert.Equal(10, metrics.CacheRead.QueuedBytes);
        Assert.Equal(2, metrics.Backpressured);
    }

    [Fact]
    public void DemandRevision_DropsQueuedAndRejectsClaimedStaleOutput()
    {
        var coordinator = new TerrainStreamingCoordinator(CreateOptions());
        var old = Demand(0, revision: 1);
        Assert.Equal(
            TerrainStreamEnqueueStatus.Accepted,
            coordinator.SubmitDemand(old, nowTicks: 0));
        Assert.True(coordinator.TryClaim(TerrainStreamWorkKind.CacheRead, 0, out var oldClaim));

        var current = old with { DemandRevision = 2 };
        Assert.Equal(
            TerrainStreamEnqueueStatus.Accepted,
            coordinator.SubmitDemand(current, nowTicks: 1));
        Assert.Equal(
            TerrainStreamCompletionStatus.Stale,
            coordinator.CompleteCacheRead(oldClaim, cacheHit: true, nowTicks: 2));

        Assert.True(coordinator.TryClaim(TerrainStreamWorkKind.CacheRead, 2, out var currentClaim));
        Assert.Equal(2, currentClaim.Item.DemandRevision);
        Assert.True(coordinator.GetMetrics().StaleDropped >= 1);
    }

    [Fact]
    public void Aging_PromotesOldSpeculationSoNewUrgentWorkCannotStarveIt()
    {
        var scheduler = CreateScheduler(agingQuantum: 10);
        var oldSpeculation = Item(
            0,
            TerrainStreamPriority.Speculation,
            enqueuedAt: 0);
        RegisterAndEnqueue(scheduler, oldSpeculation);

        var newRepair = Item(
            1,
            TerrainStreamPriority.CoverageRepair,
            enqueuedAt: 40);
        RegisterAndEnqueue(scheduler, newRepair);

        Assert.True(scheduler.TryClaimNext(50, out var claim));
        Assert.Equal(oldSpeculation.Key, claim.Item.Key);
    }

    [Fact]
    public void Cancellation_DropsQueuedWorkAndHonorsClaimCancellation()
    {
        using var itemCancellation = new CancellationTokenSource();
        var scheduler = CreateScheduler();
        var cancelled = Item(0) with { CancellationToken = itemCancellation.Token };
        RegisterAndEnqueue(scheduler, cancelled);
        itemCancellation.Cancel();

        Assert.False(scheduler.TryClaimNext(0, out _));
        Assert.Equal(1, scheduler.GetMetrics().Cancelled);

        using var claimCancellation = new CancellationTokenSource();
        claimCancellation.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => scheduler.TryClaimNext(0, out _, claimCancellation.Token));
    }

    [Fact]
    public void ParentFallback_IsClaimedAndMadeAvailableBeforeChildDetail()
    {
        var scheduler = CreateScheduler();
        var childKey = TerrainResidencyKey.Full(0, 0);
        var parentKey = TerrainStreamScheduler.ParentOf(childKey);
        Assert.NotNull(parentKey);
        var child = Item(0, priority: TerrainStreamPriority.CoverageRepair) with
        {
            Key = childKey,
            ParentFallback = parentKey,
        };
        var parent = Item(1, lodLevel: 1) with
        {
            Key = parentKey.Value,
        };
        RegisterAndEnqueue(scheduler, child);
        RegisterAndEnqueue(scheduler, parent);

        Assert.True(scheduler.TryClaimNext(0, out var parentClaim));
        Assert.Equal(parent.Key, parentClaim.Item.Key);
        Assert.Equal(TerrainStreamCompletionStatus.Accepted, scheduler.Complete(parentClaim));
        scheduler.MarkCoverageAvailable(parent.Key, parent.ContentGeneration);

        Assert.True(scheduler.TryClaimNext(0, out var childClaim));
        Assert.Equal(child.Key, childClaim.Item.Key);
    }

    [Fact]
    public void Dedupe_PreventsDuplicateQueuedAndInflightClaims()
    {
        var scheduler = CreateScheduler();
        var item = Item(0);
        Register(scheduler, item);

        Assert.Equal(TerrainStreamEnqueueStatus.Accepted, scheduler.Enqueue(item));
        Assert.Equal(TerrainStreamEnqueueStatus.Duplicate, scheduler.Enqueue(item));
        Assert.True(scheduler.TryClaimNext(0, out var claim));
        Assert.Equal(TerrainStreamEnqueueStatus.Duplicate, scheduler.Enqueue(item));
        Assert.False(scheduler.TryClaimNext(0, out _));
        Assert.Equal(2, scheduler.GetMetrics().Deduplicated);
        Assert.Equal(TerrainStreamCompletionStatus.Accepted, scheduler.Complete(claim));
    }

    [Fact]
    public void Coordinator_UsesSeparateBoundedPipelineKindsAndReportsMetrics()
    {
        var coordinator = new TerrainStreamingCoordinator(CreateOptions());
        var demand = Demand(0, revision: 1);
        Assert.Equal(TerrainStreamEnqueueStatus.Accepted, coordinator.SubmitDemand(demand, 0));
        Assert.True(coordinator.TryClaim(TerrainStreamWorkKind.CacheRead, 0, out var read));
        Assert.Equal(
            TerrainStreamCompletionStatus.Accepted,
            coordinator.CompleteCacheRead(read, cacheHit: false, nowTicks: 1));
        Assert.True(coordinator.TryClaim(TerrainStreamWorkKind.CpuBake, 1, out var bake));

        var baked = coordinator.CompleteCpuBake(bake, nowTicks: 2);
        Assert.Equal(TerrainStreamCompletionStatus.Accepted, baked.Status);
        Assert.Equal(TerrainStreamEnqueueStatus.Accepted, baked.CacheWriteStatus);

        var metrics = coordinator.GetMetrics();
        Assert.Equal(1, metrics.UploadReady.QueuedItems);
        Assert.Equal(1, metrics.CacheWrite.QueuedItems);
        Assert.True(coordinator.TryClaim(TerrainStreamWorkKind.UploadReady, 2, out var upload));
        Assert.Equal(
            TerrainStreamCompletionStatus.Accepted,
            coordinator.CompleteUploadReady(upload));
        Assert.True(coordinator.Scheduler.IsCoverageAvailable(demand.Key, demand.ContentGeneration));
    }

    private static TerrainStreamScheduler CreateScheduler(
        TerrainStreamQueueLimit? cacheRead = null,
        long agingQuantum = 1_000) =>
        new(CreateOptions(cacheRead, agingQuantum));

    private static TerrainStreamSchedulerOptions CreateOptions(
        TerrainStreamQueueLimit? cacheRead = null,
        long agingQuantum = 1_000) =>
        new()
        {
            CacheRead = cacheRead ?? new TerrainStreamQueueLimit(32, 1_024),
            CpuBake = new TerrainStreamQueueLimit(32, 1_024),
            UploadReady = new TerrainStreamQueueLimit(32, 1_024),
            CacheWrite = new TerrainStreamQueueLimit(32, 1_024),
            AgingQuantumTicks = agingQuantum,
        };

    private static TerrainStreamWorkItem Item(
        int x,
        TerrainStreamPriority priority = TerrainStreamPriority.VisibleRefinement,
        long bytes = 1,
        long enqueuedAt = 0,
        long deadline = long.MaxValue,
        long predictedArrival = long.MaxValue,
        byte lodLevel = 0) =>
        new(
            new TerrainResidencyKey(x, 0, lodLevel),
            TerrainStreamWorkKind.CacheRead,
            priority,
            bytes,
            ContentGeneration: 7,
            DemandRevision: 1,
            enqueuedAt,
            deadline,
            predictedArrival);

    private static TerrainStreamDemand Demand(int x, long revision) =>
        new(
            TerrainResidencyKey.Full(x, 0),
            ContentGeneration: 7,
            DemandRevision: revision,
            TerrainStreamPriority.VisibleRefinement,
            CpuBakeBytes: 8,
            UploadReadyBytes: 8,
            CacheReadBytes: 2,
            CacheWriteBytes: 8);

    private static void Register(
        TerrainStreamScheduler scheduler,
        TerrainStreamWorkItem item) =>
        Assert.NotEqual(
            TerrainStreamDemandUpdateStatus.Stale,
            scheduler.UpdateDemand(
                item.Key,
                item.ContentGeneration,
                item.DemandRevision));

    private static void RegisterAndEnqueue(
        TerrainStreamScheduler scheduler,
        TerrainStreamWorkItem item)
    {
        Register(scheduler, item);
        Assert.Equal(TerrainStreamEnqueueStatus.Accepted, scheduler.Enqueue(item));
    }

    private static void AssertClaimKey(
        TerrainStreamScheduler scheduler,
        TerrainResidencyKey expected,
        long nowTicks)
    {
        Assert.True(scheduler.TryClaimNext(nowTicks, out var claim));
        Assert.Equal(expected, claim.Item.Key);
        Assert.Equal(TerrainStreamCompletionStatus.Accepted, scheduler.Complete(claim));
    }
}
