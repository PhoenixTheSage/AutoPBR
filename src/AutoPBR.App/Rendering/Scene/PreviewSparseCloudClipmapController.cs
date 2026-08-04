using System.Numerics;

namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// CQ4.3 CPU control plane. It owns snapped logical footprints, deterministic entering-page
/// priority, and complete build-table staging without making sparse density sampleable.
/// </summary>
internal sealed class PreviewSparseCloudClipmapController
{
    private const float ViewReprioritizationCosine = 0.9659258f;

    private readonly Int3[] _origins =
        new Int3[PreviewSparseCloudVolumeContract.ClipmapCount];
    private readonly ushort[][] _buildTables =
        Enumerable.Range(0, PreviewSparseCloudVolumeContract.ClipmapCount)
            .Select(_ => new ushort[
                PreviewSparseCloudVolumeContract.PageTableEntryCount])
            .ToArray();
    private readonly HashSet<PreviewSparseCloudLogicalBrickKey> _requested = [];
    private readonly Dictionary<PreviewSparseCloudLogicalBrickKey, int>
        _residentMappings = [];
    private readonly PriorityQueue<
        PreviewSparseCloudLogicalBrickKey,
        PreviewSparseCloudRequestPriority> _pending = new();
    private bool _initialized;
    private Vector3 _priorityViewDirection = Vector3.UnitZ;
    private int _tableRevision;
    private int _recenterCount;
    private int _teleportCount;

    public PreviewSparseCloudClipmapController()
    {
        AccountedByteLength =
            PreviewSparseCloudVolumeContract.MemoryAccounting
                .BuildTableStagingBytes +
            PreviewSparseCloudVolumeContract.MemoryAccounting
                .ClipmapControlReserveBytes;
    }

    public int TableRevision => _tableRevision;
    public int RequestedCount => _requested.Count;
    public int ResidentCount => _residentMappings.Count;
    public int PendingCount => _pending.Count;
    public int RecenterCount => _recenterCount;
    public int TeleportCount => _teleportCount;
    public long AccountedByteLength { get; }

    public Int3 GetOrigin(int clipmapLevel)
    {
        ValidateClipmapLevel(clipmapLevel);
        return _origins[clipmapLevel];
    }

    public ReadOnlySpan<ushort> GetBuildTable(int clipmapLevel)
    {
        ValidateClipmapLevel(clipmapLevel);
        return _buildTables[clipmapLevel];
    }

    public PreviewSparseCloudClipmapUpdate Update(
        Vector3 cameraWorldPosition,
        Vector3 viewDirection,
        float cloudVerticalCenterWorldY,
        ReadOnlySpan<Vector4> frustumPlanes,
        int frame,
        int maximumEntering =
            PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame)
    {
        if (maximumEntering < 0 ||
            maximumEntering >
            PreviewSparseCloudVolumeContract.MaximumEnteringBricksPerFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntering));
        }

        if (!IsFinite(cameraWorldPosition) ||
            !float.IsFinite(cloudVerticalCenterWorldY))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cameraWorldPosition),
                "Sparse clipmap anchors must be finite.");
        }

        var safeViewDirection = NormalizeOrDefault(viewDirection, Vector3.UnitZ);
        var nextOrigins = new Int3[PreviewSparseCloudVolumeContract.ClipmapCount];
        var originChanged = !_initialized;
        var teleport = false;
        for (var level = 0;
             level < PreviewSparseCloudVolumeContract.ClipmapCount;
             level++)
        {
            nextOrigins[level] = ComputeSnappedOrigin(
                cameraWorldPosition,
                cloudVerticalCenterWorldY,
                level);
            if (_initialized && nextOrigins[level] != _origins[level])
            {
                originChanged = true;
                var delta = Subtract(nextOrigins[level], _origins[level]);
                teleport |= Math.Abs(delta.X) >=
                                PreviewSparseCloudVolumeContract.PageTableWidth ||
                            Math.Abs(delta.Y) >=
                                PreviewSparseCloudVolumeContract.PageTableHeight ||
                            Math.Abs(delta.Z) >=
                                PreviewSparseCloudVolumeContract.PageTableDepth;
            }
        }

        var retired = new List<PreviewSparseCloudLogicalBrickKey>();
        if (originChanged)
        {
            if (_initialized)
            {
                _recenterCount++;
                if (teleport)
                {
                    _teleportCount++;
                }
            }

            nextOrigins.CopyTo(_origins, 0);
            foreach (var key in _requested)
            {
                if (!Contains(key))
                {
                    retired.Add(key);
                }
            }

            foreach (var key in retired)
            {
                _requested.Remove(key);
                _residentMappings.Remove(key);
            }
        }

        var viewChanged =
            Vector3.Dot(_priorityViewDirection, safeViewDirection) <
            ViewReprioritizationCosine;
        if (!_initialized || originChanged || viewChanged)
        {
            _priorityViewDirection = safeViewDirection;
            RebuildPending(
                cameraWorldPosition,
                cloudVerticalCenterWorldY,
                frustumPlanes);
        }

        // First initialization may take the full budget so residency can warm.
        // Subsequent origin snaps / teleports must stay small or Cinematic hitching
        // from brick generation + light-cache identity churn dominates the frame.
        if (_initialized)
        {
            if (teleport)
            {
                maximumEntering = Math.Min(
                    maximumEntering,
                    PreviewSparseCloudVolumeContract
                        .TeleportEnteringBricksPerFrame);
            }
            else if (originChanged)
            {
                maximumEntering = Math.Min(
                    maximumEntering,
                    PreviewSparseCloudVolumeContract
                        .OriginChangedEnteringBricksPerFrame);
            }
        }

        _initialized = true;
        var entering = new List<PreviewSparseCloudLogicalBrickKey>(
            maximumEntering);
        while (entering.Count < maximumEntering &&
               _pending.TryDequeue(out var key, out _))
        {
            if (!Contains(key) || !_requested.Add(key))
            {
                continue;
            }

            entering.Add(key);
        }

        if (originChanged || retired.Count > 0 || entering.Count > 0)
        {
            RebuildTables();
            _tableRevision =
                _tableRevision == int.MaxValue ? 1 : _tableRevision + 1;
        }

        return new PreviewSparseCloudClipmapUpdate(
            frame,
            _tableRevision,
            originChanged,
            teleport,
            entering,
            retired,
            _requested.Count,
            _pending.Count);
    }

    public ushort GetPageValue(PreviewSparseCloudLogicalBrickKey key)
    {
        if (!TryGetLocalCoordinate(key, out var local))
        {
            return PreviewSparseCloudVolumeContract.UnmappedPage;
        }

        return _buildTables[key.ClipmapLevel][
            PreviewSparseCloudVolumeContract.PageTableLinearIndex(local)];
    }

    public bool TryMarkResident(
        PreviewSparseCloudLogicalBrickKey key,
        int physicalBrickIndex)
    {
        _ = PreviewSparseCloudVolumeContract.EncodePhysicalBrickIndex(
            physicalBrickIndex);
        if (!_requested.Contains(key) ||
            !Contains(key))
        {
            return false;
        }

        if (_residentMappings.TryGetValue(key, out var current) &&
            current == physicalBrickIndex)
        {
            return true;
        }

        _residentMappings[key] = physicalBrickIndex;
        RebuildTables();
        _tableRevision =
            _tableRevision == int.MaxValue ? 1 : _tableRevision + 1;
        return true;
    }

    public string FormatDiagnostic() =>
        $"revision={TableRevision};requested={RequestedCount};" +
        $"resident={ResidentCount};" +
        $"pending={PendingCount};recenters={RecenterCount};" +
        $"teleports={TeleportCount};cpuAccounted={AccountedByteLength};" +
        "origins=" +
        string.Join(
            "/",
            _origins.Select(
                (origin, level) =>
                    $"L{level}({origin.X},{origin.Y},{origin.Z})"));

    public IEnumerable<KeyValuePair<PreviewSparseCloudLogicalBrickKey, int>>
        EnumerateResidentMappings() =>
        _residentMappings;

    private void RebuildPending(
        Vector3 cameraWorldPosition,
        float cloudVerticalCenterWorldY,
        ReadOnlySpan<Vector4> frustumPlanes)
    {
        _pending.Clear();
        for (var level = 0;
             level < PreviewSparseCloudVolumeContract.ClipmapCount;
             level++)
        {
            var origin = _origins[level];
            var brickWorldSize =
                PreviewSparseCloudVolumeContract.BrickWorldSize(level);
            // XZ follow the camera; Y follows the cloud slab so ground-level cameras
            // still prioritize the volume that actually contains density.
            var focusBrick = new Int3(
                FloorToInt(cameraWorldPosition.X / brickWorldSize),
                FloorToInt(cloudVerticalCenterWorldY / brickWorldSize),
                FloorToInt(cameraWorldPosition.Z / brickWorldSize));
            var radius = PendingRebuildRadius(level);
            var minX = Math.Max(0, focusBrick.X - origin.X - radius);
            var maxX = Math.Min(
                PreviewSparseCloudVolumeContract.PageTableWidth - 1,
                focusBrick.X - origin.X + radius);
            var minY = Math.Max(0, focusBrick.Y - origin.Y - radius);
            var maxY = Math.Min(
                PreviewSparseCloudVolumeContract.PageTableHeight - 1,
                focusBrick.Y - origin.Y + radius);
            var minZ = Math.Max(0, focusBrick.Z - origin.Z - radius);
            var maxZ = Math.Min(
                PreviewSparseCloudVolumeContract.PageTableDepth - 1,
                focusBrick.Z - origin.Z + radius);
            for (var z = minZ; z <= maxZ; z++)
            {
                for (var y = minY; y <= maxY; y++)
                {
                    for (var x = minX; x <= maxX; x++)
                    {
                        var key = new PreviewSparseCloudLogicalBrickKey(
                            level,
                            origin.X + x,
                            origin.Y + y,
                            origin.Z + z);
                        if (_requested.Contains(key))
                        {
                            continue;
                        }

                        _pending.Enqueue(
                            key,
                            CreatePriority(
                                key,
                                cameraWorldPosition,
                                frustumPlanes));
                    }
                }
            }
        }
    }

    private static int PendingRebuildRadius(int clipmapLevel) =>
        clipmapLevel switch
        {
            0 => PreviewSparseCloudVolumeContract.PendingRebuildRadiusL0,
            1 => PreviewSparseCloudVolumeContract.PendingRebuildRadiusL1,
            _ => PreviewSparseCloudVolumeContract.PendingRebuildRadiusL2,
        };

    private static PreviewSparseCloudRequestPriority CreatePriority(
        PreviewSparseCloudLogicalBrickKey key,
        Vector3 cameraWorldPosition,
        ReadOnlySpan<Vector4> frustumPlanes)
    {
        var brickWorldSize =
            PreviewSparseCloudVolumeContract.BrickWorldSize(key.ClipmapLevel);
        var center = new Vector3(
            (key.X + 0.5f) * brickWorldSize,
            (key.Y + 0.5f) * brickWorldSize,
            (key.Z + 0.5f) * brickWorldSize);
        var cameraInside =
            key.ClipmapLevel == 0 &&
            key.X == FloorToInt(cameraWorldPosition.X / brickWorldSize) &&
            key.Y == FloorToInt(cameraWorldPosition.Y / brickWorldSize) &&
            key.Z == FloorToInt(cameraWorldPosition.Z / brickWorldSize);
        var half = brickWorldSize * 0.5f;
        var visible =
            frustumPlanes.Length >= 6 &&
            SphereIntersects(
                frustumPlanes,
                center,
                half * MathF.Sqrt(3f));
        var bucket = cameraInside
            ? 0
            : visible
                ? 1 + key.ClipmapLevel
                : 4 + key.ClipmapLevel;
        var delta = center - cameraWorldPosition;
        var distanceSquared = Vector3.Dot(delta, delta);
        return new PreviewSparseCloudRequestPriority(
            bucket,
            distanceSquared,
            key.ClipmapLevel,
            key.X,
            key.Y,
            key.Z);
    }

    private void RebuildTables()
    {
        foreach (var table in _buildTables)
        {
            Array.Clear(table);
        }

        foreach (var key in _requested)
        {
            if (!TryGetLocalCoordinate(key, out var local))
            {
                continue;
            }

            _buildTables[key.ClipmapLevel][
                PreviewSparseCloudVolumeContract.PageTableLinearIndex(local)] =
                _residentMappings.TryGetValue(
                    key,
                    out var physicalBrickIndex)
                    ? PreviewSparseCloudVolumeContract
                        .EncodePhysicalBrickIndex(physicalBrickIndex)
                    : PreviewSparseCloudVolumeContract.RequestedPage;
        }
    }

    private bool Contains(PreviewSparseCloudLogicalBrickKey key) =>
        TryGetLocalCoordinate(key, out _);

    private bool TryGetLocalCoordinate(
        PreviewSparseCloudLogicalBrickKey key,
        out Int3 local)
    {
        if (!key.IsValid)
        {
            local = default;
            return false;
        }

        var origin = _origins[key.ClipmapLevel];
        local = new Int3(
            key.X - origin.X,
            key.Y - origin.Y,
            key.Z - origin.Z);
        return local.X >= 0 &&
               local.X < PreviewSparseCloudVolumeContract.PageTableWidth &&
               local.Y >= 0 &&
               local.Y < PreviewSparseCloudVolumeContract.PageTableHeight &&
               local.Z >= 0 &&
               local.Z < PreviewSparseCloudVolumeContract.PageTableDepth;
    }

    private static Int3 ComputeSnappedOrigin(
        Vector3 cameraWorldPosition,
        float verticalCenterWorldY,
        int clipmapLevel)
    {
        var brickWorldSize =
            PreviewSparseCloudVolumeContract.BrickWorldSize(clipmapLevel);
        return new Int3(
            FloorToInt(cameraWorldPosition.X / brickWorldSize) -
            PreviewSparseCloudVolumeContract.PageTableWidth / 2,
            FloorToInt(verticalCenterWorldY / brickWorldSize) -
            PreviewSparseCloudVolumeContract.PageTableHeight / 2,
            FloorToInt(cameraWorldPosition.Z / brickWorldSize) -
            PreviewSparseCloudVolumeContract.PageTableDepth / 2);
    }

    private static bool SphereIntersects(
        ReadOnlySpan<Vector4> planes,
        Vector3 center,
        float radius)
    {
        for (var index = 0; index < 6; index++)
        {
            var plane = planes[index];
            var distance =
                plane.X * center.X +
                plane.Y * center.Y +
                plane.Z * center.Z +
                plane.W;
            if (distance < -radius)
            {
                return false;
            }
        }

        return true;
    }

    private static int FloorToInt(float value)
    {
        if (!float.IsFinite(value) ||
            value < int.MinValue ||
            value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return checked((int)MathF.Floor(value));
    }

    private static Vector3 NormalizeOrDefault(Vector3 value, Vector3 fallback) =>
        IsFinite(value) && value.LengthSquared() > 1e-12f
            ? Vector3.Normalize(value)
            : fallback;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static Int3 Subtract(Int3 left, Int3 right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    private static void ValidateClipmapLevel(int clipmapLevel)
    {
        if (clipmapLevel < 0 ||
            clipmapLevel >= PreviewSparseCloudVolumeContract.ClipmapCount)
        {
            throw new ArgumentOutOfRangeException(nameof(clipmapLevel));
        }
    }
}

internal readonly record struct PreviewSparseCloudClipmapUpdate(
    int Frame,
    int TableRevision,
    bool OriginChanged,
    bool Teleport,
    IReadOnlyList<PreviewSparseCloudLogicalBrickKey> Entering,
    IReadOnlyList<PreviewSparseCloudLogicalBrickKey> Retired,
    int RequestedCount,
    int PendingCount);

internal readonly record struct PreviewSparseCloudRequestPriority(
    int Bucket,
    float DistanceSquared,
    int ClipmapLevel,
    int X,
    int Y,
    int Z) : IComparable<PreviewSparseCloudRequestPriority>
{
    public int CompareTo(PreviewSparseCloudRequestPriority other)
    {
        var comparison = Bucket.CompareTo(other.Bucket);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = DistanceSquared.CompareTo(other.DistanceSquared);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = ClipmapLevel.CompareTo(other.ClipmapLevel);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = X.CompareTo(other.X);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Y.CompareTo(other.Y);
        return comparison != 0 ? comparison : Z.CompareTo(other.Z);
    }
}
