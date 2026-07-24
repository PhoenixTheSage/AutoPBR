using System.Diagnostics;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Wall-clock CPU pass timings for the GL thread.
/// Pass scopes match <see cref="GlGpuTimerProfiler"/>; CPU-only detail scopes may nest under an open pass.
/// </summary>
internal sealed class GlCpuTimerProfiler
{
    private const int ScopeCount = GlGpuTimerScopes.CpuScopeCount;
    private readonly double[] _elapsedMs = new double[ScopeCount];
    private int _activeScope = -1;
    private int _activeDetailScope = -1;
    private long _scopeStartTimestamp;
    private long _detailStartTimestamp;
    private bool _frameActive;
    private GlGpuTimingSnapshot? _latest;

    public void BeginFrame()
    {
        if (_activeDetailScope >= 0)
        {
            EndActiveDetailScope();
        }

        if (_activeScope >= 0)
        {
            EndActiveScope();
        }

        Array.Clear(_elapsedMs);
        _activeScope = -1;
        _activeDetailScope = -1;
        _frameActive = true;
    }

    public bool TryBeginScope(GlGpuTimerScope scope)
    {
        if (!_frameActive)
        {
            return false;
        }

        if (GlGpuTimerScopes.IsCpuDetail(scope))
        {
            if (_activeDetailScope >= 0)
            {
                return false;
            }

            _activeDetailScope = (int)scope;
            _detailStartTimestamp = Stopwatch.GetTimestamp();
            return true;
        }

        if (_activeScope >= 0)
        {
            return false;
        }

        _activeScope = (int)scope;
        _scopeStartTimestamp = Stopwatch.GetTimestamp();
        return true;
    }

    public void EndScope(GlGpuTimerScope scope)
    {
        if (!_frameActive)
        {
            return;
        }

        if (GlGpuTimerScopes.IsCpuDetail(scope))
        {
            if (_activeDetailScope != (int)scope)
            {
                return;
            }

            EndActiveDetailScope();
            return;
        }

        if (_activeScope != (int)scope)
        {
            return;
        }

        // Close an open detail scope first so pass totals stay coherent.
        if (_activeDetailScope >= 0)
        {
            EndActiveDetailScope();
        }

        EndActiveScope();
    }

    public void EndFrame()
    {
        if (!_frameActive)
        {
            return;
        }

        if (_activeDetailScope >= 0)
        {
            EndActiveDetailScope();
        }

        if (_activeScope >= 0)
        {
            EndActiveScope();
        }

        _frameActive = false;
        _latest = new GlGpuTimingSnapshot(
            _elapsedMs[(int)GlGpuTimerScope.Setup],
            _elapsedMs[(int)GlGpuTimerScope.Shadow],
            _elapsedMs[(int)GlGpuTimerScope.Scene],
            _elapsedMs[(int)GlGpuTimerScope.Post],
            _elapsedMs[(int)GlGpuTimerScope.Overlay],
            _elapsedMs[(int)GlGpuTimerScope.CloudTrace],
            _elapsedMs[(int)GlGpuTimerScope.CloudTemporal],
            _elapsedMs[(int)GlGpuTimerScope.CloudUpsample],
            _elapsedMs[(int)GlGpuTimerScope.GodRayInject],
            _elapsedMs[(int)GlGpuTimerScope.GodRayIntegrate],
            _elapsedMs[(int)GlGpuTimerScope.GodRayResolve],
            _elapsedMs[(int)GlGpuTimerScope.Taa],
            _elapsedMs[(int)GlGpuTimerScope.DepthPrepass],
            _elapsedMs[(int)GlGpuTimerScope.HiZ],
            _elapsedMs[(int)GlGpuTimerScope.SetupBones],
            _elapsedMs[(int)GlGpuTimerScope.SetupBounds],
            _elapsedMs[(int)GlGpuTimerScope.ShadowTerrainCull],
            _elapsedMs[(int)GlGpuTimerScope.TerrainStream],
            _elapsedMs[(int)GlGpuTimerScope.TerrainDraw],
            _elapsedMs[(int)GlGpuTimerScope.SubjectDraw]);
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

    private void EndActiveScope()
    {
        if (_activeScope < 0)
        {
            return;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - _scopeStartTimestamp;
        _elapsedMs[_activeScope] += elapsedTicks * 1000.0 / Stopwatch.Frequency;
        _activeScope = -1;
    }

    private void EndActiveDetailScope()
    {
        if (_activeDetailScope < 0)
        {
            return;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - _detailStartTimestamp;
        _elapsedMs[_activeDetailScope] += elapsedTicks * 1000.0 / Stopwatch.Frequency;
        _activeDetailScope = -1;
    }
}
