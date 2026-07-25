using System.Globalization;

using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

internal enum GlGpuTimerScope
{
    Setup = 0,
    Shadow = 1,
    Scene = 2,
    Post = 3,
    Overlay = 4,
    CloudTrace = 5,
    CloudTemporal = 6,
    CloudUpsample = 7,
    GodRayInject = 8,
    GodRayIntegrate = 9,
    GodRayResolve = 10,
    Taa = 11,
    DepthPrepass = 12,
    HiZ = 13,

    // CPU-only detail scopes (nest under a pass scope; not used by GL timer queries).
    SetupBones = 14,
    SetupBounds = 15,
    ShadowTerrainCull = 16,
    TerrainStream = 17,
    TerrainDraw = 18,
    SubjectDraw = 19,
}

internal static class GlGpuTimerScopes
{
    /// <summary>Pass scopes that own GPU <c>GL_TIME_ELAPSED</c> queries.</summary>
    public const int PassScopeCount = 14;

    /// <summary>Pass scopes plus CPU-only detail buckets.</summary>
    public const int CpuScopeCount = 20;

    public static bool IsCpuDetail(GlGpuTimerScope scope) => (int)scope >= PassScopeCount;
}

internal readonly record struct GlGpuTimingSnapshot(
    double SetupMs,
    double ShadowMs,
    double SceneMs,
    double PostMs,
    double OverlayMs,
    double CloudTraceMs,
    double CloudTemporalMs,
    double CloudUpsampleMs,
    double GodRayInjectMs,
    double GodRayIntegrateMs,
    double GodRayResolveMs,
    double TaaMs,
    double DepthPrepassMs = 0.0,
    double HiZMs = 0.0,
    double SetupBonesMs = 0.0,
    double SetupBoundsMs = 0.0,
    double ShadowTerrainCullMs = 0.0,
    double TerrainStreamMs = 0.0,
    double TerrainDrawMs = 0.0,
    double SubjectDrawMs = 0.0)
{
    public GlGpuTimingSnapshot(double SetupMs, double ShadowMs, double SceneMs, double PostMs, double OverlayMs)
        : this(SetupMs, ShadowMs, SceneMs, PostMs, OverlayMs, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
    {
    }

    public double GodRaysMs => GodRayInjectMs + GodRayIntegrateMs + GodRayResolveMs;

    public double PostTotalMs =>
        PostMs +
        CloudTraceMs +
        CloudTemporalMs +
        CloudUpsampleMs +
        GodRaysMs +
        TaaMs;

    /// <summary>Wall-clock pass totals only; CPU detail scopes are subsets and excluded.</summary>
    public double TotalMs =>
        SetupMs + ShadowMs + DepthPrepassMs + HiZMs + SceneMs + PostTotalMs + OverlayMs;

    public string FormatHudLine(bool expanded = false) => FormatHudLine("GPU", expanded);

    public string FormatHudLine(
        string label,
        bool expanded = false,
        GlGpuTimingHudLinger? linger = null,
        double nowSeconds = 0.0)
    {
        if (!expanded)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0} ms", label, TotalMs);
        }

        // Expanded HUD: vertical list of full pass names, omitting scopes that round to 0.0 ms
        // (or briefly lingering after they drop below the display threshold).
        // CPU detail lines nest under their parent pass when non-zero (GPU leaves them at 0).
        var lines = new List<string>(24)
        {
            string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0} ms", label, TotalMs),
        };
        AppendHudPass(lines, "Setup", SetupMs, (int)GlGpuTimerScope.Setup, linger, nowSeconds);
        AppendHudPass(lines, "  Bones", SetupBonesMs, (int)GlGpuTimerScope.SetupBones, linger, nowSeconds);
        AppendHudPass(lines, "  Bounds", SetupBoundsMs, (int)GlGpuTimerScope.SetupBounds, linger, nowSeconds);
        AppendHudPass(lines, "Shadow", ShadowMs, (int)GlGpuTimerScope.Shadow, linger, nowSeconds);
        AppendHudPass(lines, "  Terrain Cull", ShadowTerrainCullMs, (int)GlGpuTimerScope.ShadowTerrainCull, linger, nowSeconds);
        AppendHudPass(lines, "Depth Prepass", DepthPrepassMs, (int)GlGpuTimerScope.DepthPrepass, linger, nowSeconds);
        AppendHudPass(lines, "Hi-Z", HiZMs, (int)GlGpuTimerScope.HiZ, linger, nowSeconds);
        AppendHudPass(lines, "Scene", SceneMs, (int)GlGpuTimerScope.Scene, linger, nowSeconds);
        AppendHudPass(lines, "  Terrain Stream", TerrainStreamMs, (int)GlGpuTimerScope.TerrainStream, linger, nowSeconds);
        AppendHudPass(lines, "  Terrain Draw", TerrainDrawMs, (int)GlGpuTimerScope.TerrainDraw, linger, nowSeconds);
        AppendHudPass(lines, "  Subject", SubjectDrawMs, (int)GlGpuTimerScope.SubjectDraw, linger, nowSeconds);
        AppendHudPass(lines, "Cloud Trace", CloudTraceMs, (int)GlGpuTimerScope.CloudTrace, linger, nowSeconds);
        AppendHudPass(lines, "Cloud Temporal", CloudTemporalMs, (int)GlGpuTimerScope.CloudTemporal, linger, nowSeconds);
        AppendHudPass(lines, "God Ray Inject", GodRayInjectMs, (int)GlGpuTimerScope.GodRayInject, linger, nowSeconds);
        AppendHudPass(lines, "God Ray Integrate", GodRayIntegrateMs, (int)GlGpuTimerScope.GodRayIntegrate, linger, nowSeconds);
        AppendHudPass(lines, "God Ray Resolve", GodRayResolveMs, (int)GlGpuTimerScope.GodRayResolve, linger, nowSeconds);
        AppendHudPass(lines, "Cloud Upsample", CloudUpsampleMs, (int)GlGpuTimerScope.CloudUpsample, linger, nowSeconds);
        AppendHudPass(lines, "TAA", TaaMs, (int)GlGpuTimerScope.Taa, linger, nowSeconds);
        AppendHudPass(lines, "Post", PostMs, (int)GlGpuTimerScope.Post, linger, nowSeconds);
        AppendHudPass(lines, "Overlay", OverlayMs, (int)GlGpuTimerScope.Overlay, linger, nowSeconds);
        return string.Join('\n', lines);
    }

    private static void AppendHudPass(
        List<string> lines,
        string name,
        double ms,
        int passId,
        GlGpuTimingHudLinger? linger,
        double nowSeconds)
    {
        var show = linger?.ShouldShow(passId, ms, nowSeconds) ?? ms >= GlGpuTimingHudLinger.MinDisplayMs;
        if (!show)
        {
            return;
        }

        lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0} ms", name, ms));
    }

    public string FormatDiagnostic() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "setup={0:0.###}ms, shadow={1:0.###}ms, depthPrepass={2:0.###}ms, hiZ={3:0.###}ms, scene={4:0.###}ms, " +
            "cloudTrace={5:0.###}ms, cloudTemporal={6:0.###}ms, cloudUpsample={7:0.###}ms, " +
            "godRayInject={8:0.###}ms, godRayIntegrate={9:0.###}ms, godRayResolve={10:0.###}ms, " +
            "taa={11:0.###}ms, post={13:0.###}ms, postOther={12:0.###}ms, overlay={14:0.###}ms, total={15:0.###}ms",
            SetupMs,
            ShadowMs,
            DepthPrepassMs,
            HiZMs,
            SceneMs,
            CloudTraceMs,
            CloudTemporalMs,
            CloudUpsampleMs,
            GodRayInjectMs,
            GodRayIntegrateMs,
            GodRayResolveMs,
            TaaMs,
            PostMs,
            PostTotalMs,
            OverlayMs,
            TotalMs);
}

internal sealed class GlGpuTimerProfiler : IDisposable
{
    private const int ScopeCount = GlGpuTimerScopes.PassScopeCount;
    private const int FrameSlots = 5;
    private const uint TimeElapsed = 0x88BF;
    private const uint QueryResult = 0x8866;
    private const uint QueryResultAvailable = 0x8867;
    private const double NanosecondsToMilliseconds = 1.0 / 1_000_000.0;

    private readonly GL _gl;
    private readonly uint[,] _queries = new uint[FrameSlots, ScopeCount];
    private readonly bool[,] _pending = new bool[FrameSlots, ScopeCount];
    private int _nextFrameSlot;
    private int _activeFrameSlot = -1;
    private int _activeScope = -1;
    private bool _disposed;
    private GlGpuTimingSnapshot? _latest;

    public GlGpuTimerProfiler(GL gl)
    {
        _gl = gl;
        for (var frame = 0; frame < FrameSlots; frame++)
        {
            for (var scope = 0; scope < ScopeCount; scope++)
            {
                _queries[frame, scope] = _gl.GenQuery();
            }
        }
    }

    public bool BeginFrame()
    {
        if (_disposed)
        {
            return false;
        }

        PollCompletedFrames();
        if (!TryFindFreeFrameSlot(out var frameSlot))
        {
            _activeFrameSlot = -1;
            return false;
        }

        _activeFrameSlot = frameSlot;
        _activeScope = -1;
        _nextFrameSlot = (frameSlot + 1) % FrameSlots;
        return true;
    }

    public bool TryBeginScope(GlGpuTimerScope scope)
    {
        if (_disposed ||
            _activeFrameSlot < 0 ||
            _activeScope >= 0 ||
            GlGpuTimerScopes.IsCpuDetail(scope))
        {
            return false;
        }

        var scopeIndex = (int)scope;
        _gl.BeginQuery((QueryTarget)TimeElapsed, _queries[_activeFrameSlot, scopeIndex]);
        _activeScope = scopeIndex;
        return true;
    }

    public void EndScope(GlGpuTimerScope scope)
    {
        if (_disposed || _activeFrameSlot < 0 || _activeScope != (int)scope)
        {
            return;
        }

        _gl.EndQuery((QueryTarget)TimeElapsed);
        _pending[_activeFrameSlot, _activeScope] = true;
        _activeScope = -1;
    }

    public void EndFrame()
    {
        if (_disposed)
        {
            return;
        }

        if (_activeScope >= 0)
        {
            _gl.EndQuery((QueryTarget)TimeElapsed);
            if (_activeFrameSlot >= 0)
            {
                _pending[_activeFrameSlot, _activeScope] = true;
            }
        }

        _activeScope = -1;
        _activeFrameSlot = -1;
        PollCompletedFrames();
    }

    public bool TryTakeLatestSnapshot(out GlGpuTimingSnapshot snapshot)
    {
        if (_latest is { } latest)
        {
            snapshot = latest;
            _latest = null;
            return true;
        }

        snapshot = default;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_activeScope >= 0)
        {
            _gl.EndQuery((QueryTarget)TimeElapsed);
        }

        _activeScope = -1;
        _activeFrameSlot = -1;
        for (var frame = 0; frame < FrameSlots; frame++)
        {
            for (var scope = 0; scope < ScopeCount; scope++)
            {
                var query = _queries[frame, scope];
                if (query != 0)
                {
                    _gl.DeleteQuery(query);
                    _queries[frame, scope] = 0;
                }
            }
        }

        _disposed = true;
    }

    private bool TryFindFreeFrameSlot(out int frameSlot)
    {
        for (var i = 0; i < FrameSlots; i++)
        {
            var candidate = (_nextFrameSlot + i) % FrameSlots;
            if (!HasPendingQueries(candidate))
            {
                frameSlot = candidate;
                return true;
            }
        }

        frameSlot = -1;
        return false;
    }

    private bool HasPendingQueries(int frameSlot)
    {
        for (var scope = 0; scope < ScopeCount; scope++)
        {
            if (_pending[frameSlot, scope])
            {
                return true;
            }
        }

        return false;
    }

    private void PollCompletedFrames()
    {
        for (var frame = 0; frame < FrameSlots; frame++)
        {
            if (!HasPendingQueries(frame) || !ArePendingQueriesAvailable(frame))
            {
                continue;
            }

            var elapsed = new ulong[ScopeCount];
            for (var scope = 0; scope < ScopeCount; scope++)
            {
                if (!_pending[frame, scope])
                {
                    continue;
                }

                _gl.GetQueryObject(_queries[frame, scope], (QueryObjectParameterName)QueryResult, out elapsed[scope]);
                _pending[frame, scope] = false;
            }

            _latest = new GlGpuTimingSnapshot(
                elapsed[(int)GlGpuTimerScope.Setup] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.Shadow] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.Scene] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.Post] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.Overlay] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.CloudTrace] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.CloudTemporal] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.CloudUpsample] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.GodRayInject] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.GodRayIntegrate] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.GodRayResolve] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.Taa] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.DepthPrepass] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.HiZ] * NanosecondsToMilliseconds);
        }
    }

    private bool ArePendingQueriesAvailable(int frameSlot)
    {
        for (var scope = 0; scope < ScopeCount; scope++)
        {
            if (!_pending[frameSlot, scope])
            {
                continue;
            }

            _gl.GetQueryObject(_queries[frameSlot, scope], (QueryObjectParameterName)QueryResultAvailable, out int available);
            if (available == 0)
            {
                return false;
            }
        }

        return true;
    }
}
