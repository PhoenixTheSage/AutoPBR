using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlTerrainTransferQueueTests
{
    [Fact]
    public void Pump_DefersWhenStagingSegmentsBusy()
    {
        var arena = new GlTerrainMeshArena(
            segmentCount: 1,
            vertexSegmentBytes: 64,
            indexSegmentBytes: 64,
            vertexPageBytes: 16,
            indexPageBytes: 16);
        Assert.True(arena.TryReserve(32, 32, out var reservation));
        var queue = new GlTerrainTransferQueue(
            arena,
            stagingSegmentCount: 2,
            stagingSegmentBytes: 16,
            maxBytesPerFrame: 64,
            maxChunksPerFrame: 4);
        var id = queue.Enqueue(reservation);

        var result = queue.Pump(
            frameOrFenceToken: 1,
            tryAcquireSegment: _ => false,
            submitChunk: _ => throw new InvalidOperationException("Should not submit."));

        Assert.True(result.DeferredByStagingPressure);
        Assert.Equal(0, result.BytesSubmitted);
        Assert.Equal(1, queue.PendingCount);
        Assert.True(queue.Cancel(id));
    }

    [Fact]
    public void Pump_PublishesOnlyAfterCompleteTransfer()
    {
        var arena = new GlTerrainMeshArena(
            segmentCount: 1,
            vertexSegmentBytes: 64,
            indexSegmentBytes: 64,
            vertexPageBytes: 16,
            indexPageBytes: 16);
        Assert.True(arena.TryReserve(32, 32, out var reservation));
        var queue = new GlTerrainTransferQueue(
            arena,
            stagingSegmentCount: 2,
            stagingSegmentBytes: 16,
            maxBytesPerFrame: 16,
            maxChunksPerFrame: 8);
        _ = queue.Enqueue(reservation);

        GlTerrainMeshArena.Allocation? published = null;
        var submitted = 0;
        while (queue.PendingCount > 0 && submitted < 16)
        {
            var result = queue.Pump(
                frameOrFenceToken: submitted + 1,
                tryAcquireSegment: _ => true,
                submitChunk: _ => { },
                publish: allocation => published = allocation);
            submitted += Math.Max(1, result.ChunksSubmitted);
            if (result.PublishedCount > 0)
            {
                break;
            }
        }

        Assert.NotNull(published);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(1, arena.GetTelemetry().LiveCount);
    }
}
