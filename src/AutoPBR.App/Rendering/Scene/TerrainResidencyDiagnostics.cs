namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// Readable breakdown of terrain residency so logs can distinguish real GPU meshes from
/// budget fake-parks and soft-start unlock progress (e.g. 8 vs 1673).
/// </summary>
public readonly record struct TerrainResidencyDiagCounts(
    int GpuResident,
    int FakeParked,
    int UnlockedDesired,
    int DesiredTotal,
    int DeferredRetry,
    int ScheduleMaxRing)
{
    public string Format() =>
        $"gpuResident={GpuResident}, fakeParked={FakeParked}, unlockedDesired={UnlockedDesired}, " +
        $"desiredTotal={DesiredTotal}, deferredRetry={DeferredRetry}, scheduleMax={ScheduleMaxRing}";
}

public static class TerrainResidencyDiagnostics
{
    public static TerrainResidencyDiagCounts Count(
        IReadOnlyDictionary<TerrainResidencyKey, TerrainChunkLodKind> desired,
        IReadOnlyCollection<TerrainResidencyKey> gpuResidentKeys,
        IReadOnlyCollection<TerrainResidencyKey> deferredKeys,
        Func<TerrainResidencyKey, bool> isStreamerResident,
        TerrainChunkKey cameraChunk,
        int scheduleMaxRing)
    {
        var gpu = gpuResidentKeys is HashSet<TerrainResidencyKey> gpuSet
            ? gpuSet
            : gpuResidentKeys.ToHashSet();

        var fakeParked = 0;
        var deferredRetry = 0;
        foreach (var key in deferredKeys)
        {
            if (gpu.Contains(key))
            {
                continue;
            }

            if (isStreamerResident(key))
            {
                fakeParked++;
            }
            else
            {
                deferredRetry++;
            }
        }

        var unlocked = 0;
        foreach (var key in desired.Keys)
        {
            if (key.IsFull ||
                TerrainStreamSchedule.RingIndex(key, cameraChunk) <= scheduleMaxRing)
            {
                unlocked++;
            }
        }

        return new TerrainResidencyDiagCounts(
            GpuResident: gpu.Count,
            FakeParked: fakeParked,
            UnlockedDesired: unlocked,
            DesiredTotal: desired.Count,
            DeferredRetry: deferredRetry,
            ScheduleMaxRing: scheduleMaxRing);
    }
}
