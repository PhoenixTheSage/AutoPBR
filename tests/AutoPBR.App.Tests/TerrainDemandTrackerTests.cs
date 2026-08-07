using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class TerrainDemandTrackerTests
{
    [Fact]
    public void UpdateCameraTarget_IsIncrementalAndIdempotent()
    {
        var tracker = new TerrainDemandTracker();
        var first = tracker.UpdateCameraTarget(new TerrainChunkKey(0, 0), hardRadiusChunks: 2, lodRingChunks: 8);
        var again = tracker.UpdateCameraTarget(new TerrainChunkKey(0, 0), hardRadiusChunks: 2, lodRingChunks: 8);
        Assert.Same(first, again);
        Assert.Equal(1, first.Token.DemandRevision);
        Assert.Empty(first.Exited);
        Assert.Equal(first.TargetCut.Count, first.Entered.Count);

        var moved = tracker.UpdateCameraTarget(new TerrainChunkKey(1, 0), hardRadiusChunks: 2, lodRingChunks: 8);
        Assert.Equal(2, moved.Token.DemandRevision);
        Assert.NotEmpty(moved.Entered);
        Assert.NotEmpty(moved.Exited);
        Assert.DoesNotContain(moved.Entered, key => moved.Exited.Contains(key));
        TerrainTargetCutBuilder.ValidateCameraTarget(
            moved.TargetCut,
            moved.CameraChunk,
            moved.HardRadiusChunks,
            moved.LodRingChunks);
    }

    [Fact]
    public void AdvanceContentGeneration_InvalidatesPayloadIdentity()
    {
        var tracker = new TerrainDemandTracker();
        var demand = tracker.UpdateCameraTarget(new TerrainChunkKey(0, 0), 1, 4);
        var leaf = demand.TargetCut.First();
        Assert.True(tracker.IsStillDemanded(leaf, demand.Token));

        var next = tracker.AdvanceContentGeneration();
        Assert.Equal(2, next.ContentGeneration);
        Assert.False(tracker.IsStillDemanded(leaf, demand.Token));
        Assert.True(tracker.IsContentCurrent(next.ContentGeneration));
    }

    [Fact]
    public void UpdateCameraTarget_DetectsTeleport()
    {
        var tracker = new TerrainDemandTracker();
        _ = tracker.UpdateCameraTarget(new TerrainChunkKey(0, 0), hardRadiusChunks: 2, lodRingChunks: 4);
        var teleport = tracker.UpdateCameraTarget(new TerrainChunkKey(64, 0), hardRadiusChunks: 2, lodRingChunks: 4);
        Assert.True(teleport.IsTeleport);
    }
}
