namespace AutoPBR.App.Rendering.Scene;

/// <summary>Independent queues used by the terrain streaming pipeline.</summary>
public enum TerrainStreamWorkKind : byte
{
    CacheRead,
    CpuBake,
    UploadReady,
    CacheWrite,
}

/// <summary>
/// Base priority before aging. The ordering deliberately does not contain a Full-terrain phase.
/// </summary>
public enum TerrainStreamPriority : byte
{
    CoverageRepair,
    TransactionDeadline,
    PredictedArrival,
    VisibleRefinement,
    AgingFairness,
    Speculation,
}

public enum TerrainStreamEnqueueStatus : byte
{
    Accepted,
    Updated,
    Duplicate,
    Backpressured,
    Stale,
    Cancelled,
}

public enum TerrainStreamDemandUpdateStatus : byte
{
    Accepted,
    Unchanged,
    Stale,
}

public enum TerrainStreamCompletionStatus : byte
{
    Accepted,
    Stale,
    Cancelled,
    UnknownClaim,
    Backpressured,
}

/// <summary>A hard item and byte cap for one work-kind queue.</summary>
public readonly record struct TerrainStreamQueueLimit(int MaxItems, long MaxBytes)
{
    internal int SafeMaxItems => Math.Max(0, MaxItems);
    internal long SafeMaxBytes => Math.Max(0, MaxBytes);
}

/// <summary>Configuration for the deterministic scheduler. All time values use caller-defined ticks.</summary>
public sealed class TerrainStreamSchedulerOptions
{
    public TerrainStreamQueueLimit CacheRead { get; init; } = new(128, 64L * 1024 * 1024);
    public TerrainStreamQueueLimit CpuBake { get; init; } = new(64, 256L * 1024 * 1024);
    public TerrainStreamQueueLimit UploadReady { get; init; } = new(64, 256L * 1024 * 1024);
    public TerrainStreamQueueLimit CacheWrite { get; init; } = new(64, 256L * 1024 * 1024);

    /// <summary>
    /// Waiting this many caller ticks promotes work by one priority class. A value at or below zero
    /// disables promotion.
    /// </summary>
    public long AgingQuantumTicks { get; init; } = 1_000;

    internal TerrainStreamQueueLimit LimitFor(TerrainStreamWorkKind kind) => kind switch
    {
        TerrainStreamWorkKind.CacheRead => CacheRead,
        TerrainStreamWorkKind.CpuBake => CpuBake,
        TerrainStreamWorkKind.UploadReady => UploadReady,
        TerrainStreamWorkKind.CacheWrite => CacheWrite,
        _ => default,
    };
}

/// <summary>
/// Immutable unit of queued work. Content generation identifies the source data; demand revision
/// identifies the latest camera/residency decision for the key.
/// </summary>
public readonly record struct TerrainStreamWorkItem(
    TerrainResidencyKey Key,
    TerrainStreamWorkKind Kind,
    TerrainStreamPriority Priority,
    long EstimatedBytes,
    long ContentGeneration,
    long DemandRevision,
    long EnqueuedAtTicks,
    long DeadlineTicks = long.MaxValue,
    long PredictedArrivalTicks = long.MaxValue,
    TerrainResidencyKey? ParentFallback = null,
    CancellationToken CancellationToken = default);

public readonly record struct TerrainStreamClaim(long Id, TerrainStreamWorkItem Item);

public readonly record struct TerrainStreamQueueMetrics(
    int QueuedItems,
    long QueuedBytes,
    int InflightItems,
    long InflightBytes);

public readonly record struct TerrainStreamSchedulerMetrics(
    TerrainStreamQueueMetrics CacheRead,
    TerrainStreamQueueMetrics CpuBake,
    TerrainStreamQueueMetrics UploadReady,
    TerrainStreamQueueMetrics CacheWrite,
    long Accepted,
    long Updated,
    long Deduplicated,
    long Backpressured,
    long StaleDropped,
    long Cancelled,
    long Claimed);

/// <summary>
/// Thread-safe, bounded, deterministic scheduler for terrain stream work. It owns no workers and
/// reads no clock, so callers can drive it from tests or a future <see cref="TerrainChunkStreamer"/>
/// integration.
/// </summary>
public sealed class TerrainStreamScheduler
{
    private readonly object _gate = new();
    private readonly TerrainStreamSchedulerOptions _options;
    private readonly Dictionary<WorkIdentity, QueueEntry> _queued = new();
    private readonly Dictionary<long, TerrainStreamWorkItem> _inflight = new();
    private readonly Dictionary<TerrainStreamWorkKind, QueueAccounting> _accounting = new();
    private readonly Dictionary<TerrainResidencyKey, DemandStamp> _demands = new();
    private readonly Dictionary<TerrainResidencyKey, long> _coverageGenerations = new();
    private long _nextSequence;
    private long _nextClaimId;
    private long _accepted;
    private long _updated;
    private long _deduplicated;
    private long _backpressured;
    private long _staleDropped;
    private long _cancelled;
    private long _claimed;

    public TerrainStreamScheduler(TerrainStreamSchedulerOptions? options = null)
    {
        _options = options ?? new TerrainStreamSchedulerOptions();
        foreach (var kind in Enum.GetValues<TerrainStreamWorkKind>())
        {
            _accounting.Add(kind, new QueueAccounting());
        }
    }

    /// <summary>
    /// Records the only content generation and demand revision currently valid for a key. Newer
    /// demand immediately removes stale queued stages; already claimed stages are rejected later.
    /// </summary>
    public TerrainStreamDemandUpdateStatus UpdateDemand(
        TerrainResidencyKey key,
        long contentGeneration,
        long demandRevision)
    {
        lock (_gate)
        {
            if (_demands.TryGetValue(key, out var current))
            {
                if (demandRevision < current.DemandRevision ||
                    (demandRevision == current.DemandRevision &&
                     contentGeneration != current.ContentGeneration))
                {
                    return TerrainStreamDemandUpdateStatus.Stale;
                }

                if (current == new DemandStamp(contentGeneration, demandRevision))
                {
                    return TerrainStreamDemandUpdateStatus.Unchanged;
                }
            }

            _demands[key] = new DemandStamp(contentGeneration, demandRevision);
            DropInvalidQueuedForKey(key);
            return TerrainStreamDemandUpdateStatus.Accepted;
        }
    }

    /// <summary>Withdraws demand at or beyond the supplied revision and drops its queued stages.</summary>
    public TerrainStreamDemandUpdateStatus WithdrawDemand(TerrainResidencyKey key, long demandRevision)
    {
        lock (_gate)
        {
            if (!_demands.TryGetValue(key, out var current))
            {
                return TerrainStreamDemandUpdateStatus.Unchanged;
            }

            if (demandRevision < current.DemandRevision)
            {
                return TerrainStreamDemandUpdateStatus.Stale;
            }

            _demands.Remove(key);
            DropInvalidQueuedForKey(key);
            return TerrainStreamDemandUpdateStatus.Accepted;
        }
    }

    public TerrainStreamEnqueueStatus Enqueue(TerrainStreamWorkItem item)
    {
        lock (_gate)
        {
            return EnqueueLocked(item);
        }
    }

    /// <summary>Claims the highest ranked eligible item across every work kind.</summary>
    public bool TryClaimNext(
        long nowTicks,
        out TerrainStreamClaim claim,
        CancellationToken cancellationToken = default)
    {
        return TryClaimCore(kind: null, nowTicks, out claim, cancellationToken);
    }

    /// <summary>Claims the highest ranked eligible item from one worker lane.</summary>
    public bool TryClaim(
        TerrainStreamWorkKind kind,
        long nowTicks,
        out TerrainStreamClaim claim,
        CancellationToken cancellationToken = default)
    {
        return TryClaimCore(kind, nowTicks, out claim, cancellationToken);
    }

    /// <summary>
    /// Atomically queues a successor and releases a claim. On backpressure the claim remains live,
    /// allowing the producer to retry without losing output.
    /// </summary>
    public TerrainStreamCompletionStatus TryTransition(
        TerrainStreamClaim claim,
        TerrainStreamWorkItem successor)
    {
        lock (_gate)
        {
            var state = ValidateClaimLocked(claim);
            if (state != TerrainStreamCompletionStatus.Accepted)
            {
                ReleaseInvalidClaimLocked(claim, state);
                return state;
            }

            var enqueue = EnqueueLocked(successor);
            if (enqueue == TerrainStreamEnqueueStatus.Backpressured)
            {
                return TerrainStreamCompletionStatus.Backpressured;
            }

            if (enqueue is TerrainStreamEnqueueStatus.Stale or TerrainStreamEnqueueStatus.Cancelled)
            {
                ReleaseClaimLocked(claim);
                return enqueue == TerrainStreamEnqueueStatus.Cancelled
                    ? TerrainStreamCompletionStatus.Cancelled
                    : TerrainStreamCompletionStatus.Stale;
            }

            ReleaseClaimLocked(claim);
            return TerrainStreamCompletionStatus.Accepted;
        }
    }

    public TerrainStreamCompletionStatus Complete(TerrainStreamClaim claim)
    {
        lock (_gate)
        {
            var state = ValidateClaimLocked(claim);
            if (state != TerrainStreamCompletionStatus.Accepted)
            {
                ReleaseInvalidClaimLocked(claim, state);
                return state;
            }

            ReleaseClaimLocked(claim);
            return TerrainStreamCompletionStatus.Accepted;
        }
    }

    public bool IsClaimCurrent(TerrainStreamClaim claim)
    {
        lock (_gate)
        {
            return ValidateClaimLocked(claim) == TerrainStreamCompletionStatus.Accepted;
        }
    }

    /// <summary>Marks fallback geometry usable for children from the same content generation.</summary>
    public void MarkCoverageAvailable(TerrainResidencyKey key, long contentGeneration)
    {
        lock (_gate)
        {
            _coverageGenerations[key] = contentGeneration;
        }
    }

    public void RevokeCoverage(TerrainResidencyKey key)
    {
        lock (_gate)
        {
            _coverageGenerations.Remove(key);
        }
    }

    public bool IsCoverageAvailable(TerrainResidencyKey key, long contentGeneration)
    {
        lock (_gate)
        {
            return _coverageGenerations.TryGetValue(key, out var generation) &&
                   generation == contentGeneration;
        }
    }

    public TerrainStreamSchedulerMetrics GetMetrics()
    {
        lock (_gate)
        {
            return new TerrainStreamSchedulerMetrics(
                MetricsFor(TerrainStreamWorkKind.CacheRead),
                MetricsFor(TerrainStreamWorkKind.CpuBake),
                MetricsFor(TerrainStreamWorkKind.UploadReady),
                MetricsFor(TerrainStreamWorkKind.CacheWrite),
                _accepted,
                _updated,
                _deduplicated,
                _backpressured,
                _staleDropped,
                _cancelled,
                _claimed);
        }
    }

    /// <summary>Returns the coarser section that can cover a detail key, or null at maximum LOD.</summary>
    public static TerrainResidencyKey? ParentOf(TerrainResidencyKey key)
    {
        if (key.LodLevel >= TerrainResidencyKey.MaxLodLevel)
        {
            return null;
        }

        var parentLevel = (byte)(key.LodLevel + 1);
        return TerrainResidencyKey.FromChunk(
            new TerrainChunkKey(key.OriginChunkX, key.OriginChunkZ),
            parentLevel);
    }

    private bool TryClaimCore(
        TerrainStreamWorkKind? kind,
        long nowTicks,
        out TerrainStreamClaim claim,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            PruneInvalidQueued();

            QueueEntry? best = null;
            foreach (var entry in _queued.Values)
            {
                if ((kind.HasValue && entry.Item.Kind != kind.Value) ||
                    !IsDependencySatisfied(entry.Item))
                {
                    continue;
                }

                if (best is null || Compare(entry, best, nowTicks) < 0)
                {
                    best = entry;
                }
            }

            if (best is null)
            {
                claim = default;
                return false;
            }

            RemoveQueued(best);
            var id = ++_nextClaimId;
            _inflight.Add(id, best.Item);
            var accounting = _accounting[best.Item.Kind];
            accounting.InflightItems++;
            accounting.InflightBytes += SafeBytes(best.Item.EstimatedBytes);
            _claimed++;
            claim = new TerrainStreamClaim(id, best.Item);
            return true;
        }
    }

    private TerrainStreamEnqueueStatus EnqueueLocked(TerrainStreamWorkItem item)
    {
        if (item.CancellationToken.IsCancellationRequested)
        {
            _cancelled++;
            return TerrainStreamEnqueueStatus.Cancelled;
        }

        if (!IsCurrent(item))
        {
            _staleDropped++;
            return TerrainStreamEnqueueStatus.Stale;
        }

        var identity = WorkIdentity.From(item);
        if (_queued.TryGetValue(identity, out var existing))
        {
            if (existing.Item == item)
            {
                _deduplicated++;
                return TerrainStreamEnqueueStatus.Duplicate;
            }

            var accounting = _accounting[item.Kind];
            var replacementBytes = SafeBytes(item.EstimatedBytes);
            var bytesAfter = accounting.QueuedBytes -
                             SafeBytes(existing.Item.EstimatedBytes) +
                             replacementBytes;
            if (bytesAfter > _options.LimitFor(item.Kind).SafeMaxBytes)
            {
                _backpressured++;
                return TerrainStreamEnqueueStatus.Backpressured;
            }

            accounting.QueuedBytes = bytesAfter;
            existing.Item = item with
            {
                EnqueuedAtTicks = Math.Min(existing.Item.EnqueuedAtTicks, item.EnqueuedAtTicks),
            };
            _updated++;
            return TerrainStreamEnqueueStatus.Updated;
        }

        if (_inflight.Values.Any(inflight => WorkIdentity.From(inflight) == identity))
        {
            _deduplicated++;
            return TerrainStreamEnqueueStatus.Duplicate;
        }

        var limit = _options.LimitFor(item.Kind);
        var queue = _accounting[item.Kind];
        var bytes = SafeBytes(item.EstimatedBytes);
        if (queue.QueuedItems >= limit.SafeMaxItems ||
            bytes > limit.SafeMaxBytes - queue.QueuedBytes)
        {
            _backpressured++;
            return TerrainStreamEnqueueStatus.Backpressured;
        }

        var entry = new QueueEntry(item, ++_nextSequence);
        _queued.Add(identity, entry);
        queue.QueuedItems++;
        queue.QueuedBytes += bytes;
        _accepted++;
        return TerrainStreamEnqueueStatus.Accepted;
    }

    private int Compare(QueueEntry left, QueueEntry right, long nowTicks)
    {
        var leftRank = EffectivePriority(left.Item, nowTicks);
        var rightRank = EffectivePriority(right.Item, nowTicks);
        var compare = leftRank.CompareTo(rightRank);
        if (compare != 0)
        {
            return compare;
        }

        compare = UrgencyTicks(left.Item).CompareTo(UrgencyTicks(right.Item));
        if (compare != 0)
        {
            return compare;
        }

        compare = left.Item.EnqueuedAtTicks.CompareTo(right.Item.EnqueuedAtTicks);
        if (compare != 0)
        {
            return compare;
        }

        return left.Sequence.CompareTo(right.Sequence);
    }

    private int EffectivePriority(TerrainStreamWorkItem item, long nowTicks)
    {
        var basePriority = (int)item.Priority;
        if (_options.AgingQuantumTicks <= 0 || nowTicks <= item.EnqueuedAtTicks)
        {
            return basePriority;
        }

        var promotions = (nowTicks - item.EnqueuedAtTicks) / _options.AgingQuantumTicks;
        return (int)Math.Max(0, basePriority - promotions);
    }

    private static long UrgencyTicks(TerrainStreamWorkItem item) => item.Priority switch
    {
        TerrainStreamPriority.TransactionDeadline => item.DeadlineTicks,
        TerrainStreamPriority.PredictedArrival => item.PredictedArrivalTicks,
        _ => long.MaxValue,
    };

    private bool IsDependencySatisfied(TerrainStreamWorkItem item) =>
        item.ParentFallback is not { } parent ||
        (_coverageGenerations.TryGetValue(parent, out var generation) &&
         generation == item.ContentGeneration);

    private bool IsCurrent(TerrainStreamWorkItem item) =>
        _demands.TryGetValue(item.Key, out var demand) &&
        demand.ContentGeneration == item.ContentGeneration &&
        demand.DemandRevision == item.DemandRevision;

    private TerrainStreamCompletionStatus ValidateClaimLocked(TerrainStreamClaim claim)
    {
        if (!_inflight.TryGetValue(claim.Id, out var active) || active != claim.Item)
        {
            return TerrainStreamCompletionStatus.UnknownClaim;
        }

        if (active.CancellationToken.IsCancellationRequested)
        {
            return TerrainStreamCompletionStatus.Cancelled;
        }

        return IsCurrent(active)
            ? TerrainStreamCompletionStatus.Accepted
            : TerrainStreamCompletionStatus.Stale;
    }

    private void ReleaseInvalidClaimLocked(
        TerrainStreamClaim claim,
        TerrainStreamCompletionStatus status)
    {
        if (!_inflight.ContainsKey(claim.Id))
        {
            return;
        }

        if (status == TerrainStreamCompletionStatus.Cancelled)
        {
            _cancelled++;
        }
        else if (status == TerrainStreamCompletionStatus.Stale)
        {
            _staleDropped++;
        }

        ReleaseClaimLocked(claim);
    }

    private void ReleaseClaimLocked(TerrainStreamClaim claim)
    {
        if (!_inflight.Remove(claim.Id, out var item))
        {
            return;
        }

        var accounting = _accounting[item.Kind];
        accounting.InflightItems--;
        accounting.InflightBytes -= SafeBytes(item.EstimatedBytes);
    }

    private void PruneInvalidQueued()
    {
        foreach (var entry in _queued.Values.ToArray())
        {
            if (entry.Item.CancellationToken.IsCancellationRequested)
            {
                RemoveQueued(entry);
                _cancelled++;
            }
            else if (!IsCurrent(entry.Item))
            {
                RemoveQueued(entry);
                _staleDropped++;
            }
        }
    }

    private void DropInvalidQueuedForKey(TerrainResidencyKey key)
    {
        foreach (var entry in _queued.Values
                     .Where(entry => entry.Item.Key == key && !IsCurrent(entry.Item))
                     .ToArray())
        {
            RemoveQueued(entry);
            _staleDropped++;
        }
    }

    private void RemoveQueued(QueueEntry entry)
    {
        if (!_queued.Remove(WorkIdentity.From(entry.Item)))
        {
            return;
        }

        var accounting = _accounting[entry.Item.Kind];
        accounting.QueuedItems--;
        accounting.QueuedBytes -= SafeBytes(entry.Item.EstimatedBytes);
    }

    private TerrainStreamQueueMetrics MetricsFor(TerrainStreamWorkKind kind)
    {
        var accounting = _accounting[kind];
        return new TerrainStreamQueueMetrics(
            accounting.QueuedItems,
            accounting.QueuedBytes,
            accounting.InflightItems,
            accounting.InflightBytes);
    }

    private static long SafeBytes(long bytes) => Math.Max(0, bytes);

    private readonly record struct DemandStamp(long ContentGeneration, long DemandRevision);

    private readonly record struct WorkIdentity(
        TerrainResidencyKey Key,
        TerrainStreamWorkKind Kind,
        long ContentGeneration,
        long DemandRevision)
    {
        public static WorkIdentity From(TerrainStreamWorkItem item) =>
            new(item.Key, item.Kind, item.ContentGeneration, item.DemandRevision);
    }

    private sealed class QueueEntry(TerrainStreamWorkItem item, long sequence)
    {
        public TerrainStreamWorkItem Item { get; set; } = item;
        public long Sequence { get; } = sequence;
    }

    private sealed class QueueAccounting
    {
        public int QueuedItems { get; set; }
        public long QueuedBytes { get; set; }
        public int InflightItems { get; set; }
        public long InflightBytes { get; set; }
    }
}
