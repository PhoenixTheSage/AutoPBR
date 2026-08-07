using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlTerrainMeshArenaTests
{
    [Fact]
    public void Reserve_PairsPageAlignedRangesInOneFixedSegment()
    {
        var arena = CreateArena(segmentCount: 2, pagesPerSegment: 4);

        Assert.True(arena.TryReserve(5, 9, out var reservation));

        Assert.InRange(reservation.SegmentIndex, 0, 1);
        Assert.Equal(0, reservation.VertexOffsetBytes);
        Assert.Equal(0, reservation.IndexOffsetBytes);
        Assert.Equal(8, reservation.VertexReservedBytes);
        Assert.Equal(12, reservation.IndexReservedBytes);
        Assert.Equal(32, arena.VertexCapacityBytes);
        Assert.Equal(32, arena.IndexCapacityBytes);

        var telemetry = arena.GetTelemetry();
        Assert.Equal(8, telemetry.ReservedVertexBytes);
        Assert.Equal(12, telemetry.ReservedIndexBytes);
        Assert.Equal(1, telemetry.ReservedCount);
        Assert.Equal(0, telemetry.LiveCount);
    }

    [Fact]
    public void Reserve_OrdinaryPressurePreservesExplicitTransitionHeadroom()
    {
        var arena = new GlTerrainMeshArena(
            segmentCount: 1,
            vertexSegmentBytes: 16,
            indexSegmentBytes: 16,
            vertexPageBytes: 4,
            indexPageBytes: 4,
            transitionVertexHeadroomBytes: 4,
            transitionIndexHeadroomBytes: 4);

        Assert.True(arena.TryReserve(12, 12, out _));
        Assert.False(arena.TryReserve(4, 4, out _));
        Assert.True(arena.TryReserve(4, 4, isTransition: true, out var transition));
        Assert.True(transition.IsTransition);
        Assert.Equal(4, arena.TransitionVertexHeadroomBytes);
        Assert.Equal(4, arena.TransitionIndexHeadroomBytes);
    }

    [Fact]
    public void ProfileHeadroom_IsScaledToRealizedArenaAndLeavesOrdinaryCapacity()
    {
        const int mib = 1024 * 1024;
        var headroom = GlTerrainMeshArena.ResolveTransitionHeadroomPerStreamSegment(
            requestedArenaBytes: 4L * 1024 * mib,
            requestedTransitionReserveBytes: 512L * mib,
            realizedArenaBytes: 512L * mib,
            segmentCount: 16,
            pageBytes: 64 * 1024,
            segmentBytes: 16 * mib);

        Assert.Equal(2 * mib, headroom);
        var arena = new GlTerrainMeshArena(
            segmentCount: 16,
            vertexSegmentBytes: 16 * mib,
            indexSegmentBytes: 16 * mib,
            vertexPageBytes: 64 * 1024,
            indexPageBytes: 64 * 1024,
            transitionVertexHeadroomBytes: headroom,
            transitionIndexHeadroomBytes: headroom);

        for (var i = 0; i < 256; i++)
        {
            Assert.True(
                arena.TryReserve(256 * 1024, 128 * 1024, isTransition: false, out _),
                $"Production-shaped arena stopped ordinary admission at reservation {i}.");
        }

        Assert.Equal(32 * mib, arena.TransitionVertexHeadroomBytes);
        Assert.Equal(32 * mib, arena.TransitionIndexHeadroomBytes);
    }

    [Fact]
    public void Reserve_PairedPressureAndOversizeRefusalDoNotMutateArena()
    {
        var arena = CreateArena(segmentCount: 1, pagesPerSegment: 4);
        Assert.True(arena.TryReserve(12, 4, out _));
        var before = arena.GetTelemetry();

        Assert.False(arena.TryReserve(4, 16, out _));
        Assert.False(arena.TryReserve(20, 4, isTransition: true, out _));
        Assert.False(arena.TryReserve(0, 4, out _));

        Assert.Equal(before, arena.GetTelemetry());
    }

    [Fact]
    public void Retirement_KeepsPagesUnavailableUntilFenceTokenCompletes()
    {
        var arena = CreateArena(segmentCount: 1, pagesPerSegment: 4);
        Assert.True(arena.TryReserve(16, 16, out var reservation));
        Assert.True(arena.TryPublish(reservation, out var allocation));
        Assert.True(arena.Retire(allocation, completionToken: 42));

        Assert.False(arena.TryReserve(4, 4, isTransition: true, out _));
        Assert.Equal(0, arena.ReclaimCompleted(token => token < 42));
        Assert.Equal(1, arena.GetTelemetry().RetiringCount);

        var polled = new List<long>();
        Assert.Equal(1, arena.ReclaimCompleted(token =>
        {
            polled.Add(token);
            return token == 42;
        }));
        Assert.Equal([42L], polled);
        Assert.True(arena.TryReserve(16, 16, isTransition: true, out _));
    }

    [Fact]
    public void FreeList_ReportsFragmentationAndCoalescesReclaimedPages()
    {
        var arena = CreateArena(segmentCount: 1, pagesPerSegment: 8);
        Assert.True(arena.TryReserve(4, 4, out var first));
        Assert.True(arena.TryReserve(4, 4, out var middle));
        Assert.True(arena.TryReserve(4, 4, out var last));
        Assert.True(arena.TryPublish(first, out var firstLive));
        Assert.True(arena.TryPublish(middle, out var middleLive));
        Assert.True(arena.TryPublish(last, out var lastLive));

        Assert.True(arena.Retire(middleLive, 1));
        Assert.Equal(1, arena.ReclaimCompleted(token => token == 1));
        var fragmented = arena.GetTelemetry();
        Assert.Equal(24, fragmented.FreeVertexBytes);
        Assert.Equal(20, fragmented.LargestFreeVertexRangeBytes);
        Assert.True(fragmented.VertexFragmentation > 0d);

        Assert.True(arena.Retire(firstLive, 2));
        Assert.True(arena.Retire(lastLive, 2));
        Assert.Equal(2, arena.ReclaimCompleted(token => token == 2));
        var coalesced = arena.GetTelemetry();
        Assert.Equal(32, coalesced.LargestFreeVertexRangeBytes);
        Assert.Equal(0d, coalesced.VertexFragmentation);
        Assert.Equal(0, coalesced.LiveCount);
    }

    [Fact]
    public void TransferQueue_ChunksStreamsAndHonorsPerFrameBudgets()
    {
        var arena = CreateArena(segmentCount: 1, pagesPerSegment: 16);
        Assert.True(arena.TryReserve(10, 6, out var reservation));
        var queue = new GlTerrainTransferQueue(
            arena,
            stagingSegmentCount: 4,
            stagingSegmentBytes: 4,
            maxBytesPerFrame: 6,
            maxChunksPerFrame: 2);
        queue.Enqueue(reservation);
        var chunks = new List<GlTerrainTransferQueue.Chunk>();

        var first = queue.Pump(10, _ => true, chunks.Add);
        var second = queue.Pump(11, _ => true, chunks.Add);
        var third = queue.Pump(12, _ => true, chunks.Add);

        Assert.Equal(6, first.BytesSubmitted);
        Assert.Equal(2, first.ChunksSubmitted);
        Assert.Equal(6, second.BytesSubmitted);
        Assert.Equal(2, second.ChunksSubmitted);
        Assert.Equal(4, third.BytesSubmitted);
        Assert.Equal([4, 2, 4, 2, 4], chunks.Select(chunk => chunk.ByteCount).ToArray());
        Assert.Equal(
            [
                GlTerrainTransferQueue.StreamKind.Vertex,
                GlTerrainTransferQueue.StreamKind.Vertex,
                GlTerrainTransferQueue.StreamKind.Vertex,
                GlTerrainTransferQueue.StreamKind.Index,
                GlTerrainTransferQueue.StreamKind.Index,
            ],
            chunks.Select(chunk => chunk.Stream).ToArray());
        Assert.All(chunks, chunk => Assert.InRange(chunk.ByteCount, 1, 4));
        Assert.Equal(1, third.PublishedCount);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void TransferQueue_StagingBackpressurePollsAndDefersWithoutBlocking()
    {
        var arena = CreateArena(segmentCount: 1, pagesPerSegment: 4);
        Assert.True(arena.TryReserve(4, 4, out var reservation));
        var queue = new GlTerrainTransferQueue(arena, 2, 8, 8);
        queue.Enqueue(reservation);
        var polls = new List<int>();

        var blocked = queue.Pump(
            5,
            segment =>
            {
                polls.Add(segment);
                return false;
            },
            _ => throw new InvalidOperationException("No chunk should submit while staging is busy."));

        Assert.True(blocked.DeferredByStagingPressure);
        Assert.Equal(2, blocked.StagingAcquisitionPolls);
        Assert.Equal([0, 1], polls);
        Assert.Equal(0, blocked.BytesSubmitted);
        Assert.True(arena.IsReserved(reservation));

        var resumed = queue.Pump(6, _ => true, _ => { });
        Assert.Equal(8, resumed.BytesSubmitted);
        Assert.Equal(1, resumed.PublishedCount);
    }

    [Fact]
    public void TransferQueue_CancellationFreesUntouchedAndRetiresPartialUploads()
    {
        var arena = CreateArena(segmentCount: 1, pagesPerSegment: 8);
        Assert.True(arena.TryReserve(4, 4, out var untouched));
        var queue = new GlTerrainTransferQueue(arena, 1, 4, 4);
        var untouchedId = queue.Enqueue(untouched);

        Assert.True(queue.Cancel(untouchedId));
        Assert.Equal(0, arena.GetTelemetry().ReservedCount);

        Assert.True(arena.TryReserve(8, 4, out var partial));
        var partialId = queue.Enqueue(partial);
        var pump = queue.Pump(77, _ => true, _ => { });
        Assert.Equal(4, pump.BytesSubmitted);
        Assert.True(queue.Cancel(partialId));

        var retiring = arena.GetTelemetry();
        Assert.Equal(1, retiring.RetiringCount);
        Assert.Equal(0, retiring.ReservedCount);
        Assert.Equal(0, arena.ReclaimCompleted(token => token != 77));
        Assert.Equal(1, arena.ReclaimCompleted(token => token == 77));
    }

    [Fact]
    public void TransferQueue_PublishesOnlyAfterFinalChunkCompletes()
    {
        var arena = CreateArena(segmentCount: 1, pagesPerSegment: 4);
        Assert.True(arena.TryReserve(2, 2, out var reservation));
        var queue = new GlTerrainTransferQueue(arena, 1, 2, 2);
        queue.Enqueue(reservation);
        var published = new List<GlTerrainMeshArena.Allocation>();

        var first = queue.Pump(1, _ => true, _ => { }, published.Add);
        Assert.Equal(0, first.PublishedCount);
        Assert.Empty(published);
        Assert.Equal(1, arena.GetTelemetry().ReservedCount);
        Assert.Equal(0, arena.GetTelemetry().LiveCount);

        var second = queue.Pump(2, _ => true, _ => { }, published.Add);
        Assert.Equal(1, second.PublishedCount);
        Assert.Single(published);
        Assert.Equal(reservation.Id, published[0].Id);
        Assert.Equal(0, arena.GetTelemetry().ReservedCount);
        Assert.Equal(1, arena.GetTelemetry().LiveCount);
    }

    private static GlTerrainMeshArena CreateArena(int segmentCount, int pagesPerSegment) =>
        new(
            segmentCount,
            vertexSegmentBytes: pagesPerSegment * 4,
            indexSegmentBytes: pagesPerSegment * 4,
            vertexPageBytes: 4,
            indexPageBytes: 4);
}
