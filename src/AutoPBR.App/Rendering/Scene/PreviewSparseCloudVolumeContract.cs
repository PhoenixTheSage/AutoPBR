using System.Numerics;
using System.Runtime.InteropServices;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Shared CQ4 sparse-density ABI. These constants are the authority for later CPU queue,
/// compute-generation, and GLSL traversal implementations.
/// </summary>
internal static class PreviewSparseCloudVolumeContract
{
    public const int ClipmapCount = 3;
    public const float Level0VoxelWorldSize = 2f;
    public const float Level1VoxelWorldSize = 8f;
    public const float Level2VoxelWorldSize = 32f;
    public const int LogicalBrickInteriorSize = 8;
    public const int PhysicalBrickBorderSize = 1;
    public const int PhysicalBrickSize =
        LogicalBrickInteriorSize + PhysicalBrickBorderSize * 2;
    public const int AtlasBricksPerAxis = 16;
    public const int PhysicalBrickCount =
        AtlasBricksPerAxis * AtlasBricksPerAxis * AtlasBricksPerAxis;
    public const int ReservedFallbackPhysicalBrickIndex =
        PhysicalBrickCount - 1;
    public const int AllocatablePhysicalBrickCount =
        ReservedFallbackPhysicalBrickIndex;
    public const int AtlasTexelSize =
        AtlasBricksPerAxis * PhysicalBrickSize;
    public const int AtlasChannelCount = 2;
    public const int AtlasBytesPerChannel = 1;

    public const int PageTableWidth = 32;
    public const int PageTableHeight = 16;
    public const int PageTableDepth = 32;
    public const int PageTableCountPerClipmap = 2;
    public const int PageTableEntryByteSize = sizeof(ushort);
    public const ushort UnmappedPage = 0;
    public const ushort FirstMappedPage = 1;
    public const ushort LastMappedPage = AllocatablePhysicalBrickCount;
    public const ushort RequestedPage = ushort.MaxValue;

    public const int MaximumEnteringBricksPerFrame = 96;
    /// <summary>
    /// Fast camera motion recenters the clipmap. Submitting a full 96-brick batch
    /// on every origin snap saturates Cinematic GPU time; keep motion bursts small.
    /// </summary>
    public const int OriginChangedEnteringBricksPerFrame = 24;
    /// <summary>
    /// Whole-page-table teleports retire most residency. Rebuild from a tiny
    /// prioritized seed instead of a full generation burst.
    /// </summary>
    public const int TeleportEnteringBricksPerFrame = 12;
    /// <summary>
    /// Pending-queue rebuild Chebyshev radius (pages) around the camera brick.
    /// Full 32×16×32×3 enumeration on every view/origin change is a CPU hitch.
    /// </summary>
    public const int PendingRebuildRadiusL0 = 8;
    public const int PendingRebuildRadiusL1 = 6;
    public const int PendingRebuildRadiusL2 = 5;
    public const int GenerationQueueRecordByteSize = 32;
    public const int ManagedLookupReserveBytesPerBrick = 32;
    public const int ClipmapControlReserveBytesPerLogicalPage = 64;
    public const int ResidentMappingReserveBytesPerBrick = 32;
    public const long MemoryBudgetBytes = 16L * 1024L * 1024L;
    public const long TemplateLibraryBytes = 589_824L;

    public static int PageTableEntryCount =>
        PageTableWidth * PageTableHeight * PageTableDepth;

    public static int LogicalPageCount =>
        PageTableEntryCount * ClipmapCount;

    public static long AtlasByteLength =>
        (long)AtlasTexelSize * AtlasTexelSize * AtlasTexelSize *
        AtlasChannelCount * AtlasBytesPerChannel;

    public static long PageTableByteLength =>
        (long)PageTableEntryCount * PageTableEntryByteSize;

    public static Int3 PageTableDimensions =>
        new(PageTableWidth, PageTableHeight, PageTableDepth);

    public static PreviewSparseCloudMemoryAccounting MemoryAccounting { get; } =
        CreateMemoryAccounting();

    public static ushort EncodePhysicalBrickIndex(int physicalBrickIndex)
    {
        if (physicalBrickIndex < 0 ||
            physicalBrickIndex >= AllocatablePhysicalBrickCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalBrickIndex),
                $"CQ4 mapped physical brick indices must be in 0.." +
                $"{AllocatablePhysicalBrickCount - 1}; slot " +
                $"{ReservedFallbackPhysicalBrickIndex} is reserved.");
        }

        return checked((ushort)(physicalBrickIndex + 1));
    }

    public static bool TryDecodePhysicalBrickIndex(
        ushort pageValue,
        out int physicalBrickIndex)
    {
        if (pageValue < FirstMappedPage || pageValue > LastMappedPage)
        {
            physicalBrickIndex = -1;
            return false;
        }

        physicalBrickIndex = pageValue - 1;
        return true;
    }

    public static Int3 PhysicalBrickAtlasCoordinate(int physicalBrickIndex)
    {
        if (physicalBrickIndex < 0 || physicalBrickIndex >= PhysicalBrickCount)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalBrickIndex));
        }

        var x = physicalBrickIndex % AtlasBricksPerAxis;
        var quotient = physicalBrickIndex / AtlasBricksPerAxis;
        var y = quotient % AtlasBricksPerAxis;
        var z = quotient / AtlasBricksPerAxis;
        return new Int3(x, y, z);
    }

    public static int PhysicalBrickIndex(Int3 atlasBrickCoordinate)
    {
        if (atlasBrickCoordinate.X < 0 ||
            atlasBrickCoordinate.X >= AtlasBricksPerAxis ||
            atlasBrickCoordinate.Y < 0 ||
            atlasBrickCoordinate.Y >= AtlasBricksPerAxis ||
            atlasBrickCoordinate.Z < 0 ||
            atlasBrickCoordinate.Z >= AtlasBricksPerAxis)
        {
            throw new ArgumentOutOfRangeException(nameof(atlasBrickCoordinate));
        }

        return (atlasBrickCoordinate.Z * AtlasBricksPerAxis +
                atlasBrickCoordinate.Y) *
               AtlasBricksPerAxis +
               atlasBrickCoordinate.X;
    }

    public static float VoxelWorldSize(int clipmapLevel) =>
        clipmapLevel switch
        {
            0 => Level0VoxelWorldSize,
            1 => Level1VoxelWorldSize,
            2 => Level2VoxelWorldSize,
            _ => throw new ArgumentOutOfRangeException(nameof(clipmapLevel)),
        };

    public static float BrickWorldSize(int clipmapLevel) =>
        VoxelWorldSize(clipmapLevel) * LogicalBrickInteriorSize;

    public static Int3 WorldCoverage(int clipmapLevel)
    {
        var voxelWorldSize = checked((int)VoxelWorldSize(clipmapLevel));
        return new Int3(
            PageTableWidth * LogicalBrickInteriorSize * voxelWorldSize,
            PageTableHeight * LogicalBrickInteriorSize * voxelWorldSize,
            PageTableDepth * LogicalBrickInteriorSize * voxelWorldSize);
    }

    public static int PageTableLinearIndex(Int3 localBrickCoordinate)
    {
        if (localBrickCoordinate.X < 0 ||
            localBrickCoordinate.X >= PageTableWidth ||
            localBrickCoordinate.Y < 0 ||
            localBrickCoordinate.Y >= PageTableHeight ||
            localBrickCoordinate.Z < 0 ||
            localBrickCoordinate.Z >= PageTableDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(localBrickCoordinate));
        }

        return (localBrickCoordinate.Z * PageTableHeight +
                localBrickCoordinate.Y) *
               PageTableWidth +
               localBrickCoordinate.X;
    }

    public static Int3 PageTableCoordinate(int linearIndex)
    {
        if (linearIndex < 0 || linearIndex >= PageTableEntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(linearIndex));
        }

        var x = linearIndex % PageTableWidth;
        var quotient = linearIndex / PageTableWidth;
        var y = quotient % PageTableHeight;
        var z = quotient / PageTableHeight;
        return new Int3(x, y, z);
    }

    private static PreviewSparseCloudMemoryAccounting CreateMemoryAccounting()
    {
        var atlas = AtlasByteLength;
        var pageTables =
            PageTableByteLength * ClipmapCount * PageTableCountPerClipmap;
        var residency =
            (long)Marshal.SizeOf<PreviewSparseCloudBrickResidencyRecord>() *
            PhysicalBrickCount;
        var freeIndices =
            (long)AllocatablePhysicalBrickCount * sizeof(int);
        var buildStaging =
            PageTableByteLength * ClipmapCount;
        var generationQueue =
            (long)MaximumEnteringBricksPerFrame *
            GenerationQueueRecordByteSize;
        var generationStatus =
            (long)MaximumEnteringBricksPerFrame * sizeof(uint);
        var managedLookup =
            (long)AllocatablePhysicalBrickCount *
            ManagedLookupReserveBytesPerBrick;
        var clipmapControl =
            (long)LogicalPageCount *
            ClipmapControlReserveBytesPerLogicalPage +
            (long)AllocatablePhysicalBrickCount *
            ResidentMappingReserveBytesPerBrick;
        return new PreviewSparseCloudMemoryAccounting(
            atlas,
            pageTables,
            residency,
            freeIndices,
            buildStaging,
            generationQueue,
            generationStatus,
            managedLookup,
            clipmapControl,
            TemplateLibraryBytes);
    }
}

internal readonly record struct PreviewSparseCloudMemoryAccounting(
    long AtlasBytes,
    long PageTableBytes,
    long ResidencyRecordBytes,
    long FreeIndexBytes,
    long BuildTableStagingBytes,
    long GenerationQueueBytes,
    long GenerationStatusBytes,
    long ManagedLookupReserveBytes,
    long ClipmapControlReserveBytes,
    long TemplateLibraryBytes)
{
    public long TotalBytes =>
        AtlasBytes +
        PageTableBytes +
        ResidencyRecordBytes +
        FreeIndexBytes +
        BuildTableStagingBytes +
        GenerationQueueBytes +
        GenerationStatusBytes +
        ManagedLookupReserveBytes +
        ClipmapControlReserveBytes +
        TemplateLibraryBytes;

    public bool IsWithinBudget =>
        TotalBytes <= PreviewSparseCloudVolumeContract.MemoryBudgetBytes;

    public string FormatDiagnostic() =>
        $"atlas={AtlasBytes};pageTables={PageTableBytes};" +
        $"residency={ResidencyRecordBytes};freeIndices={FreeIndexBytes};" +
        $"buildStaging={BuildTableStagingBytes};queue={GenerationQueueBytes};" +
        $"generationStatus={GenerationStatusBytes};" +
        $"lookupReserve={ManagedLookupReserveBytes};" +
        $"clipmapControlReserve={ClipmapControlReserveBytes};" +
        $"templates={TemplateLibraryBytes};" +
        $"total={TotalBytes}/" +
        $"{PreviewSparseCloudVolumeContract.MemoryBudgetBytes};" +
        $"withinBudget={IsWithinBudget}";
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PreviewSparseCloudBrickResidencyRecord
{
    public int ClipmapLevel;
    public int LogicalX;
    public int LogicalY;
    public int LogicalZ;
    public int PhysicalBrickIndex;
    public int LastRequestedFrame;
    public int LastVisibleFrame;
    public int GenerationId;
    public PreviewSparseCloudBrickState State;
    public float CoveragePriority;
    public int ActiveReferenceCount;

    public readonly PreviewSparseCloudLogicalBrickKey Key =>
        new(ClipmapLevel, LogicalX, LogicalY, LogicalZ);
}

internal enum PreviewSparseCloudBrickState
{
    Free = 0,
    Requested = 1,
    Generating = 2,
    Resident = 3,
    Retiring = 4,
    ReservedFallback = 5,
}

internal readonly record struct PreviewSparseCloudLogicalBrickKey(
    int ClipmapLevel,
    int X,
    int Y,
    int Z)
{
    public bool IsValid =>
        ClipmapLevel >= 0 &&
        ClipmapLevel < PreviewSparseCloudVolumeContract.ClipmapCount;
}

internal readonly record struct Int3(int X, int Y, int Z)
{
    public Vector3 ToVector3() => new(X, Y, Z);
}
