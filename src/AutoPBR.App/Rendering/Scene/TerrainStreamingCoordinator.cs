namespace AutoPBR.App.Rendering.Scene;

/// <summary>
/// A complete demand description used to create and advance scheduler stages.
/// </summary>
public readonly record struct TerrainStreamDemand(
    TerrainResidencyKey Key,
    long ContentGeneration,
    long DemandRevision,
    TerrainStreamPriority Priority,
    long CpuBakeBytes,
    long UploadReadyBytes,
    long CacheReadBytes = 0,
    long CacheWriteBytes = 0,
    long DeadlineTicks = long.MaxValue,
    long PredictedArrivalTicks = long.MaxValue,
    TerrainResidencyKey? ParentFallback = null,
    bool ReadCache = true,
    bool WriteCache = true,
    CancellationToken CancellationToken = default);

public readonly record struct TerrainStreamStageCompletion(
    TerrainStreamCompletionStatus Status,
    TerrainStreamEnqueueStatus? CacheWriteStatus = null);

/// <summary>
/// Deterministic stage coordinator over <see cref="TerrainStreamScheduler"/>. It contains no terrain
/// baking, cache, or GL code; integration code claims a stage, performs it, and reports the result.
/// </summary>
public sealed class TerrainStreamingCoordinator
{
    private readonly object _gate = new();
    private readonly TerrainStreamScheduler _scheduler;
    private readonly Dictionary<TerrainResidencyKey, TerrainStreamDemand> _demands = new();
    private readonly HashSet<DemandIdentity> _started = new();

    public TerrainStreamingCoordinator(TerrainStreamSchedulerOptions? options = null)
        : this(new TerrainStreamScheduler(options))
    {
    }

    public TerrainStreamingCoordinator(TerrainStreamScheduler scheduler)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public TerrainStreamScheduler Scheduler => _scheduler;

    /// <summary>
    /// Adds or revises demand and queues its first cache-read or CPU-bake stage. Repeating an already
    /// started demand is idempotent.
    /// </summary>
    public TerrainStreamEnqueueStatus SubmitDemand(TerrainStreamDemand demand, long nowTicks)
    {
        lock (_gate)
        {
            var update = _scheduler.UpdateDemand(
                demand.Key,
                demand.ContentGeneration,
                demand.DemandRevision);
            if (update == TerrainStreamDemandUpdateStatus.Stale)
            {
                return TerrainStreamEnqueueStatus.Stale;
            }

            var identity = DemandIdentity.From(demand);
            if (_started.Contains(identity))
            {
                return TerrainStreamEnqueueStatus.Duplicate;
            }

            _demands[demand.Key] = demand;
            _started.RemoveWhere(candidate =>
                candidate.Key == demand.Key && candidate != identity);

            var firstKind = demand.ReadCache
                ? TerrainStreamWorkKind.CacheRead
                : TerrainStreamWorkKind.CpuBake;
            var bytes = firstKind == TerrainStreamWorkKind.CacheRead
                ? demand.CacheReadBytes
                : demand.CpuBakeBytes;
            var status = _scheduler.Enqueue(CreateWork(demand, firstKind, bytes, nowTicks));
            if (status is TerrainStreamEnqueueStatus.Accepted
                or TerrainStreamEnqueueStatus.Updated
                or TerrainStreamEnqueueStatus.Duplicate)
            {
                _started.Add(identity);
            }

            return status;
        }
    }

    /// <summary>Cancels a key's current demand and rejects any later output from its claims.</summary>
    public TerrainStreamDemandUpdateStatus CancelDemand(
        TerrainResidencyKey key,
        long demandRevision)
    {
        lock (_gate)
        {
            var status = _scheduler.WithdrawDemand(key, demandRevision);
            if (status == TerrainStreamDemandUpdateStatus.Accepted)
            {
                _demands.Remove(key);
                _started.RemoveWhere(candidate => candidate.Key == key);
            }

            return status;
        }
    }

    public bool TryClaimNext(
        long nowTicks,
        out TerrainStreamClaim claim,
        CancellationToken cancellationToken = default) =>
        _scheduler.TryClaimNext(nowTicks, out claim, cancellationToken);

    public bool TryClaim(
        TerrainStreamWorkKind kind,
        long nowTicks,
        out TerrainStreamClaim claim,
        CancellationToken cancellationToken = default) =>
        _scheduler.TryClaim(kind, nowTicks, out claim, cancellationToken);

    /// <summary>Advances a cache read to upload on a hit, or CPU bake on a miss.</summary>
    public TerrainStreamCompletionStatus CompleteCacheRead(
        TerrainStreamClaim claim,
        bool cacheHit,
        long nowTicks,
        long outputBytes = -1)
    {
        lock (_gate)
        {
            if (claim.Item.Kind != TerrainStreamWorkKind.CacheRead)
            {
                return TerrainStreamCompletionStatus.UnknownClaim;
            }

            if (!TryGetCurrentDemand(claim, out var demand))
            {
                return _scheduler.Complete(claim);
            }

            var nextKind = cacheHit
                ? TerrainStreamWorkKind.UploadReady
                : TerrainStreamWorkKind.CpuBake;
            var configuredBytes = cacheHit ? demand.UploadReadyBytes : demand.CpuBakeBytes;
            var nextBytes = outputBytes >= 0 ? outputBytes : configuredBytes;
            return _scheduler.TryTransition(
                claim,
                CreateWork(demand, nextKind, nextBytes, nowTicks));
        }
    }

    /// <summary>
    /// Advances a bake to upload and independently offers its cache write to the bounded speculative
    /// queue. Upload backpressure keeps the bake claim live for retry.
    /// </summary>
    public TerrainStreamStageCompletion CompleteCpuBake(
        TerrainStreamClaim claim,
        long nowTicks,
        long outputBytes = -1)
    {
        lock (_gate)
        {
            if (claim.Item.Kind != TerrainStreamWorkKind.CpuBake)
            {
                return new TerrainStreamStageCompletion(
                    TerrainStreamCompletionStatus.UnknownClaim);
            }

            if (!TryGetCurrentDemand(claim, out var demand))
            {
                return new TerrainStreamStageCompletion(_scheduler.Complete(claim));
            }

            var uploadBytes = outputBytes >= 0 ? outputBytes : demand.UploadReadyBytes;
            var transition = _scheduler.TryTransition(
                claim,
                CreateWork(
                    demand,
                    TerrainStreamWorkKind.UploadReady,
                    uploadBytes,
                    nowTicks));
            if (transition != TerrainStreamCompletionStatus.Accepted || !demand.WriteCache)
            {
                return new TerrainStreamStageCompletion(transition);
            }

            var cacheWrite = CreateWork(
                demand with { Priority = TerrainStreamPriority.Speculation },
                TerrainStreamWorkKind.CacheWrite,
                demand.CacheWriteBytes > 0 ? demand.CacheWriteBytes : uploadBytes,
                nowTicks);
            var cacheStatus = _scheduler.Enqueue(cacheWrite);
            return new TerrainStreamStageCompletion(transition, cacheStatus);
        }
    }

    /// <summary>Completes upload and makes this key valid fallback coverage for dependent detail.</summary>
    public TerrainStreamCompletionStatus CompleteUploadReady(TerrainStreamClaim claim)
    {
        lock (_gate)
        {
            if (claim.Item.Kind != TerrainStreamWorkKind.UploadReady)
            {
                return TerrainStreamCompletionStatus.UnknownClaim;
            }

            var status = _scheduler.Complete(claim);
            if (status == TerrainStreamCompletionStatus.Accepted)
            {
                _scheduler.MarkCoverageAvailable(
                    claim.Item.Key,
                    claim.Item.ContentGeneration);
            }

            return status;
        }
    }

    public TerrainStreamCompletionStatus CompleteCacheWrite(TerrainStreamClaim claim)
    {
        if (claim.Item.Kind != TerrainStreamWorkKind.CacheWrite)
        {
            return TerrainStreamCompletionStatus.UnknownClaim;
        }

        return _scheduler.Complete(claim);
    }

    public void MarkCoverageAvailable(TerrainResidencyKey key, long contentGeneration) =>
        _scheduler.MarkCoverageAvailable(key, contentGeneration);

    public void RevokeCoverage(TerrainResidencyKey key) => _scheduler.RevokeCoverage(key);

    public TerrainStreamSchedulerMetrics GetMetrics() => _scheduler.GetMetrics();

    private bool TryGetCurrentDemand(
        TerrainStreamClaim claim,
        out TerrainStreamDemand demand)
    {
        return _demands.TryGetValue(claim.Item.Key, out demand) &&
               demand.ContentGeneration == claim.Item.ContentGeneration &&
               demand.DemandRevision == claim.Item.DemandRevision;
    }

    private static TerrainStreamWorkItem CreateWork(
        TerrainStreamDemand demand,
        TerrainStreamWorkKind kind,
        long bytes,
        long nowTicks) =>
        new(
            demand.Key,
            kind,
            demand.Priority,
            Math.Max(0, bytes),
            demand.ContentGeneration,
            demand.DemandRevision,
            nowTicks,
            demand.DeadlineTicks,
            demand.PredictedArrivalTicks,
            demand.ParentFallback,
            demand.CancellationToken);

    private readonly record struct DemandIdentity(
        TerrainResidencyKey Key,
        long ContentGeneration,
        long DemandRevision)
    {
        public static DemandIdentity From(TerrainStreamDemand demand) =>
            new(demand.Key, demand.ContentGeneration, demand.DemandRevision);
    }
}
