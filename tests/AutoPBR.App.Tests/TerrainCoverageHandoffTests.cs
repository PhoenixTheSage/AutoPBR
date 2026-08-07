using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class TerrainCoverageHandoffTests
{
    [Fact]
    public void OutgoingFull_waits_for_real_lod_parent_coverage()
    {
        var camera = new TerrainChunkKey(0, 0);
        var full = TerrainResidencyKey.Full(3, 0);
        var residents = new HashSet<TerrainResidencyKey> { full };

        Assert.False(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                full,
                camera,
                keepRadiusChunks: 8,
                residents));

        residents.Add(TerrainResidencyKey.FromChunk(new TerrainChunkKey(3, 0), lodLevel: 1));
        Assert.True(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                full,
                camera,
                keepRadiusChunks: 8,
                residents));
    }

    [Fact]
    public void Replacement_coverage_requires_every_required_cell()
    {
        var camera = new TerrainChunkKey(0, 0);
        var leaving = TerrainResidencyKey.Section(0, 0, lodLevel: 2);
        var residents = new HashSet<TerrainResidencyKey> { leaving };
        var children = TerrainTargetCutBuilder.ChildrenOf(leaving);
        residents.UnionWith(children.Take(3));

        Assert.False(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                leaving,
                camera,
                keepRadiusChunks: 8,
                residents));

        residents.Add(children[3]);
        Assert.True(
            TerrainChunkStreamer.HasFootprintReplacementCoverage(
                leaving,
                camera,
                keepRadiusChunks: 8,
                residents));
    }
}
