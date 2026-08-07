using AutoPBR.App.Rendering.Abstractions;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class TerrainStreamingTelemetryTests
{
    [Fact]
    public void Snapshot_reports_stage_counters_and_queue_gauges()
    {
        var telemetry = new TerrainStreamingTelemetry();
        telemetry.RecordPlanner(100);
        telemetry.RecordSchedulerDequeue();
        telemetry.RecordStaleDrop();
        telemetry.RecordBackpressure();
        telemetry.RecordCacheRead(hit: true, elapsedTicks: 50);
        telemetry.RecordBake(75);
        telemetry.RecordUpload(4096);
        telemetry.RecordStagingDeferral();
        telemetry.SetGpuState(1000, 200, 100);
        telemetry.SetCoverageState(3, 2);
        telemetry.SetQueueState(1, 10, 2, 20, 3, 30);
        telemetry.RecordStreamCpuFrame(0.25);
        telemetry.RecordStreamCpuFrame(0.75);

        var snapshot = telemetry.Snapshot();

        Assert.Equal(1, snapshot.PlannerUpdates);
        Assert.Equal(1, snapshot.SchedulerDequeues);
        Assert.Equal(1, snapshot.SchedulerStaleDrops);
        Assert.Equal(1, snapshot.SchedulerBackpressure);
        Assert.Equal(1, snapshot.CacheHits);
        Assert.Equal(1, snapshot.BakeCompleted);
        Assert.Equal(4096, snapshot.UploadBytes);
        Assert.Equal(3, snapshot.CoverageDebt);
        Assert.Equal(1000, snapshot.ActiveGpuBytes);
        Assert.Equal(3, snapshot.UploadQueueItems);
        Assert.Equal(0.75, snapshot.StreamCpuP95Ms);
    }

    [Theory]
    [InlineData(PreviewTerrainStreamingMode.Low, 1)]
    [InlineData(PreviewTerrainStreamingMode.Balanced, 2)]
    [InlineData(PreviewTerrainStreamingMode.High, 2)]
    public void Explicit_profiles_are_bounded(
        PreviewTerrainStreamingMode mode,
        int minimumWorkers)
    {
        var profile = TerrainStreamingProfile.Resolve(
            mode,
            processorCount: 16,
            dedicatedVramBytes: 12L * 1024 * 1024 * 1024,
            persistentTransferSupported: true);

        Assert.Equal(mode, profile.Mode);
        Assert.True(profile.BakeConcurrency >= minimumWorkers);
        Assert.True(profile.MaxInflightBytes > profile.MaxReadyBytes);
        Assert.True(profile.TransitionReserveBytes < profile.MeshArenaBytes);
        Assert.InRange(profile.MeshArenaPageBytes, 4 * 1024, 256 * 1024);
        Assert.True(profile.MeshArenaPageBytes < profile.TransferSegmentBytes);
    }

    [Fact]
    public void Adaptive_controller_reduces_fast_and_recovers_slowly()
    {
        var profile = TerrainStreamingProfile.Resolve(
            PreviewTerrainStreamingMode.Balanced,
            processorCount: 8,
            dedicatedVramBytes: 8L * 1024 * 1024 * 1024,
            persistentTransferSupported: true);
        var controller = new TerrainAdaptiveBudgetController(profile);

        var reduced = controller.Update(
            nowSeconds: 1,
            terrainStreamCpuP95Ms: profile.TerrainStreamCpuBudgetMs * 2,
            stagingBackpressured: true,
            memoryPressured: false,
            coverageDebt: 0);
        var cooldown = controller.Update(
            nowSeconds: 3,
            terrainStreamCpuP95Ms: 0,
            stagingBackpressured: false,
            memoryPressured: false,
            coverageDebt: 0);
        var recovering = controller.Update(
            nowSeconds: 7,
            terrainStreamCpuP95Ms: 0,
            stagingBackpressured: false,
            memoryPressured: false,
            coverageDebt: 0);

        Assert.True(reduced.Scale < 1);
        Assert.Equal(reduced.Scale, cooldown.Scale);
        Assert.True(recovering.Scale > cooldown.Scale);
    }
}
