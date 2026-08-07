namespace AutoPBR.App.Rendering.Scene;

/// <summary>Revision identity carried by asynchronous terrain work.</summary>
public readonly record struct TerrainDemandToken(long ContentGeneration, long DemandRevision);

/// <summary>A deterministic camera target and its incremental leaf-set delta.</summary>
public sealed record TerrainDemandUpdate(
    TerrainDemandToken Token,
    TerrainChunkKey CameraChunk,
    int HardRadiusChunks,
    int LodRingChunks,
    IReadOnlySet<TerrainResidencyKey> TargetCut,
    IReadOnlySet<TerrainResidencyKey> Entered,
    IReadOnlySet<TerrainResidencyKey> Exited,
    bool IsTeleport);

/// <summary>
/// Separates content identity from camera demand identity. Content changes invalidate every
/// payload; demand changes invalidate only work no longer useful to the current target cut.
/// </summary>
public sealed class TerrainDemandTracker
{
    private readonly object _sync = new();
    private readonly TerrainTargetCutBuilder _targetBuilder;
    private long _contentGeneration;
    private long _demandRevision;
    private TerrainDemandUpdate? _current;

    public TerrainDemandTracker(
        long initialContentGeneration = 1,
        TerrainTargetCutBuilder? targetBuilder = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialContentGeneration);

        _contentGeneration = initialContentGeneration;
        _targetBuilder = targetBuilder ?? new TerrainTargetCutBuilder();
    }

    public long ContentGeneration
    {
        get
        {
            lock (_sync)
            {
                return _contentGeneration;
            }
        }
    }

    public long DemandRevision
    {
        get
        {
            lock (_sync)
            {
                return _demandRevision;
            }
        }
    }

    public TerrainDemandToken CurrentToken
    {
        get
        {
            lock (_sync)
            {
                return new TerrainDemandToken(_contentGeneration, _demandRevision);
            }
        }
    }

    public TerrainDemandUpdate? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Produces a new revision only when camera/profile demand changes. Entered/exited sets are
    /// deterministic and can be consumed without rescanning all active coverage.
    /// </summary>
    public TerrainDemandUpdate UpdateCameraTarget(
        TerrainChunkKey cameraChunk,
        int hardRadiusChunks,
        int lodRingChunks)
    {
        hardRadiusChunks = Math.Max(0, hardRadiusChunks);
        lodRingChunks = Math.Max(0, lodRingChunks);

        lock (_sync)
        {
            if (_current is not null &&
                _current.CameraChunk == cameraChunk &&
                _current.HardRadiusChunks == hardRadiusChunks &&
                _current.LodRingChunks == lodRingChunks)
            {
                return _current;
            }

            var next = _targetBuilder.Build(cameraChunk, hardRadiusChunks, lodRingChunks);
            var previous = _current?.TargetCut;
            var entered = previous is null
                ? new HashSet<TerrainResidencyKey>(next)
                : next.Where(key => !previous.Contains(key)).ToHashSet();
            var exited = previous is null
                ? []
                : previous.Where(key => !next.Contains(key)).ToHashSet();

            _demandRevision = checked(_demandRevision + 1);
            var teleport = IsTeleport(
                _current,
                cameraChunk,
                checked(hardRadiusChunks + lodRingChunks));
            _current = new TerrainDemandUpdate(
                new TerrainDemandToken(_contentGeneration, _demandRevision),
                cameraChunk,
                hardRadiusChunks,
                lodRingChunks,
                next,
                entered,
                exited,
                teleport);
            return _current;
        }
    }

    /// <summary>
    /// Starts a new content namespace while retaining the old drawable cut as fallback.
    /// The next publications must carry the returned token.
    /// </summary>
    public TerrainDemandToken AdvanceContentGeneration()
    {
        lock (_sync)
        {
            _contentGeneration = checked(_contentGeneration + 1);
            _demandRevision = checked(_demandRevision + 1);
            if (_current is not null)
            {
                _current = _current with
                {
                    Token = new TerrainDemandToken(_contentGeneration, _demandRevision),
                    Entered = new HashSet<TerrainResidencyKey>(_current.TargetCut),
                    Exited = new HashSet<TerrainResidencyKey>(),
                };
            }

            return new TerrainDemandToken(_contentGeneration, _demandRevision);
        }
    }

    public bool IsCurrent(TerrainDemandToken token)
    {
        lock (_sync)
        {
            return token.ContentGeneration == _contentGeneration &&
                token.DemandRevision == _demandRevision;
        }
    }

    public bool IsContentCurrent(long contentGeneration)
    {
        lock (_sync)
        {
            return contentGeneration == _contentGeneration;
        }
    }

    /// <summary>
    /// Demand-stale work is reusable only when its content is current and the key remains targeted.
    /// </summary>
    public bool IsStillDemanded(TerrainResidencyKey key, TerrainDemandToken token)
    {
        lock (_sync)
        {
            if (token.ContentGeneration != _contentGeneration || _current is null)
            {
                return false;
            }

            return _current.TargetCut.Contains(key);
        }
    }

    private static bool IsTeleport(
        TerrainDemandUpdate? previous,
        TerrainChunkKey nextCamera,
        int nextRadius)
    {
        if (previous is null)
        {
            return false;
        }

        var previousRadius = checked(previous.HardRadiusChunks + previous.LodRingChunks);
        var overlapReach = checked(previousRadius + nextRadius);
        return previous.CameraChunk.ChebyshevDistanceTo(nextCamera) > overlapReach;
    }
}
