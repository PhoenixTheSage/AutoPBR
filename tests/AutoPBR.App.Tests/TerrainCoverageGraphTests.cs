using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class TerrainCoverageGraphTests
{
    [Fact]
    public void BuildCameraTarget_ExtremeRing_RemainsSparse()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var cut = TerrainTargetCutBuilder.BuildCameraTarget(
            new TerrainChunkKey(0, 0),
            hardRadiusChunks: 8,
            lodRingChunks: 1024);
        started.Stop();

        Assert.InRange(cut.Count, 1, 8_000);
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(250), started.Elapsed.ToString());
    }

    [Fact]
    public void BuildCameraTarget_NegativeCoordinates_IsStrictAndComplete()
    {
        var camera = new TerrainChunkKey(-17, -9);
        var cut = TerrainTargetCutBuilder.BuildCameraTarget(camera, hardRadiusChunks: 2, lodRingChunks: 18);

        TerrainTargetCutBuilder.ValidateCameraTarget(cut, camera, 2, 18);
        for (var z = camera.Z - 2; z <= camera.Z + 2; z++)
        {
            for (var x = camera.X - 2; x <= camera.X + 2; x++)
            {
                Assert.Contains(TerrainResidencyKey.Full(x, z), cut);
            }
        }

        Assert.Contains(cut, key => key.IsLod && key.X < 0 && key.Z < 0);
    }

    [Fact]
    public void ValidateStrictCut_ParentAndChild_Throws()
    {
        var parent = TerrainResidencyKey.Section(-1, -1, 2);
        var child = TerrainTargetCutBuilder.ChildrenOf(parent)[0];

        var error = Assert.Throws<ArgumentException>(
            () => TerrainTargetCutBuilder.ValidateStrictCut([parent, child]));

        Assert.Contains("overlaps", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Split_Approach_KeepsParentUntilEveryChildIsDrawable()
    {
        var token = new TerrainDemandToken(4, 7);
        var parent = TerrainResidencyKey.Section(-2, 1, 1);
        var children = TerrainTargetCutBuilder.ChildrenOf(parent);
        var graph = new TerrainCoverageGraph(resourceCapacityBytes: 256, 4, 7);
        graph.Initialize(new Dictionary<TerrainResidencyKey, long> { [parent] = 100 }, token);

        Assert.True(
            graph.TryBeginSplit(parent, bytesPerChild: 20, token, out var split, out var failure),
            failure);
        Assert.NotNull(split);
        Assert.Equal(180, graph.ClaimedBytes);
        Assert.Equal(TerrainCoverageNodeState.TransitionOutgoing, graph.GetNode(parent)!.State);

        foreach (var child in children)
        {
            Assert.True(
                graph.TryPublishDrawable(split.Id, child, 15, token, out failure),
                failure);
            graph.AssertInvariants(children);
        }

        Assert.True(graph.CanCommit(split.Id));
        Assert.True(graph.TryCommit(split.Id, out failure), failure);
        graph.AssertInvariants(children);
        Assert.Equal(60, graph.ActiveBytes);
        Assert.Equal(100, graph.RetiringBytes);
        Assert.Equal(0, graph.ReservedBytes);
        Assert.All(children, child => Assert.Equal(TerrainCoverageNodeState.Active, graph.GetNode(child)!.State));
        Assert.Equal(TerrainCoverageNodeState.Retiring, graph.GetNode(parent)!.State);
    }

    [Fact]
    public void Merge_Recede_KeepsChildrenUntilParentIsDrawable()
    {
        var token = new TerrainDemandToken(2, 3);
        var parent = TerrainResidencyKey.Section(2, -3, 1);
        var children = TerrainTargetCutBuilder.ChildrenOf(parent);
        var graph = new TerrainCoverageGraph(resourceCapacityBytes: 160, 2, 3);
        graph.Initialize(children.ToDictionary(key => key, _ => 20L), token);

        Assert.True(
            graph.TryBeginMerge(parent, parentReservationBytes: 40, token, out var merge, out var failure),
            failure);
        Assert.NotNull(merge);
        graph.AssertInvariants([parent]);
        Assert.True(graph.TryPublishDrawable(merge.Id, parent, 35, token, out failure), failure);
        graph.AssertInvariants([parent]);
        Assert.True(graph.TryCommit(merge.Id, out failure), failure);

        graph.AssertInvariants([parent]);
        Assert.Equal(35, graph.ActiveBytes);
        Assert.Equal(80, graph.RetiringBytes);
        Assert.Equal(TerrainCoverageNodeState.Active, graph.GetNode(parent)!.State);
        Assert.All(children, child => Assert.Equal(TerrainCoverageNodeState.Retiring, graph.GetNode(child)!.State));
        Assert.Equal(4, graph.ReleaseAllRetired());
        Assert.Equal(35, graph.ClaimedBytes);
    }

    [Fact]
    public void Split_ResourcePressure_RefusesWithoutChangingActiveCut()
    {
        var token = new TerrainDemandToken(1, 1);
        var parent = TerrainResidencyKey.Section(0, 0, 1);
        var graph = new TerrainCoverageGraph(resourceCapacityBytes: 150, 1, 1);
        graph.Initialize(new Dictionary<TerrainResidencyKey, long> { [parent] = 100 }, token);

        Assert.False(
            graph.TryBeginSplit(parent, bytesPerChild: 20, token, out var split, out var failure));
        Assert.Null(split);
        Assert.Contains("available", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(100, graph.ClaimedBytes);
        Assert.Equal(0, graph.ReservedBytes);
        Assert.Equal(TerrainCoverageNodeState.Active, graph.GetNode(parent)!.State);
        graph.AssertInvariants([parent]);
    }

    [Fact]
    public void Split_FailedOrStalePublication_AbortsToOldCoverage()
    {
        var token = new TerrainDemandToken(9, 12);
        var parent = TerrainResidencyKey.Section(-1, 0, 1);
        var children = TerrainTargetCutBuilder.ChildrenOf(parent);
        var graph = new TerrainCoverageGraph(resourceCapacityBytes: 220, 9, 12);
        graph.Initialize(new Dictionary<TerrainResidencyKey, long> { [parent] = 100 }, token);
        Assert.True(graph.TryBeginSplit(parent, 20, token, out var split, out var failure), failure);
        Assert.NotNull(split);

        Assert.False(
            graph.TryPublishDrawable(
                split.Id,
                children[0],
                actualResourceBytes: 21,
                token,
                out failure));
        Assert.Contains("reserved", failure, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            graph.TryPublishDrawable(
                split.Id,
                children[0],
                actualResourceBytes: 15,
                new TerrainDemandToken(9, 11),
                out failure));
        Assert.Contains("stale", failure, StringComparison.OrdinalIgnoreCase);

        Assert.True(graph.TryPublishDrawable(split.Id, children[0], 15, token, out failure), failure);
        Assert.True(graph.AbortTransaction(split.Id, out failure), failure);
        Assert.Equal(TerrainCoverageNodeState.Active, graph.GetNode(parent)!.State);
        Assert.Equal(TerrainCoverageNodeState.Retiring, graph.GetNode(children[0])!.State);
        Assert.Equal(0, graph.ReservedBytes);
        graph.AssertInvariants([parent]);
        Assert.True(graph.ReleaseRetired(children[0]));
        Assert.Equal(100, graph.ClaimedBytes);
    }

    [Fact]
    public void DemandTracker_ContentAndDemandRevisionsRejectStaleWork()
    {
        var tracker = new TerrainDemandTracker(initialContentGeneration: 3);
        var first = tracker.UpdateCameraTarget(new TerrainChunkKey(-4, -4), 1, 8);
        var unchanged = tracker.UpdateCameraTarget(new TerrainChunkKey(-4, -4), 1, 8);
        Assert.Same(first, unchanged);

        var moved = tracker.UpdateCameraTarget(new TerrainChunkKey(-3, -4), 1, 8);
        Assert.True(moved.Token.DemandRevision > first.Token.DemandRevision);
        Assert.NotEmpty(moved.Entered);
        Assert.NotEmpty(moved.Exited);
        Assert.False(tracker.IsCurrent(first.Token));
        Assert.True(tracker.IsCurrent(moved.Token));

        var contentToken = tracker.AdvanceContentGeneration();
        Assert.Equal(4, contentToken.ContentGeneration);
        Assert.False(tracker.IsContentCurrent(moved.Token.ContentGeneration));
        Assert.False(tracker.IsStillDemanded(moved.TargetCut.First(), moved.Token));
    }

    [Fact]
    public void Teleport_CutReplacement_ChangesRequiredDomainOnlyAfterCompleteCoverage()
    {
        var tracker = new TerrainDemandTracker();
        var oldDemand = tracker.UpdateCameraTarget(new TerrainChunkKey(-20, 11), 1, 8);
        var newDemand = tracker.UpdateCameraTarget(new TerrainChunkKey(300, -250), 1, 8);
        Assert.True(newDemand.IsTeleport);
        Assert.Empty(oldDemand.TargetCut.Intersect(newDemand.TargetCut));

        var capacity = oldDemand.TargetCut.Count + newDemand.TargetCut.Count;
        var graph = new TerrainCoverageGraph(capacity, 1, oldDemand.Token.DemandRevision);
        graph.Initialize(oldDemand.TargetCut.ToDictionary(key => key, _ => 1L), oldDemand.Token);
        graph.AdvanceRevisions(newDemand.Token);
        Assert.True(
            graph.TryBeginCutReplacement(
                newDemand.TargetCut.ToDictionary(key => key, _ => 1L),
                newDemand.Token,
                out var replacement,
                out var failure),
            failure);
        Assert.NotNull(replacement);

        foreach (var key in newDemand.TargetCut.OrderBy(key => key.LodLevel).ThenBy(key => key.Z).ThenBy(key => key.X))
        {
            graph.AssertCoverage(
                oldDemand.CameraChunk,
                oldDemand.HardRadiusChunks,
                oldDemand.LodRingChunks);
            Assert.True(graph.TryPublishDrawable(replacement.Id, key, 1, newDemand.Token, out failure), failure);
        }

        Assert.True(graph.TryCommit(replacement.Id, out failure), failure);
        graph.AssertCoverage(
            newDemand.CameraChunk,
            newDemand.HardRadiusChunks,
            newDemand.LodRingChunks);
        Assert.Equal(newDemand.TargetCut.Count, graph.ActiveCut.Count);
        Assert.Equal(oldDemand.TargetCut.Count, graph.ReleaseAllRetired());
    }

    [Fact]
    public void RepeatedApproachAndRecede_NeverExposeAChunkCell()
    {
        var token = new TerrainDemandToken(1, 1);
        var parent = TerrainResidencyKey.Section(0, 0, 1);
        var children = TerrainTargetCutBuilder.ChildrenOf(parent);
        var graph = new TerrainCoverageGraph(resourceCapacityBytes: 256, 1, 1);
        graph.Initialize(new Dictionary<TerrainResidencyKey, long> { [parent] = 32 }, token);

        for (var cycle = 0; cycle < 4; cycle++)
        {
            Assert.True(graph.TryBeginSplit(parent, 8, token, out var split, out var failure), failure);
            Assert.NotNull(split);
            foreach (var child in children)
            {
                Assert.True(graph.TryPublishDrawable(split.Id, child, 8, token, out failure), failure);
                graph.AssertInvariants(children);
            }

            Assert.True(graph.TryCommit(split.Id, out failure), failure);
            Assert.True(graph.ReleaseRetired(parent));
            graph.AssertInvariants(children);

            Assert.True(graph.TryBeginMerge(parent, 32, token, out var merge, out failure), failure);
            Assert.NotNull(merge);
            Assert.True(graph.TryPublishDrawable(merge.Id, parent, 32, token, out failure), failure);
            graph.AssertInvariants([parent]);
            Assert.True(graph.TryCommit(merge.Id, out failure), failure);
            graph.AssertInvariants([parent]);
            Assert.Equal(4, graph.ReleaseAllRetired());
        }
    }
}
