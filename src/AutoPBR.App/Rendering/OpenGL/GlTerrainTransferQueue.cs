namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Frame-budgeted, nonblocking upload scheduler for terrain arena reservations. The queue contains
/// no GL calls: callers inject staging-segment polling and chunk submission callbacks.
/// </summary>
internal sealed class GlTerrainTransferQueue
{
    private readonly GlTerrainMeshArena _arena;
    private readonly LinkedList<Transfer> _pending = [];
    private readonly Dictionary<long, LinkedListNode<Transfer>> _byId = [];
    private long _nextTransferId;
    private int _nextStagingSegment;
    private long _submittedBytes;
    private long _cancelledBytes;
    private long _publishedBytes;

    public GlTerrainTransferQueue(
        GlTerrainMeshArena arena,
        int stagingSegmentCount,
        int stagingSegmentBytes,
        int maxBytesPerFrame,
        int maxChunksPerFrame)
    {
        _arena = arena ?? throw new ArgumentNullException(nameof(arena));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stagingSegmentCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stagingSegmentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChunksPerFrame);
        StagingSegmentCount = stagingSegmentCount;
        StagingSegmentBytes = stagingSegmentBytes;
        MaxBytesPerFrame = maxBytesPerFrame;
        MaxChunksPerFrame = maxChunksPerFrame;
    }

    public GlTerrainTransferQueue(
        GlTerrainMeshArena arena,
        int stagingSegmentCount,
        int stagingSegmentBytes,
        int maxBytesPerFrame)
        : this(
            arena,
            stagingSegmentCount,
            stagingSegmentBytes,
            maxBytesPerFrame,
            stagingSegmentCount)
    {
    }

    public int StagingSegmentCount { get; }
    public int StagingSegmentBytes { get; }
    public int MaxBytesPerFrame { get; }
    public int MaxChunksPerFrame { get; }
    public int PendingCount => _pending.Count;

    /// <summary>Adds a still-reserved paired allocation to the FIFO upload queue.</summary>
    public long Enqueue(
        GlTerrainMeshArena.Reservation reservation,
        CancellationToken cancellationToken = default)
    {
        if (!_arena.IsReserved(reservation))
        {
            throw new InvalidOperationException("Only an active arena reservation can be queued.");
        }

        foreach (var transfer in _pending)
        {
            if (transfer.Reservation.Id == reservation.Id)
            {
                throw new InvalidOperationException("The reservation is already queued.");
            }
        }

        var id = checked(++_nextTransferId);
        var item = new Transfer(id, reservation, cancellationToken);
        var node = _pending.AddLast(item);
        _byId.Add(id, node);
        if (cancellationToken.IsCancellationRequested)
        {
            Cancel(id);
        }

        return id;
    }

    /// <summary>
    /// Cancels without waiting. Untouched reservations are freed immediately; partially submitted
    /// reservations enter the arena retirement list under their last submission token.
    /// </summary>
    public bool Cancel(long transferId)
    {
        if (!_byId.Remove(transferId, out var node))
        {
            return false;
        }

        var transfer = node.Value;
        _pending.Remove(node);
        var released = transfer.BytesTransferred == 0
            ? _arena.Cancel(transfer.Reservation)
            : _arena.Retire(transfer.Reservation, transfer.LastSubmissionToken);
        if (!released)
        {
            throw new InvalidOperationException("The queued arena reservation changed unexpectedly.");
        }

        _cancelledBytes += transfer.TotalBytes - transfer.BytesTransferred;
        return true;
    }

    /// <summary>
    /// Advances uploads within the configured byte/chunk budget. <paramref name="tryAcquireSegment"/>
    /// must poll/acquire without blocking and return false for a staging segment whose fence is not
    /// ready. Each staging segment is attempted at most once per pump, so pressure is deferred to a
    /// later frame rather than waited on.
    /// </summary>
    public PumpResult Pump(
        long frameOrFenceToken,
        Func<int, bool> tryAcquireSegment,
        Action<Chunk> submitChunk,
        Action<GlTerrainMeshArena.Allocation>? publish = null)
    {
        ArgumentNullException.ThrowIfNull(tryAcquireSegment);
        ArgumentNullException.ThrowIfNull(submitChunk);

        CancelRequestedTransfers();
        var bytesThisFrame = 0;
        var chunksThisFrame = 0;
        var acquisitionPolls = 0;
        var publishedThisFrame = 0;
        var attemptedSegments = new bool[StagingSegmentCount];
        var availableSegmentAttempts = StagingSegmentCount;
        var stagingDeferred = false;

        while (_pending.First is { } node &&
               bytesThisFrame < MaxBytesPerFrame &&
               chunksThisFrame < MaxChunksPerFrame)
        {
            var transfer = node.Value;
            if (transfer.CancellationToken.IsCancellationRequested)
            {
                Cancel(transfer.Id);
                continue;
            }

            if (!TryAcquire(
                    tryAcquireSegment,
                    attemptedSegments,
                    ref availableSegmentAttempts,
                    ref acquisitionPolls,
                    out var stagingSegment))
            {
                stagingDeferred = true;
                break;
            }

            var bytesRemainingInFrame = MaxBytesPerFrame - bytesThisFrame;
            var chunkBytes = Math.Min(
                StagingSegmentBytes,
                Math.Min(bytesRemainingInFrame, transfer.BytesRemainingInCurrentStream));
            if (chunkBytes <= 0)
            {
                throw new InvalidOperationException("A queued transfer has no uploadable bytes.");
            }

            var stream = transfer.VertexBytesTransferred < transfer.Reservation.VertexBytes
                ? StreamKind.Vertex
                : StreamKind.Index;
            var sourceOffset = stream == StreamKind.Vertex
                ? transfer.VertexBytesTransferred
                : transfer.IndexBytesTransferred;
            var destinationOffset = stream == StreamKind.Vertex
                ? transfer.Reservation.VertexOffsetBytes + sourceOffset
                : transfer.Reservation.IndexOffsetBytes + sourceOffset;
            var completesTransfer = transfer.BytesTransferred + chunkBytes == transfer.TotalBytes;
            submitChunk(
                new Chunk(
                    transfer.Id,
                    transfer.Reservation.Id,
                    stagingSegment,
                    stream,
                    sourceOffset,
                    destinationOffset,
                    chunkBytes,
                    frameOrFenceToken,
                    completesTransfer));

            if (stream == StreamKind.Vertex)
            {
                transfer.VertexBytesTransferred += chunkBytes;
            }
            else
            {
                transfer.IndexBytesTransferred += chunkBytes;
            }

            transfer.LastSubmissionToken = frameOrFenceToken;
            bytesThisFrame += chunkBytes;
            chunksThisFrame++;
            _submittedBytes += chunkBytes;

            if (transfer.BytesTransferred != transfer.TotalBytes)
            {
                continue;
            }

            if (!_arena.TryPublish(transfer.Reservation, out var allocation))
            {
                throw new InvalidOperationException("Completed upload could not publish its reservation.");
            }

            _byId.Remove(transfer.Id);
            _pending.Remove(node);
            _publishedBytes += transfer.TotalBytes;
            publishedThisFrame++;
            publish?.Invoke(allocation);
        }

        return new PumpResult(
            bytesThisFrame,
            chunksThisFrame,
            publishedThisFrame,
            acquisitionPolls,
            stagingDeferred,
            _pending.Count);
    }

    public Telemetry GetTelemetry()
    {
        long pendingBytes = 0;
        long transferredPendingBytes = 0;
        foreach (var transfer in _pending)
        {
            pendingBytes += transfer.TotalBytes - transfer.BytesTransferred;
            transferredPendingBytes += transfer.BytesTransferred;
        }

        return new Telemetry(
            _pending.Count,
            pendingBytes,
            transferredPendingBytes,
            _submittedBytes,
            _publishedBytes,
            _cancelledBytes);
    }

    private bool TryAcquire(
        Func<int, bool> tryAcquireSegment,
        bool[] attemptedSegments,
        ref int attemptsRemaining,
        ref int acquisitionPolls,
        out int segment)
    {
        while (attemptsRemaining > 0)
        {
            segment = _nextStagingSegment;
            _nextStagingSegment = (_nextStagingSegment + 1) % StagingSegmentCount;
            if (attemptedSegments[segment])
            {
                continue;
            }

            attemptedSegments[segment] = true;
            attemptsRemaining--;
            acquisitionPolls++;
            if (tryAcquireSegment(segment))
            {
                return true;
            }
        }

        segment = -1;
        return false;
    }

    private void CancelRequestedTransfers()
    {
        var node = _pending.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.CancellationToken.IsCancellationRequested)
            {
                Cancel(node.Value.Id);
            }

            node = next;
        }
    }

    internal enum StreamKind
    {
        Vertex,
        Index,
    }

    internal readonly record struct Chunk(
        long TransferId,
        long ReservationId,
        int StagingSegment,
        StreamKind Stream,
        int SourceOffsetBytes,
        int DestinationOffsetBytes,
        int ByteCount,
        long FrameOrFenceToken,
        bool CompletesTransfer);

    internal readonly record struct PumpResult(
        int BytesSubmitted,
        int ChunksSubmitted,
        int PublishedCount,
        int StagingAcquisitionPolls,
        bool DeferredByStagingPressure,
        int PendingCount);

    internal readonly record struct Telemetry(
        int PendingCount,
        long PendingBytes,
        long SubmittedButUnpublishedBytes,
        long LifetimeSubmittedBytes,
        long LifetimePublishedBytes,
        long LifetimeCancelledBytes);

    private sealed class Transfer(
        long id,
        GlTerrainMeshArena.Reservation reservation,
        CancellationToken cancellationToken)
    {
        public long Id { get; } = id;
        public GlTerrainMeshArena.Reservation Reservation { get; } = reservation;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public int VertexBytesTransferred { get; set; }
        public int IndexBytesTransferred { get; set; }
        public long LastSubmissionToken { get; set; }
        public int TotalBytes => checked(Reservation.VertexBytes + Reservation.IndexBytes);
        public int BytesTransferred => checked(VertexBytesTransferred + IndexBytesTransferred);
        public int BytesRemainingInCurrentStream =>
            VertexBytesTransferred < Reservation.VertexBytes
                ? Reservation.VertexBytes - VertexBytesTransferred
                : Reservation.IndexBytes - IndexBytesTransferred;
    }
}
