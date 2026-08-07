namespace AutoPBR.App.Rendering.Scene;

public enum TerrainCoverageNodeState
{
    Active,
    TransitionOutgoing,
    TransitionIncoming,
    Retiring,
}

public enum TerrainCoverageTransactionKind
{
    Split,
    Merge,
    Replace,
}

public enum TerrainCoverageTransactionState
{
    Preparing,
    Committed,
    Aborted,
}

public sealed record TerrainCoverageNode(
    TerrainResidencyKey Key,
    TerrainCoverageNodeState State,
    bool IsDrawable,
    long ResourceBytes,
    long ContentGeneration,
    long DemandRevision,
    long? TransactionId);

public sealed class TerrainCoverageTransaction
{
    internal TerrainCoverageTransaction(
        long id,
        TerrainCoverageTransactionKind kind,
        TerrainDemandToken token,
        IReadOnlyList<TerrainResidencyKey> outgoing,
        IReadOnlyDictionary<TerrainResidencyKey, long> incomingReservations)
    {
        Id = id;
        Kind = kind;
        Token = token;
        Outgoing = outgoing;
        IncomingReservations = incomingReservations;
    }

    public long Id { get; }

    public TerrainCoverageTransactionKind Kind { get; }

    public TerrainDemandToken Token { get; }

    public TerrainCoverageTransactionState State { get; internal set; } =
        TerrainCoverageTransactionState.Preparing;

    public IReadOnlyList<TerrainResidencyKey> Outgoing { get; }

    public IReadOnlyDictionary<TerrainResidencyKey, long> IncomingReservations { get; }
}

/// <summary>
/// CPU authority for drawable terrain coverage and split/merge admission. Resource claims are
/// reserved before topology changes, old coverage remains drawable until atomic commit, and
/// allocations that may have been drawn remain unavailable until explicitly released.
/// </summary>
public sealed class TerrainCoverageGraph
{
    private sealed class MutableNode
    {
        public required TerrainResidencyKey Key { get; init; }
        public required TerrainCoverageNodeState State { get; set; }
        public required bool IsDrawable { get; set; }
        public required long ResourceBytes { get; set; }
        public required long ContentGeneration { get; set; }
        public required long DemandRevision { get; set; }
        public long? TransactionId { get; set; }

        public TerrainCoverageNode Snapshot() =>
            new(
                Key,
                State,
                IsDrawable,
                ResourceBytes,
                ContentGeneration,
                DemandRevision,
                TransactionId);
    }

    private readonly object _sync = new();
    private readonly Dictionary<TerrainResidencyKey, MutableNode> _nodes = [];
    private readonly Dictionary<long, TerrainCoverageTransaction> _transactions = [];
    private long _nextTransactionId;
    private long _activeBytes;
    private long _transitionBytes;
    private long _retiringBytes;
    private long _reservedBytes;
    private TerrainDemandToken _currentToken;

    public TerrainCoverageGraph(
        long resourceCapacityBytes,
        long contentGeneration = 1,
        long demandRevision = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resourceCapacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(contentGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(demandRevision);

        ResourceCapacityBytes = resourceCapacityBytes;
        _currentToken = new TerrainDemandToken(contentGeneration, demandRevision);
    }

    public long ResourceCapacityBytes { get; }

    public TerrainDemandToken CurrentToken
    {
        get
        {
            lock (_sync)
            {
                return _currentToken;
            }
        }
    }

    public long ActiveBytes
    {
        get
        {
            lock (_sync)
            {
                return _activeBytes;
            }
        }
    }

    public long TransitionBytes
    {
        get
        {
            lock (_sync)
            {
                return _transitionBytes;
            }
        }
    }

    public long RetiringBytes
    {
        get
        {
            lock (_sync)
            {
                return _retiringBytes;
            }
        }
    }

    public long ReservedBytes
    {
        get
        {
            lock (_sync)
            {
                return _reservedBytes;
            }
        }
    }

    public long ClaimedBytes
    {
        get
        {
            lock (_sync)
            {
                return ClaimedBytesUnsafe();
            }
        }
    }

    public long AvailableBytes
    {
        get
        {
            lock (_sync)
            {
                return ResourceCapacityBytes - ClaimedBytesUnsafe();
            }
        }
    }

    public IReadOnlySet<TerrainResidencyKey> ActiveCut
    {
        get
        {
            lock (_sync)
            {
                return _nodes.Values
                    .Where(node =>
                        node.State is TerrainCoverageNodeState.Active or
                            TerrainCoverageNodeState.TransitionOutgoing)
                    .Select(node => node.Key)
                    .ToHashSet();
            }
        }
    }

    public IReadOnlySet<TerrainResidencyKey> TransitionOverlay
    {
        get
        {
            lock (_sync)
            {
                return _nodes.Values
                    .Where(node => node.State == TerrainCoverageNodeState.TransitionIncoming)
                    .Select(node => node.Key)
                    .ToHashSet();
            }
        }
    }

    public void Initialize(
        IEnumerable<TerrainResidencyKey> activeCut,
        long bytesPerNode,
        TerrainDemandToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerNode);

        ArgumentNullException.ThrowIfNull(activeCut);
        Initialize(activeCut.ToDictionary(key => key, _ => bytesPerNode), token);
    }

    public void Initialize(
        IReadOnlyDictionary<TerrainResidencyKey, long> activeResources,
        TerrainDemandToken token)
    {
        ArgumentNullException.ThrowIfNull(activeResources);
        TerrainTargetCutBuilder.ValidateStrictCut(activeResources.Keys);
        ValidateToken(token);

        var bytes = SumPositiveResources(activeResources, nameof(activeResources));
        if (bytes > ResourceCapacityBytes)
        {
            throw new InvalidOperationException(
                $"Initial terrain cut requires {bytes} bytes but capacity is {ResourceCapacityBytes}.");
        }

        lock (_sync)
        {
            if (_nodes.Count != 0 || _transactions.Count != 0)
            {
                throw new InvalidOperationException("Terrain coverage graph is already initialized.");
            }

            _currentToken = token;
            foreach (var (key, resourceBytes) in activeResources)
            {
                _nodes.Add(
                    key,
                    new MutableNode
                    {
                        Key = key,
                        State = TerrainCoverageNodeState.Active,
                        IsDrawable = true,
                        ResourceBytes = resourceBytes,
                        ContentGeneration = token.ContentGeneration,
                        DemandRevision = token.DemandRevision,
                    });
            }

            _activeBytes = bytes;
        }
    }

    public bool TryBeginSplit(
        TerrainResidencyKey parent,
        long bytesPerChild,
        TerrainDemandToken token,
        out TerrainCoverageTransaction? transaction,
        out string? failure)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerChild);

        var reservations = TerrainTargetCutBuilder.ChildrenOf(parent)
            .ToDictionary(key => key, _ => bytesPerChild);
        return TryBeginSplit(parent, reservations, token, out transaction, out failure);
    }

    public bool TryBeginSplit(
        TerrainResidencyKey parent,
        IReadOnlyDictionary<TerrainResidencyKey, long> childReservations,
        TerrainDemandToken token,
        out TerrainCoverageTransaction? transaction,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(childReservations);
        IReadOnlyList<TerrainResidencyKey> expectedChildren;
        try
        {
            expectedChildren = TerrainTargetCutBuilder.ChildrenOf(parent);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            transaction = null;
            failure = exception.Message;
            return false;
        }

        if (childReservations.Count != expectedChildren.Count ||
            expectedChildren.Any(key => !childReservations.ContainsKey(key)))
        {
            transaction = null;
            failure = "A split requires reservations for all four immediate children.";
            return false;
        }

        return TryBeginTransaction(
            TerrainCoverageTransactionKind.Split,
            [parent],
            childReservations,
            token,
            out transaction,
            out failure);
    }

    public bool TryBeginMerge(
        TerrainResidencyKey parent,
        long parentReservationBytes,
        TerrainDemandToken token,
        out TerrainCoverageTransaction? transaction,
        out string? failure)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentReservationBytes);

        IReadOnlyList<TerrainResidencyKey> children;
        try
        {
            children = TerrainTargetCutBuilder.ChildrenOf(parent);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            transaction = null;
            failure = exception.Message;
            return false;
        }

        return TryBeginTransaction(
            TerrainCoverageTransactionKind.Merge,
            children,
            new Dictionary<TerrainResidencyKey, long> { [parent] = parentReservationBytes },
            token,
            out transaction,
            out failure);
    }

    /// <summary>
    /// Stages a disjoint cut (for example after a teleport) and switches required topology only
    /// after the complete replacement is drawable.
    /// </summary>
    public bool TryBeginCutReplacement(
        IReadOnlyDictionary<TerrainResidencyKey, long> replacementReservations,
        TerrainDemandToken token,
        out TerrainCoverageTransaction? transaction,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(replacementReservations);
        try
        {
            TerrainTargetCutBuilder.ValidateStrictCut(replacementReservations.Keys);
        }
        catch (ArgumentException exception)
        {
            transaction = null;
            failure = exception.Message;
            return false;
        }

        lock (_sync)
        {
            var outgoing = _nodes.Values
                .Where(node => node.State == TerrainCoverageNodeState.Active)
                .Select(node => node.Key)
                .ToArray();
            if (replacementReservations.Keys.Any(_nodes.ContainsKey))
            {
                transaction = null;
                failure = "Cut replacement currently requires a disjoint target.";
                return false;
            }

            return TryBeginTransactionUnsafe(
                TerrainCoverageTransactionKind.Replace,
                outgoing,
                replacementReservations,
                token,
                out transaction,
                out failure);
        }
    }

    /// <summary>
    /// Publishes a real allocation. Stale revisions and allocations larger than their reservation
    /// are rejected without changing coverage or accounting.
    /// </summary>
    public bool TryPublishDrawable(
        long transactionId,
        TerrainResidencyKey key,
        long actualResourceBytes,
        TerrainDemandToken token,
        out string? failure)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actualResourceBytes);

        lock (_sync)
        {
            if (!_transactions.TryGetValue(transactionId, out var transaction) ||
                transaction.State != TerrainCoverageTransactionState.Preparing)
            {
                failure = "Terrain transaction is not preparing.";
                return false;
            }

            if (token != transaction.Token || token != _currentToken)
            {
                failure = "Terrain publication carries a stale content or demand revision.";
                return false;
            }

            if (!transaction.IncomingReservations.TryGetValue(key, out var reservation))
            {
                failure = $"Terrain key {key} is not incoming for transaction {transactionId}.";
                return false;
            }

            if (_nodes.TryGetValue(key, out var existing) && existing.IsDrawable)
            {
                failure = $"Terrain key {key} is already drawable.";
                return false;
            }

            if (actualResourceBytes > reservation)
            {
                failure =
                    $"Terrain key {key} needs {actualResourceBytes} bytes but reserved {reservation}.";
                return false;
            }

            _reservedBytes -= reservation;
            _transitionBytes = checked(_transitionBytes + actualResourceBytes);
            _nodes[key] = new MutableNode
            {
                Key = key,
                State = TerrainCoverageNodeState.TransitionIncoming,
                IsDrawable = true,
                ResourceBytes = actualResourceBytes,
                ContentGeneration = token.ContentGeneration,
                DemandRevision = token.DemandRevision,
                TransactionId = transactionId,
            };
            failure = null;
            return true;
        }
    }

    public bool CanCommit(long transactionId)
    {
        lock (_sync)
        {
            return _transactions.TryGetValue(transactionId, out var transaction) &&
                transaction.State == TerrainCoverageTransactionState.Preparing &&
                transaction.Token == _currentToken &&
                transaction.IncomingReservations.Keys.All(
                    key => _nodes.TryGetValue(key, out var node) &&
                        node.State == TerrainCoverageNodeState.TransitionIncoming &&
                        node.IsDrawable &&
                        node.TransactionId == transactionId);
        }
    }

    public bool TryCommit(long transactionId, out string? failure)
    {
        lock (_sync)
        {
            if (!_transactions.TryGetValue(transactionId, out var transaction) ||
                transaction.State != TerrainCoverageTransactionState.Preparing)
            {
                failure = "Terrain transaction is not preparing.";
                return false;
            }

            if (transaction.Token != _currentToken)
            {
                failure = "Terrain transaction carries a stale content or demand revision.";
                return false;
            }

            foreach (var key in transaction.IncomingReservations.Keys)
            {
                if (!_nodes.TryGetValue(key, out var incoming) ||
                    incoming.State != TerrainCoverageNodeState.TransitionIncoming ||
                    !incoming.IsDrawable ||
                    incoming.TransactionId != transactionId)
                {
                    failure = $"Incoming terrain key {key} is not drawable.";
                    return false;
                }
            }

            foreach (var key in transaction.Outgoing)
            {
                var outgoing = _nodes[key];
                outgoing.State = TerrainCoverageNodeState.Retiring;
                outgoing.IsDrawable = false;
                outgoing.TransactionId = null;
                _activeBytes -= outgoing.ResourceBytes;
                _retiringBytes = checked(_retiringBytes + outgoing.ResourceBytes);
            }

            foreach (var key in transaction.IncomingReservations.Keys)
            {
                var incoming = _nodes[key];
                incoming.State = TerrainCoverageNodeState.Active;
                incoming.TransactionId = null;
                _transitionBytes -= incoming.ResourceBytes;
                _activeBytes = checked(_activeBytes + incoming.ResourceBytes);
            }

            transaction.State = TerrainCoverageTransactionState.Committed;
            _transactions.Remove(transactionId);
            failure = null;
            return true;
        }
    }

    /// <summary>
    /// Restores outgoing coverage. Drawable incoming allocations move to retiring because they may
    /// already have participated in a transition draw; unconsumed reservations are released.
    /// </summary>
    public bool AbortTransaction(long transactionId, out string? failure)
    {
        lock (_sync)
        {
            if (!_transactions.TryGetValue(transactionId, out var transaction) ||
                transaction.State != TerrainCoverageTransactionState.Preparing)
            {
                failure = "Terrain transaction is not preparing.";
                return false;
            }

            AbortTransactionUnsafe(transaction);
            failure = null;
            return true;
        }
    }

    /// <summary>Advances authority and safely aborts every now-stale transition.</summary>
    public void AdvanceRevisions(TerrainDemandToken token)
    {
        lock (_sync)
        {
            if (token.ContentGeneration < _currentToken.ContentGeneration ||
                token.DemandRevision < _currentToken.DemandRevision)
            {
                throw new ArgumentOutOfRangeException(nameof(token), "Terrain revisions cannot move backward.");
            }

            foreach (var transaction in _transactions.Values.ToArray())
            {
                AbortTransactionUnsafe(transaction);
            }

            _currentToken = token;
        }
    }

    /// <summary>Signals that the GPU lifetime fence for a retired allocation has completed.</summary>
    public bool ReleaseRetired(TerrainResidencyKey key)
    {
        lock (_sync)
        {
            if (!_nodes.TryGetValue(key, out var node) ||
                node.State != TerrainCoverageNodeState.Retiring)
            {
                return false;
            }

            _retiringBytes -= node.ResourceBytes;
            _nodes.Remove(key);
            return true;
        }
    }

    public int ReleaseAllRetired()
    {
        lock (_sync)
        {
            var retired = _nodes.Values
                .Where(node => node.State == TerrainCoverageNodeState.Retiring)
                .Select(node => node.Key)
                .ToArray();
            foreach (var key in retired)
            {
                _retiringBytes -= _nodes[key].ResourceBytes;
                _nodes.Remove(key);
            }

            return retired.Length;
        }
    }

    public TerrainCoverageNode? GetNode(TerrainResidencyKey key)
    {
        lock (_sync)
        {
            return _nodes.TryGetValue(key, out var node) ? node.Snapshot() : null;
        }
    }

    public IReadOnlyList<TerrainCoverageNode> SnapshotNodes()
    {
        lock (_sync)
        {
            return _nodes.Values
                .OrderBy(node => node.Key.LodLevel)
                .ThenBy(node => node.Key.Z)
                .ThenBy(node => node.Key.X)
                .Select(node => node.Snapshot())
                .ToArray();
        }
    }

    public int DrawableCoverageCount(int chunkX, int chunkZ, bool includeTransitionOverlay = true)
    {
        lock (_sync)
        {
            return _nodes.Values.Count(node =>
                node.IsDrawable &&
                (includeTransitionOverlay ||
                    node.State != TerrainCoverageNodeState.TransitionIncoming) &&
                CoversCell(node.Key, chunkX, chunkZ));
        }
    }

    public bool IsCellCovered(int chunkX, int chunkZ) =>
        DrawableCoverageCount(chunkX, chunkZ) > 0;

    public void AssertCoverage(
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks,
        int lodRingChunks)
    {
        hardRadiusChunks = Math.Max(0, hardRadiusChunks);
        lodRingChunks = Math.Max(0, lodRingChunks);
        var outerRadius = checked(hardRadiusChunks + lodRingChunks);
        lock (_sync)
        {
            AssertAccountingUnsafe();
            TerrainTargetCutBuilder.ValidateStrictCut(
                _nodes.Values
                    .Where(node =>
                        node.State is TerrainCoverageNodeState.Active or
                            TerrainCoverageNodeState.TransitionOutgoing)
                    .Select(node => node.Key));

            for (var z = cameraChunk.Z - outerRadius; z <= cameraChunk.Z + outerRadius; z++)
            {
                for (var x = cameraChunk.X - outerRadius; x <= cameraChunk.X + outerRadius; x++)
                {
                    if (!_nodes.Values.Any(node => node.IsDrawable && CoversCell(node.Key, x, z)))
                    {
                        throw new InvalidOperationException(
                            $"Required terrain cell ({x}, {z}) has no drawable coverage.");
                    }
                }
            }
        }
    }

    public void AssertInvariants(IEnumerable<TerrainResidencyKey> targetCut)
    {
        ArgumentNullException.ThrowIfNull(targetCut);
        var target = targetCut.ToHashSet();
        TerrainTargetCutBuilder.ValidateStrictCut(target);

        lock (_sync)
        {
            AssertAccountingUnsafe();
            TerrainTargetCutBuilder.ValidateStrictCut(
                _nodes.Values
                    .Where(node =>
                        node.State is TerrainCoverageNodeState.Active or
                            TerrainCoverageNodeState.TransitionOutgoing)
                    .Select(node => node.Key));

            foreach (var leaf in target)
            {
                var side = leaf.ChunksPerSide;
                for (var z = leaf.OriginChunkZ; z < leaf.OriginChunkZ + side; z++)
                {
                    for (var x = leaf.OriginChunkX; x < leaf.OriginChunkX + side; x++)
                    {
                        if (!_nodes.Values.Any(node => node.IsDrawable && CoversCell(node.Key, x, z)))
                        {
                            throw new InvalidOperationException(
                                $"Target terrain cell ({x}, {z}) has no drawable coverage.");
                        }
                    }
                }
            }
        }
    }

    private bool TryBeginTransaction(
        TerrainCoverageTransactionKind kind,
        IReadOnlyList<TerrainResidencyKey> outgoing,
        IReadOnlyDictionary<TerrainResidencyKey, long> incomingReservations,
        TerrainDemandToken token,
        out TerrainCoverageTransaction? transaction,
        out string? failure)
    {
        lock (_sync)
        {
            return TryBeginTransactionUnsafe(
                kind,
                outgoing,
                incomingReservations,
                token,
                out transaction,
                out failure);
        }
    }

    private bool TryBeginTransactionUnsafe(
        TerrainCoverageTransactionKind kind,
        IReadOnlyList<TerrainResidencyKey> outgoing,
        IReadOnlyDictionary<TerrainResidencyKey, long> incomingReservations,
        TerrainDemandToken token,
        out TerrainCoverageTransaction? transaction,
        out string? failure)
    {
        if (token != _currentToken)
        {
            transaction = null;
            failure = "Terrain transaction carries a stale content or demand revision.";
            return false;
        }

        foreach (var key in outgoing)
        {
            if (!_nodes.TryGetValue(key, out var node) ||
                node.State != TerrainCoverageNodeState.Active ||
                !node.IsDrawable)
            {
                transaction = null;
                failure = $"Outgoing terrain key {key} is not an active drawable leaf.";
                return false;
            }
        }

        foreach (var key in incomingReservations.Keys)
        {
            if (_nodes.ContainsKey(key))
            {
                transaction = null;
                failure = $"Incoming terrain key {key} already has coverage state.";
                return false;
            }
        }

        long reservationBytes;
        try
        {
            reservationBytes = SumPositiveResources(incomingReservations, nameof(incomingReservations));
        }
        catch (ArgumentException exception)
        {
            transaction = null;
            failure = exception.Message;
            return false;
        }

        if (reservationBytes > ResourceCapacityBytes - ClaimedBytesUnsafe())
        {
            transaction = null;
            failure =
                $"Terrain transition needs {reservationBytes} bytes but only " +
                $"{ResourceCapacityBytes - ClaimedBytesUnsafe()} are available.";
            return false;
        }

        var id = checked(++_nextTransactionId);
        var reservations = new Dictionary<TerrainResidencyKey, long>(incomingReservations);
        transaction = new TerrainCoverageTransaction(id, kind, token, outgoing, reservations);
        _transactions.Add(id, transaction);
        _reservedBytes = checked(_reservedBytes + reservationBytes);
        foreach (var key in outgoing)
        {
            var node = _nodes[key];
            node.State = TerrainCoverageNodeState.TransitionOutgoing;
            node.TransactionId = id;
        }

        failure = null;
        return true;
    }

    private void AbortTransactionUnsafe(TerrainCoverageTransaction transaction)
    {
        foreach (var key in transaction.Outgoing)
        {
            var outgoing = _nodes[key];
            outgoing.State = TerrainCoverageNodeState.Active;
            outgoing.TransactionId = null;
        }

        foreach (var (key, reservation) in transaction.IncomingReservations)
        {
            if (_nodes.TryGetValue(key, out var incoming) &&
                incoming.TransactionId == transaction.Id)
            {
                _transitionBytes -= incoming.ResourceBytes;
                _retiringBytes = checked(_retiringBytes + incoming.ResourceBytes);
                incoming.State = TerrainCoverageNodeState.Retiring;
                incoming.IsDrawable = false;
                incoming.TransactionId = null;
            }
            else
            {
                _reservedBytes -= reservation;
            }
        }

        transaction.State = TerrainCoverageTransactionState.Aborted;
        _transactions.Remove(transaction.Id);
    }

    private void AssertAccountingUnsafe()
    {
        var active = _nodes.Values
            .Where(node =>
                node.State is TerrainCoverageNodeState.Active or
                    TerrainCoverageNodeState.TransitionOutgoing)
            .Sum(node => node.ResourceBytes);
        var transition = _nodes.Values
            .Where(node => node.State == TerrainCoverageNodeState.TransitionIncoming)
            .Sum(node => node.ResourceBytes);
        var retiring = _nodes.Values
            .Where(node => node.State == TerrainCoverageNodeState.Retiring)
            .Sum(node => node.ResourceBytes);
        if (active != _activeBytes ||
            transition != _transitionBytes ||
            retiring != _retiringBytes ||
            ClaimedBytesUnsafe() > ResourceCapacityBytes)
        {
            throw new InvalidOperationException("Terrain coverage resource accounting invariant failed.");
        }
    }

    private long ClaimedBytesUnsafe() =>
        checked(_activeBytes + _transitionBytes + _retiringBytes + _reservedBytes);

    private static long SumPositiveResources(
        IReadOnlyDictionary<TerrainResidencyKey, long> resources,
        string parameterName)
    {
        long sum = 0;
        foreach (var (key, bytes) in resources)
        {
            if (bytes <= 0)
            {
                throw new ArgumentException(
                    $"Terrain key {key} must reserve a positive resource size.",
                    parameterName);
            }

            sum = checked(sum + bytes);
        }

        return sum;
    }

    private static bool CoversCell(TerrainResidencyKey key, int chunkX, int chunkZ)
    {
        var side = key.ChunksPerSide;
        return chunkX >= key.OriginChunkX &&
            chunkX < key.OriginChunkX + side &&
            chunkZ >= key.OriginChunkZ &&
            chunkZ < key.OriginChunkZ + side;
    }

    private static void ValidateToken(TerrainDemandToken token)
    {
        if (token.ContentGeneration < 0 || token.DemandRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }
    }
}
