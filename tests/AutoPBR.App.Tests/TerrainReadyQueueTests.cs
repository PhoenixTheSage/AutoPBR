using AutoPBR.App.Rendering.Scene;

namespace AutoPBR.App.Tests;

public sealed class TerrainReadyQueueTests
{
    [Fact]
    public void ReturnReady_deduplicates_the_same_residency_key()
    {
        using var streamer = new TerrainChunkStreamer();
        var mesh = CreateMesh(TerrainResidencyKey.Full(3, 4));

        streamer.ReturnReady(mesh);
        streamer.ReturnReady(mesh);

        Assert.Equal(1, streamer.ReadyCount);
        Assert.Equal(1, streamer.InflightCount);
        var ready = new List<PreviewTerrainChunkMesh>();
        Assert.Equal(1, streamer.DrainReady(ready, 8));
        Assert.Single(ready);
        streamer.NotifyUploaded(ready[0].Key, ready[0].Lod);
        Assert.Equal(0, streamer.ReadyCount);
        Assert.Equal(0, streamer.InflightCount);
    }

    [Fact]
    public void DrainReadySplit_keeps_overflow_inflight_without_duplicating_it()
    {
        using var streamer = new TerrainChunkStreamer();
        var keys = new[]
        {
            TerrainResidencyKey.Full(0, 0),
            TerrainResidencyKey.Full(1, 0),
            TerrainResidencyKey.Full(2, 0),
            TerrainResidencyKey.Full(3, 0),
        };
        foreach (var key in keys)
        {
            streamer.ReturnReady(CreateMesh(key));
        }

        var full = new List<PreviewTerrainChunkMesh>();
        var lod = new List<PreviewTerrainChunkMesh>();
        streamer.DrainReadySplit(
            full,
            lod,
            maxFull: 1,
            maxLod: 0,
            maxFullBytes: 1024,
            maxLodBytes: 1024);

        Assert.Single(full);
        Assert.Empty(lod);
        streamer.NotifyUploaded(full[0].Key, full[0].Lod);
        Assert.Equal(3, streamer.ReadyCount);
        Assert.Equal(3, streamer.InflightCount);

        // Simulate a late duplicate producer while overflow is waiting.
        streamer.ReturnReady(CreateMesh(keys[1]));
        Assert.Equal(3, streamer.ReadyCount);
        Assert.Equal(3, streamer.ReadyUniqueCount);

        var remaining = new List<PreviewTerrainChunkMesh>();
        streamer.DrainReady(remaining, 8);
        Assert.Equal(3, remaining.Count);
        Assert.Equal(3, remaining.Select(mesh => mesh.Key).Distinct().Count());
        Assert.Equal(0, streamer.ReadyCount);
        Assert.Equal(0, streamer.ReadyUniqueCount);

        foreach (var mesh in remaining)
        {
            streamer.NotifyUploaded(mesh.Key, mesh.Lod);
        }

        Assert.Equal(0, streamer.InflightCount);
    }

    private static PreviewTerrainChunkMesh CreateMesh(TerrainResidencyKey key) => new()
    {
        Key = key,
        Lod = key.Kind,
        InterleavedVertices = new float[12],
        Indices = [0, 0, 0],
    };
}
