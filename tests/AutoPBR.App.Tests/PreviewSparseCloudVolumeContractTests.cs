using AutoPBR.App.Rendering.Scene;

using System.Runtime.InteropServices;

namespace AutoPBR.App.Tests;

public sealed class PreviewSparseCloudVolumeContractTests
{
    [Fact]
    public void Contract_UsesDecisionCompleteCq42AbiWithinMemoryBudget()
    {
        Assert.Equal(3, PreviewSparseCloudVolumeContract.ClipmapCount);
        Assert.Equal(8, PreviewSparseCloudVolumeContract.LogicalBrickInteriorSize);
        Assert.Equal(1, PreviewSparseCloudVolumeContract.PhysicalBrickBorderSize);
        Assert.Equal(10, PreviewSparseCloudVolumeContract.PhysicalBrickSize);
        Assert.Equal(4096, PreviewSparseCloudVolumeContract.PhysicalBrickCount);
        Assert.Equal(4095, PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount);
        Assert.Equal(160, PreviewSparseCloudVolumeContract.AtlasTexelSize);
        Assert.Equal(8_192_000L, PreviewSparseCloudVolumeContract.AtlasByteLength);

        Assert.Equal(32, PreviewSparseCloudVolumeContract.PageTableWidth);
        Assert.Equal(16, PreviewSparseCloudVolumeContract.PageTableHeight);
        Assert.Equal(32, PreviewSparseCloudVolumeContract.PageTableDepth);
        Assert.Equal(16_384, PreviewSparseCloudVolumeContract.PageTableEntryCount);
        Assert.Equal(32_768L, PreviewSparseCloudVolumeContract.PageTableByteLength);
        Assert.Equal((ushort)0, PreviewSparseCloudVolumeContract.UnmappedPage);
        Assert.Equal((ushort)4095, PreviewSparseCloudVolumeContract.LastMappedPage);
        Assert.Equal(ushort.MaxValue, PreviewSparseCloudVolumeContract.RequestedPage);
        Assert.Equal(44, Marshal.SizeOf<PreviewSparseCloudBrickResidencyRecord>());

        var memory = PreviewSparseCloudVolumeContract.MemoryAccounting;
        Assert.Equal(196_608L, memory.PageTableBytes);
        Assert.Equal(589_824L, memory.TemplateLibraryBytes);
        Assert.Equal(3_276_768L, memory.ClipmapControlReserveBytes);
        Assert.Equal(384L, memory.GenerationStatusBytes);
        Assert.Equal(12_684_604L, memory.TotalBytes);
        Assert.True(memory.IsWithinBudget);
        Assert.True(
            memory.TotalBytes <
            PreviewSparseCloudVolumeContract.MemoryBudgetBytes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(4094, 4095)]
    public void PageEncoding_MappedValuesRoundTrip(
        int physicalBrickIndex,
        ushort expectedPageValue)
    {
        var encoded =
            PreviewSparseCloudVolumeContract.EncodePhysicalBrickIndex(
                physicalBrickIndex);

        Assert.Equal(expectedPageValue, encoded);
        Assert.True(
            PreviewSparseCloudVolumeContract.TryDecodePhysicalBrickIndex(
                encoded,
                out var decoded));
        Assert.Equal(physicalBrickIndex, decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    [InlineData(65535)]
    public void PageEncoding_SentinelOrInvalidValueIsNotDecoded(
        ushort pageValue)
    {
        Assert.False(
            PreviewSparseCloudVolumeContract.TryDecodePhysicalBrickIndex(
                pageValue,
                out var physicalBrickIndex));
        Assert.Equal(-1, physicalBrickIndex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4095)]
    public void PageEncoding_ReservedOrOutOfRangeIndexIsRejected(
        int physicalBrickIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PreviewSparseCloudVolumeContract.EncodePhysicalBrickIndex(
                physicalBrickIndex));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(4094)]
    [InlineData(4095)]
    public void AtlasAddressing_PhysicalBrickIndexRoundTrips(
        int physicalBrickIndex)
    {
        var coordinate =
            PreviewSparseCloudVolumeContract.PhysicalBrickAtlasCoordinate(
                physicalBrickIndex);

        Assert.Equal(
            physicalBrickIndex,
            PreviewSparseCloudVolumeContract.PhysicalBrickIndex(coordinate));
    }

    [Fact]
    public void Allocator_DeduplicatesAndDoesNotRecycleActiveBrick()
    {
        var allocator = new PreviewSparseCloudBrickAllocator();
        var key = new PreviewSparseCloudLogicalBrickKey(1, 7, -2, 13);

        Assert.True(allocator.TryRequest(key, 4, 0.25f, out var requested));
        Assert.Equal(0, requested.PhysicalBrickIndex);
        Assert.Equal(PreviewSparseCloudBrickState.Requested, requested.State);
        Assert.True(allocator.TryRequest(key, 9, 0.75f, out var deduplicated));
        Assert.Equal(requested.PhysicalBrickIndex, deduplicated.PhysicalBrickIndex);
        Assert.Equal(9, deduplicated.LastRequestedFrame);
        Assert.Equal(0.75f, deduplicated.CoveragePriority);
        Assert.Equal(1, allocator.AllocatedCount);

        Assert.True(allocator.MarkGenerating(key));
        Assert.True(allocator.MarkResident(key, 3, 10));
        Assert.True(allocator.SetActiveReferenceCount(key, 1));
        Assert.False(allocator.TryBeginRetire(key));
        Assert.True(allocator.SetActiveReferenceCount(key, 0));
        Assert.True(allocator.TryBeginRetire(key));
        Assert.True(allocator.TryReleaseRetired(key));
        Assert.False(allocator.TryGet(key, out _));
        Assert.Equal(0, allocator.AllocatedCount);
        Assert.Equal(
            PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount,
            allocator.FreeCount);
    }

    [Fact]
    public void Allocator_ReservesClearedFallbackAndReportsBoundedOverflow()
    {
        var allocator = new PreviewSparseCloudBrickAllocator();
        var indices = new HashSet<int>();

        for (var index = 0;
             index < PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount;
             index++)
        {
            var key = new PreviewSparseCloudLogicalBrickKey(
                index % PreviewSparseCloudVolumeContract.ClipmapCount,
                index,
                0,
                0);
            Assert.True(
                allocator.TryRequest(
                    key,
                    frame: 1,
                    coveragePriority: 1f,
                    out var record));
            Assert.True(indices.Add(record.PhysicalBrickIndex));
        }

        Assert.DoesNotContain(
            PreviewSparseCloudVolumeContract.ReservedFallbackPhysicalBrickIndex,
            indices);
        Assert.Equal(0, allocator.FreeCount);
        Assert.False(
            allocator.TryRequest(
                new PreviewSparseCloudLogicalBrickKey(0, int.MaxValue, 0, 0),
                frame: 2,
                coveragePriority: 1f,
                out _));
        Assert.Equal(1, allocator.OverflowCount);
        Assert.Equal(
            PreviewSparseCloudBrickState.ReservedFallback,
            allocator.GetPhysicalRecord(
                PreviewSparseCloudVolumeContract
                    .ReservedFallbackPhysicalBrickIndex).State);
    }
}
