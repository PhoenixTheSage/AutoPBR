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
    GodRays = 6,
    CloudComposite = 7,
}

internal readonly record struct GlGpuTimingSnapshot(
    double SetupMs,
    double ShadowMs,
    double SceneMs,
    double PostMs,
    double OverlayMs,
    double CloudTraceMs,
    double GodRaysMs,
    double CloudCompositeMs)
{
    public GlGpuTimingSnapshot(double SetupMs, double ShadowMs, double SceneMs, double PostMs, double OverlayMs)
        : this(SetupMs, ShadowMs, SceneMs, PostMs, OverlayMs, 0.0, 0.0, 0.0)
    {
    }

    public double PostTotalMs => PostMs + CloudTraceMs + GodRaysMs + CloudCompositeMs;
    public double TotalMs => SetupMs + ShadowMs + SceneMs + PostTotalMs + OverlayMs;

    public string FormatHudLine(bool expanded = false) =>
        expanded
            ? string.Format(
                CultureInfo.InvariantCulture,
                "GPU {0:0.0} ms | set {1:0.0} sh {2:0.0} scn {3:0.0} post {4:0.0} " +
                "(cloud {5:0.0}+{6:0.0}, rays {7:0.0}) ovl {8:0.0}",
                TotalMs,
                SetupMs,
                ShadowMs,
                SceneMs,
                PostTotalMs,
                CloudTraceMs,
                CloudCompositeMs,
                GodRaysMs,
                OverlayMs)
            : string.Format(
                CultureInfo.InvariantCulture,
                "GPU {0:0.0} ms",
                TotalMs);

    public string FormatDiagnostic() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "setup={0:0.###}ms, shadow={1:0.###}ms, scene={2:0.###}ms, " +
            "cloudTrace={3:0.###}ms, cloudComposite={4:0.###}ms, godRays={5:0.###}ms, " +
            "post={7:0.###}ms, postOther={6:0.###}ms, overlay={8:0.###}ms, total={9:0.###}ms",
            SetupMs,
            ShadowMs,
            SceneMs,
            CloudTraceMs,
            CloudCompositeMs,
            GodRaysMs,
            PostMs,
            PostTotalMs,
            OverlayMs,
            TotalMs);
}

internal sealed class GlGpuTimerProfiler : IDisposable
{
    private const int ScopeCount = 8;
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
        if (_disposed || _activeFrameSlot < 0 || _activeScope >= 0)
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
                elapsed[(int)GlGpuTimerScope.GodRays] * NanosecondsToMilliseconds,
                elapsed[(int)GlGpuTimerScope.CloudComposite] * NanosecondsToMilliseconds);
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
