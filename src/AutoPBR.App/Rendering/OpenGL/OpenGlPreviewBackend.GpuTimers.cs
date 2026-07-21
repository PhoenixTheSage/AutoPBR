using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private readonly struct GpuTimerScopeLease : IDisposable
    {
        private readonly GlGpuTimerProfiler? _profiler;
        private readonly GlGpuTimerScope _scope;

        public GpuTimerScopeLease(GlGpuTimerProfiler? profiler, GlGpuTimerScope scope)
        {
            _profiler = profiler;
            _scope = scope;
        }

        public void Dispose() => _profiler?.EndScope(_scope);
    }

    private bool BeginGpuTimerFrame(GL gl)
    {
        if (_glCapabilities?.CanUseGpuTimerQueries != true)
        {
            DisposeGpuTimerProfiler();
            SetLatestGpuTimingHudText(null);
            return false;
        }

        try
        {
            _gpuTimerProfiler ??= new GlGpuTimerProfiler(gl);
            if (!_loggedGpuTimerProfilerActive)
            {
                _loggedGpuTimerProfilerActive = true;
                EmitDiagnostic("[3D preview] P8 GPU timer queries active for pass-scope profiling.");
            }

            return _gpuTimerProfiler.BeginFrame();
        }
        catch (Exception ex)
        {
            DisableGpuTimerProfiler($"timer query init failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private GpuTimerScopeLease BeginGpuTimerScope(GlGpuTimerScope scope)
    {
        var profiler = _gpuTimerProfiler;
        if (profiler is null)
        {
            return default;
        }

        try
        {
            return profiler.TryBeginScope(scope)
                ? new GpuTimerScopeLease(profiler, scope)
                : default;
        }
        catch (Exception ex)
        {
            DisableGpuTimerProfiler($"timer query begin failed: {ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    private void EndGpuTimerFrame(double renderTimeSeconds)
    {
        var profiler = _gpuTimerProfiler;
        if (profiler is null)
        {
            return;
        }

        try
        {
            profiler.EndFrame();
            if (!profiler.TryTakeLatestSnapshot(out var snapshot))
            {
                return;
            }

            _gpuTimingWindow.Add(snapshot);
            SetLatestGpuTimingHudText(snapshot.FormatHudLine(_settings.ShowExpandedGpuTimingHud));
            if (_settings.LogGpuPassTimings &&
                renderTimeSeconds - _lastGpuTimingDiagnosticSeconds >= 2.0)
            {
                _lastGpuTimingDiagnosticSeconds = renderTimeSeconds;
                var cloudWindow = _gpuTimingWindow.Count >= 8
                    ? "; " + _gpuTimingWindow.FormatCloudDiagnostic()
                    : string.Empty;
                EmitDiagnostic("[3D preview] P8 GPU timings: " + snapshot.FormatDiagnostic() + cloudWindow + ".");
            }
        }
        catch (Exception ex)
        {
            DisableGpuTimerProfiler($"timer query readback failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SetLatestGpuTimingHudText(string? text)
    {
        lock (_sync)
        {
            _latestGpuTimingHudText = text;
        }
    }

    private void DisableGpuTimerProfiler(string reason)
    {
        DisposeGpuTimerProfiler();
        SetLatestGpuTimingHudText(null);
        if (!_loggedGpuTimerProfilerFallback)
        {
            _loggedGpuTimerProfilerFallback = true;
            EmitDiagnostic("[3D preview] P8 GPU timer queries disabled; keeping untimed fallback path (" + reason + ").");
        }
    }

    private void DisposeGpuTimerProfiler()
    {
        _gpuTimerProfiler?.Dispose();
        _gpuTimerProfiler = null;
    }

    private void AbandonGpuTimerProfiler()
    {
        _gpuTimerProfiler = null;
        _latestGpuTimingHudText = null;
    }
}
