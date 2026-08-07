using System.Diagnostics;

namespace AutoPBR.App.Rendering.Scene;

public readonly record struct TerrainStreamingTelemetrySnapshot(
    long PlannerTicks,
    long PlannerUpdates,
    long SchedulerDequeues,
    long SchedulerStaleDrops,
    long SchedulerBackpressure,
    long CacheHits,
    long CacheMisses,
    long CacheReadTicks,
    long BakeTicks,
    long BakeCompleted,
    long UploadBytes,
    long UploadCompleted,
    long StagingDeferrals,
    long ActiveGpuBytes,
    long ReservedGpuBytes,
    long RetiringGpuBytes,
    int CoverageDebt,
    int TransitionCount,
    int CacheReadQueueItems,
    long CacheReadQueueBytes,
    int BakeQueueItems,
    long BakeQueueBytes,
    int UploadQueueItems,
    long UploadQueueBytes,
    double StreamCpuP95Ms)
{
    public double PlannerMilliseconds =>
        PlannerTicks * 1000.0 / Stopwatch.Frequency;

    public double AverageCacheReadMilliseconds =>
        CacheHits + CacheMisses > 0
            ? CacheReadTicks * 1000.0 / Stopwatch.Frequency / (CacheHits + CacheMisses)
            : 0.0;

    public double AverageBakeMilliseconds =>
        BakeCompleted > 0
            ? BakeTicks * 1000.0 / Stopwatch.Frequency / BakeCompleted
            : 0.0;
}

/// <summary>
/// Lock-free counters shared by terrain planning, cache, bake, and GL upload stages.
/// Queue gauges are replaced atomically so diagnostics never enumerate live collections.
/// </summary>
public sealed class TerrainStreamingTelemetry
{
    private long _plannerTicks;
    private long _plannerUpdates;
    private long _schedulerDequeues;
    private long _schedulerStaleDrops;
    private long _schedulerBackpressure;
    private long _cacheHits;
    private long _cacheMisses;
    private long _cacheReadTicks;
    private long _bakeTicks;
    private long _bakeCompleted;
    private long _uploadBytes;
    private long _uploadCompleted;
    private long _stagingDeferrals;
    private long _activeGpuBytes;
    private long _reservedGpuBytes;
    private long _retiringGpuBytes;
    private int _coverageDebt;
    private int _transitionCount;
    private int _cacheReadQueueItems;
    private long _cacheReadQueueBytes;
    private int _bakeQueueItems;
    private long _bakeQueueBytes;
    private int _uploadQueueItems;
    private long _uploadQueueBytes;
    private readonly object _frameGate = new();
    private readonly double[] _streamCpuMilliseconds = new double[128];
    private int _streamCpuCount;
    private int _streamCpuCursor;

    public void RecordPlanner(long elapsedTicks)
    {
        Interlocked.Add(ref _plannerTicks, Math.Max(0, elapsedTicks));
        Interlocked.Increment(ref _plannerUpdates);
    }

    public void RecordSchedulerDequeue() =>
        Interlocked.Increment(ref _schedulerDequeues);

    public void RecordStaleDrop() =>
        Interlocked.Increment(ref _schedulerStaleDrops);

    public void RecordBackpressure() =>
        Interlocked.Increment(ref _schedulerBackpressure);

    public void RecordCacheRead(bool hit, long elapsedTicks)
    {
        if (hit)
        {
            Interlocked.Increment(ref _cacheHits);
        }
        else
        {
            Interlocked.Increment(ref _cacheMisses);
        }

        Interlocked.Add(ref _cacheReadTicks, Math.Max(0, elapsedTicks));
    }

    public void RecordBake(long elapsedTicks)
    {
        Interlocked.Add(ref _bakeTicks, Math.Max(0, elapsedTicks));
        Interlocked.Increment(ref _bakeCompleted);
    }

    public void RecordUpload(long bytes)
    {
        Interlocked.Add(ref _uploadBytes, Math.Max(0, bytes));
        Interlocked.Increment(ref _uploadCompleted);
    }

    public void RecordStagingDeferral() =>
        Interlocked.Increment(ref _stagingDeferrals);

    public void RecordStreamCpuFrame(double milliseconds)
    {
        lock (_frameGate)
        {
            _streamCpuMilliseconds[_streamCpuCursor] = Math.Max(0.0, milliseconds);
            _streamCpuCursor = (_streamCpuCursor + 1) % _streamCpuMilliseconds.Length;
            _streamCpuCount = Math.Min(_streamCpuCount + 1, _streamCpuMilliseconds.Length);
        }
    }

    public void SetGpuState(long activeBytes, long reservedBytes, long retiringBytes)
    {
        Interlocked.Exchange(ref _activeGpuBytes, Math.Max(0, activeBytes));
        Interlocked.Exchange(ref _reservedGpuBytes, Math.Max(0, reservedBytes));
        Interlocked.Exchange(ref _retiringGpuBytes, Math.Max(0, retiringBytes));
    }

    public void SetCoverageState(int debt, int transitionCount)
    {
        Volatile.Write(ref _coverageDebt, Math.Max(0, debt));
        Volatile.Write(ref _transitionCount, Math.Max(0, transitionCount));
    }

    public void SetQueueState(
        int cacheReadItems,
        long cacheReadBytes,
        int bakeItems,
        long bakeBytes,
        int uploadItems,
        long uploadBytes)
    {
        Volatile.Write(ref _cacheReadQueueItems, Math.Max(0, cacheReadItems));
        Interlocked.Exchange(ref _cacheReadQueueBytes, Math.Max(0, cacheReadBytes));
        Volatile.Write(ref _bakeQueueItems, Math.Max(0, bakeItems));
        Interlocked.Exchange(ref _bakeQueueBytes, Math.Max(0, bakeBytes));
        Volatile.Write(ref _uploadQueueItems, Math.Max(0, uploadItems));
        Interlocked.Exchange(ref _uploadQueueBytes, Math.Max(0, uploadBytes));
    }

    public TerrainStreamingTelemetrySnapshot Snapshot()
    {
        double streamP95;
        lock (_frameGate)
        {
            if (_streamCpuCount == 0)
            {
                streamP95 = 0.0;
            }
            else
            {
                var samples = new double[_streamCpuCount];
                Array.Copy(_streamCpuMilliseconds, samples, _streamCpuCount);
                Array.Sort(samples);
                var index = Math.Clamp(
                    (int)Math.Ceiling(samples.Length * 0.95) - 1,
                    0,
                    samples.Length - 1);
                streamP95 = samples[index];
            }
        }

        return new(
            Interlocked.Read(ref _plannerTicks),
            Interlocked.Read(ref _plannerUpdates),
            Interlocked.Read(ref _schedulerDequeues),
            Interlocked.Read(ref _schedulerStaleDrops),
            Interlocked.Read(ref _schedulerBackpressure),
            Interlocked.Read(ref _cacheHits),
            Interlocked.Read(ref _cacheMisses),
            Interlocked.Read(ref _cacheReadTicks),
            Interlocked.Read(ref _bakeTicks),
            Interlocked.Read(ref _bakeCompleted),
            Interlocked.Read(ref _uploadBytes),
            Interlocked.Read(ref _uploadCompleted),
            Interlocked.Read(ref _stagingDeferrals),
            Interlocked.Read(ref _activeGpuBytes),
            Interlocked.Read(ref _reservedGpuBytes),
            Interlocked.Read(ref _retiringGpuBytes),
            Volatile.Read(ref _coverageDebt),
            Volatile.Read(ref _transitionCount),
            Volatile.Read(ref _cacheReadQueueItems),
            Interlocked.Read(ref _cacheReadQueueBytes),
            Volatile.Read(ref _bakeQueueItems),
            Interlocked.Read(ref _bakeQueueBytes),
            Volatile.Read(ref _uploadQueueItems),
            Interlocked.Read(ref _uploadQueueBytes),
            streamP95);
    }
}
