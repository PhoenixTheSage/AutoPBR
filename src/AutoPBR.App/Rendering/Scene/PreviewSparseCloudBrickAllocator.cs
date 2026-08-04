namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CQ4.2 CPU ownership model. It never recycles a brick referenced by an active page table and
/// permanently excludes the cleared fallback atlas slot from mapped allocations.
/// </summary>
internal sealed class PreviewSparseCloudBrickAllocator
{
    private readonly PreviewSparseCloudBrickResidencyRecord[] _records =
        new PreviewSparseCloudBrickResidencyRecord[
            PreviewSparseCloudVolumeContract.PhysicalBrickCount];
    private readonly Stack<int> _freeIndices =
        new(PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount);
    private readonly Dictionary<PreviewSparseCloudLogicalBrickKey, int> _lookup =
        new(PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount);

    public PreviewSparseCloudBrickAllocator()
    {
        AccountedByteLength =
            PreviewSparseCloudVolumeContract.MemoryAccounting.ResidencyRecordBytes +
            PreviewSparseCloudVolumeContract.MemoryAccounting.FreeIndexBytes +
            PreviewSparseCloudVolumeContract.MemoryAccounting.ManagedLookupReserveBytes;
        for (var index = 0;
             index < PreviewSparseCloudVolumeContract.PhysicalBrickCount;
             index++)
        {
            _records[index] = CreateFreeRecord(index);
        }

        _records[
            PreviewSparseCloudVolumeContract.ReservedFallbackPhysicalBrickIndex]
            .State = PreviewSparseCloudBrickState.ReservedFallback;
        for (var index =
                 PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount - 1;
             index >= 0;
             index--)
        {
            _freeIndices.Push(index);
        }
    }

    public int AllocatedCount => _lookup.Count;
    public int FreeCount => _freeIndices.Count;
    public int OverflowCount { get; private set; }
    public int RecycledCount { get; private set; }
    public int GeneratingCount { get; private set; }
    public int ResidentRecordCount { get; private set; }
    public long AccountedByteLength { get; }

    public bool TryRequest(
        PreviewSparseCloudLogicalBrickKey key,
        int frame,
        float coveragePriority,
        out PreviewSparseCloudBrickResidencyRecord record)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        if (_lookup.TryGetValue(key, out var existingIndex))
        {
            var existing = _records[existingIndex];
            existing.LastRequestedFrame = Math.Max(existing.LastRequestedFrame, frame);
            existing.CoveragePriority = Math.Max(
                existing.CoveragePriority,
                coveragePriority);
            _records[existingIndex] = existing;
            record = existing;
            return true;
        }

        if (!_freeIndices.TryPop(out var physicalBrickIndex))
        {
            OverflowCount = OverflowCount == int.MaxValue
                ? int.MaxValue
                : OverflowCount + 1;
            record = default;
            return false;
        }

        record = new PreviewSparseCloudBrickResidencyRecord
        {
            ClipmapLevel = key.ClipmapLevel,
            LogicalX = key.X,
            LogicalY = key.Y,
            LogicalZ = key.Z,
            PhysicalBrickIndex = physicalBrickIndex,
            LastRequestedFrame = frame,
            LastVisibleFrame = -1,
            GenerationId = 0,
            State = PreviewSparseCloudBrickState.Requested,
            CoveragePriority = coveragePriority,
            ActiveReferenceCount = 0,
        };
        _records[physicalBrickIndex] = record;
        _lookup.Add(key, physicalBrickIndex);
        return true;
    }

    public bool TryGet(
        PreviewSparseCloudLogicalBrickKey key,
        out PreviewSparseCloudBrickResidencyRecord record)
    {
        if (_lookup.TryGetValue(key, out var index))
        {
            record = _records[index];
            return true;
        }

        record = default;
        return false;
    }

    public bool MarkGenerating(PreviewSparseCloudLogicalBrickKey key)
    {
        if (!TryGetIndex(key, out var index) ||
            _records[index].State != PreviewSparseCloudBrickState.Requested)
        {
            return false;
        }

        _records[index].State = PreviewSparseCloudBrickState.Generating;
        GeneratingCount++;
        return true;
    }

    public bool MarkResident(
        PreviewSparseCloudLogicalBrickKey key,
        int generationId,
        int visibleFrame)
    {
        if (!TryGetIndex(key, out var index) ||
            _records[index].State != PreviewSparseCloudBrickState.Generating)
        {
            return false;
        }

        _records[index].State = PreviewSparseCloudBrickState.Resident;
        _records[index].GenerationId = Math.Max(0, generationId);
        _records[index].LastVisibleFrame = Math.Max(
            _records[index].LastVisibleFrame,
            visibleFrame);
        GeneratingCount = Math.Max(0, GeneratingCount - 1);
        ResidentRecordCount++;
        return true;
    }

    public bool SetActiveReferenceCount(
        PreviewSparseCloudLogicalBrickKey key,
        int activeReferenceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeReferenceCount);
        if (!TryGetIndex(key, out var index))
        {
            return false;
        }

        _records[index].ActiveReferenceCount = activeReferenceCount;
        return true;
    }

    public bool TryBeginRetire(PreviewSparseCloudLogicalBrickKey key)
    {
        if (!TryGetIndex(key, out var index) ||
            _records[index].State != PreviewSparseCloudBrickState.Resident ||
            _records[index].ActiveReferenceCount != 0)
        {
            return false;
        }

        _records[index].State = PreviewSparseCloudBrickState.Retiring;
        return true;
    }

    public bool TryReleaseRetired(PreviewSparseCloudLogicalBrickKey key)
    {
        if (!TryGetIndex(key, out var index) ||
            _records[index].State != PreviewSparseCloudBrickState.Retiring ||
            _records[index].ActiveReferenceCount != 0)
        {
            return false;
        }

        ReleasePhysicalSlot(key, index, wasResident: true);
        return true;
    }

    /// <summary>
    /// CQ4.7: recycle a brick that the clipmap no longer wants and that is not referenced by the
    /// published active page tables. Generating bricks must wait for their fence; callers then
    /// <see cref="MarkResident"/> and recycle through this path or <see cref="TryBeginRetire"/>.
    /// </summary>
    public bool TryRecycleUnreferenced(PreviewSparseCloudLogicalBrickKey key)
    {
        if (!TryGetIndex(key, out var index))
        {
            return false;
        }

        var record = _records[index];
        if (record.ActiveReferenceCount != 0 ||
            record.State == PreviewSparseCloudBrickState.Generating ||
            record.State == PreviewSparseCloudBrickState.ReservedFallback ||
            record.State == PreviewSparseCloudBrickState.Free)
        {
            return false;
        }

        if (record.State == PreviewSparseCloudBrickState.Retiring)
        {
            return TryReleaseRetired(key);
        }

        if (record.State == PreviewSparseCloudBrickState.Resident)
        {
            if (!TryBeginRetire(key))
            {
                return false;
            }

            return TryReleaseRetired(key);
        }

        if (record.State != PreviewSparseCloudBrickState.Requested)
        {
            return false;
        }

        ReleasePhysicalSlot(key, index, wasResident: false);
        return true;
    }

    /// <summary>
    /// CQ4.7: after a generation fence completes for a brick the controller already retired,
    /// mark it resident briefly with zero active references and recycle the physical slot.
    /// </summary>
    public bool TryRecycleOrphanedGeneration(
        PreviewSparseCloudLogicalBrickKey key,
        int generationId,
        int visibleFrame)
    {
        if (!MarkResident(key, generationId, visibleFrame))
        {
            return false;
        }

        if (!SetActiveReferenceCount(key, 0))
        {
            return false;
        }

        return TryRecycleUnreferenced(key);
    }

    public int SyncActiveReferences(IReadOnlySet<int> publishedPhysicalBrickIndices)
    {
        ArgumentNullException.ThrowIfNull(publishedPhysicalBrickIndices);
        var updated = 0;
        foreach (var pair in _lookup)
        {
            var index = pair.Value;
            var record = _records[index];
            if (record.State is not (
                    PreviewSparseCloudBrickState.Resident or
                    PreviewSparseCloudBrickState.Retiring))
            {
                continue;
            }

            var nextCount = publishedPhysicalBrickIndices.Contains(index) ? 1 : 0;
            if (record.ActiveReferenceCount == nextCount)
            {
                continue;
            }

            _records[index].ActiveReferenceCount = nextCount;
            updated++;
        }

        return updated;
    }

    public int ReleaseEligibleRetired()
    {
        var released = 0;
        var keys = _lookup.Keys.ToArray();
        foreach (var key in keys)
        {
            if (!TryGetIndex(key, out var index))
            {
                continue;
            }

            var record = _records[index];
            if (record.State == PreviewSparseCloudBrickState.Retiring &&
                record.ActiveReferenceCount == 0 &&
                TryReleaseRetired(key))
            {
                released++;
            }
        }

        return released;
    }

    public PreviewSparseCloudBrickResidencyRecord GetPhysicalRecord(
        int physicalBrickIndex)
    {
        if (physicalBrickIndex < 0 ||
            physicalBrickIndex >= _records.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalBrickIndex));
        }

        return _records[physicalBrickIndex];
    }

    public string FormatDiagnostic() =>
        $"allocated={AllocatedCount};free={FreeCount};" +
        $"generating={GeneratingCount};resident={ResidentRecordCount};" +
        $"reserved=1;capacity=" +
        $"{PreviewSparseCloudVolumeContract.AllocatablePhysicalBrickCount};" +
        $"overflow={OverflowCount};recycled={RecycledCount};" +
        $"cpuAccounted={AccountedByteLength}";

    private void ReleasePhysicalSlot(
        PreviewSparseCloudLogicalBrickKey key,
        int index,
        bool wasResident)
    {
        var prior = _records[index];
        if (prior.State == PreviewSparseCloudBrickState.Generating)
        {
            GeneratingCount = Math.Max(0, GeneratingCount - 1);
        }

        if (wasResident ||
            prior.State is PreviewSparseCloudBrickState.Resident or
                PreviewSparseCloudBrickState.Retiring)
        {
            ResidentRecordCount = Math.Max(0, ResidentRecordCount - 1);
        }

        _lookup.Remove(key);
        _records[index] = CreateFreeRecord(index);
        _freeIndices.Push(index);
        RecycledCount = RecycledCount == int.MaxValue
            ? int.MaxValue
            : RecycledCount + 1;
    }

    private bool TryGetIndex(
        PreviewSparseCloudLogicalBrickKey key,
        out int physicalBrickIndex) =>
        _lookup.TryGetValue(key, out physicalBrickIndex);

    private static PreviewSparseCloudBrickResidencyRecord CreateFreeRecord(
        int physicalBrickIndex) =>
        new()
        {
            ClipmapLevel = -1,
            LogicalX = 0,
            LogicalY = 0,
            LogicalZ = 0,
            PhysicalBrickIndex = physicalBrickIndex,
            LastRequestedFrame = -1,
            LastVisibleFrame = -1,
            GenerationId = 0,
            State = PreviewSparseCloudBrickState.Free,
            CoveragePriority = 0f,
            ActiveReferenceCount = 0,
        };
}
