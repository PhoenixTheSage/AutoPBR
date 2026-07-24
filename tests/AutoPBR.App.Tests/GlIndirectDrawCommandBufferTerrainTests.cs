using AutoPBR.App.Rendering.OpenGL;
using AutoPBR.Preview;

namespace AutoPBR.App.Tests;

public sealed class GlIndirectDrawCommandBufferTerrainTests
{
    [Fact]
    public void WriteCommandDwords_Raw_PacksCountFirstIndexAndBaseInstance()
    {
        Span<uint> dst = stackalloc uint[GlIndirectDrawCommandBuffer.CommandDwords];
        GlIndirectDrawCommandBuffer.WriteCommandDwords(dst, indexCount: 12u, firstIndex: 40u, baseInstance: 3u);
        Assert.Equal(12u, dst[0]);
        Assert.Equal(1u, dst[1]);
        Assert.Equal(40u, dst[2]);
        Assert.Equal(0u, dst[3]);
        Assert.Equal(3u, dst[4]);
    }

    [Fact]
    public void RemapBatchesToPool_OffsetsFirstIndexByAllocation()
    {
        // Mirror GroundTerrain.RemapBatchesToPool logic for regression coverage without GL.
        PreviewDrawBatch[] source =
        [
            new(0, 30, 0),
            new(30, 12, 2) { BoundsRadius = 1f },
        ];
        const int indexOffset = 1000;
        var remapped = new PreviewDrawBatch[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            remapped[i] = source[i] with { FirstIndex = indexOffset + source[i].FirstIndex };
        }

        Assert.Equal(1000, remapped[0].FirstIndex);
        Assert.Equal(30, remapped[0].IndexCount);
        Assert.Equal(1030, remapped[1].FirstIndex);
        Assert.Equal(2, remapped[1].MaterialIndex);
        Assert.Equal(1f, remapped[1].BoundsRadius);
    }

    [Fact]
    public void TerrainShadowSourceCommand_PacksPoolRangeForGpuCull()
    {
        // Source command ABI consumed by genesis_terrain_shadow_cull.comp (one draw per chunk).
        Span<uint> dst = stackalloc uint[GlIndirectDrawCommandBuffer.CommandDwords];
        GlIndirectDrawCommandBuffer.WriteCommandDwords(
            dst,
            indexCount: 240u,
            firstIndex: 4800u,
            baseInstance: 0u);
        Assert.Equal(240u, dst[0]);
        Assert.Equal(1u, dst[1]);
        Assert.Equal(4800u, dst[2]);
        Assert.Equal(0u, dst[4]);
    }
}
