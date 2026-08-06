using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class GlTerrainOccluderAtlasTests
{
    [Fact]
    public void SlowBakeDiagnostic_ExceedsRebuildDebounce()
    {
        // A slow bake remains single-flight; this threshold is diagnostic and must not be
        // confused with the recenter debounce.
        Assert.True(
            GlTerrainOccluderAtlas.BakeSlowDiagnosticMs > GlTerrainOccluderAtlas.RebuildDebounceMs);
    }

    [Fact]
    public void CpuBake_BoundsParallelismAndCoarseSampling()
    {
        Assert.InRange(GlTerrainOccluderAtlas.CpuBakeMaxDegreeOfParallelism, 1, 2);
        Assert.Equal(8, GlTerrainOccluderAtlas.CoarseSamplesPerAxis);
        Assert.True(
            GlTerrainOccluderAtlas.CoarseSamplesPerAxis *
            GlTerrainOccluderAtlas.CoarseSamplesPerAxis < 128 * 128);
    }

    [Fact]
    public void ResolveCoarseOccluderCellMeters_tracks_lod_ring()
    {
        Assert.Equal(
            PreviewStageConstants.TerrainOccluderCoarseMinCellMeters,
            PreviewStageConstants.ResolveCoarseOccluderCellMeters(2));
        Assert.True(PreviewStageConstants.ResolveCoarseOccluderCellMeters(128) >= 8);
        Assert.Equal(
            TerrainResidencyKey.SampleStepMetersForLevel(7),
            PreviewStageConstants.ResolveCoarseOccluderCellMeters(1024));
        Assert.True(
            PreviewStageConstants.ResolveCoarseOccluderCellMeters(1024) >=
            PreviewStageConstants.ResolveCoarseOccluderCellMeters(16));
    }

    [Fact]
    public void ResidentValidity_UsesExplicitResidencyInsteadOfSignedHashSentinel()
    {
        Assert.True(GlTerrainOccluderAtlas.EvaluateValidity(
            texture: 7,
            width: 784,
            height: 784,
            hasResidentData: true));
        Assert.False(GlTerrainOccluderAtlas.EvaluateValidity(
            texture: 7,
            width: 784,
            height: 784,
            hasResidentData: false));
    }
}
