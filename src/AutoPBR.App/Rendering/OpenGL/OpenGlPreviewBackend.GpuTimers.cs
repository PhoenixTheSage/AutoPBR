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

    private readonly struct CpuTimerScopeLease : IDisposable
    {
        private readonly GlCpuTimerProfiler? _profiler;
        private readonly GlGpuTimerScope _scope;

        public CpuTimerScopeLease(GlCpuTimerProfiler? profiler, GlGpuTimerScope scope)
        {
            _profiler = profiler;
            _scope = scope;
        }

        public void Dispose() => _profiler?.EndScope(_scope);
    }

    private readonly struct PassTimerScopeLease : IDisposable
    {
        private readonly GpuTimerScopeLease _gpu;
        private readonly CpuTimerScopeLease _cpu;

        public PassTimerScopeLease(GpuTimerScopeLease gpu, CpuTimerScopeLease cpu)
        {
            _gpu = gpu;
            _cpu = cpu;
        }

        public void Dispose()
        {
            _gpu.Dispose();
            _cpu.Dispose();
        }
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

    private void BeginCpuTimerFrame()
    {
        _cpuTimerProfiler ??= new GlCpuTimerProfiler();
        _cpuTimerProfiler.BeginFrame();
    }

    private PassTimerScopeLease BeginPassTimerScope(GlGpuTimerScope scope) =>
        new(BeginGpuTimerScope(scope), BeginCpuTimerScope(scope));

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

    private CpuTimerScopeLease BeginCpuTimerScope(GlGpuTimerScope scope)
    {
        var profiler = _cpuTimerProfiler;
        if (profiler is null)
        {
            return default;
        }

        return profiler.TryBeginScope(scope)
            ? new CpuTimerScopeLease(profiler, scope)
            : default;
    }

    private void EndPassTimerFrame(double renderTimeSeconds)
    {
        EndCpuTimerFrame(renderTimeSeconds);
        EndGpuTimerFrame(renderTimeSeconds);
    }

    private void EndCpuTimerFrame(double renderTimeSeconds)
    {
        var profiler = _cpuTimerProfiler;
        if (profiler is null)
        {
            return;
        }

        profiler.EndFrame();
        if (!profiler.TryTakeLatestSnapshot(out var snapshot))
        {
            return;
        }

        var expanded = _settings.ShowExpandedGpuTimingHud;
        SetLatestCpuTimingHudText(
            snapshot.FormatHudLine(
                "CPU",
                expanded,
                expanded ? _cpuTimingHudLinger : null,
                renderTimeSeconds));
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
            var expanded = _settings.ShowExpandedGpuTimingHud;
            var hud = snapshot.FormatHudLine(
                "GPU",
                expanded,
                expanded ? _gpuTimingHudLinger : null,
                renderTimeSeconds);
            if (!string.IsNullOrEmpty(_latestOcclusionDebugHudText))
            {
                hud += "\n" + _latestOcclusionDebugHudText;
            }

            SetLatestGpuTimingHudText(hud);
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

    private void SetLatestCpuTimingHudText(string? text)
    {
        lock (_sync)
        {
            _latestCpuTimingHudText = text;
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
        _gpuTimingHudLinger.Reset();
    }

    private void AbandonGpuTimerProfiler()
    {
        _gpuTimerProfiler = null;
        _latestGpuTimingHudText = null;
        _cpuTimerProfiler = null;
        _latestCpuTimingHudText = null;
        _gpuTimingHudLinger.Reset();
        _cpuTimingHudLinger.Reset();
    }
}
