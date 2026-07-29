using Silk.NET.OpenGL;

namespace AutoPBR.App.Rendering.OpenGL;

public sealed partial class OpenGlPreviewBackend
{
    private readonly struct GpuTimerScopeLease(GlGpuTimerProfiler? profiler, GlGpuTimerScope scope) : IDisposable
    {
        public void Dispose() => profiler?.EndScope(scope);
    }

    private readonly struct CpuTimerScopeLease(GlCpuTimerProfiler? profiler, GlGpuTimerScope scope) : IDisposable
    {
        public void Dispose() => profiler?.EndScope(scope);
    }

    private readonly struct PassTimerScopeLease(GpuTimerScopeLease gpu, CpuTimerScopeLease cpu) : IDisposable
    {
        public void Dispose()
        {
            gpu.Dispose();
            cpu.Dispose();
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

        if (!GlTimingHudPublishGate.ShouldPublish(ref _lastCpuTimingHudPublishSeconds, renderTimeSeconds))
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

            string cloudWindow;
            lock (_sync)
            {
                _latestGpuTimingSnapshot = snapshot;
                _gpuTimingSnapshotSequence++;
                _gpuTimingWindow.Add(snapshot);
                cloudWindow = _gpuTimingWindow.Count >= 8
                    ? "; " + _gpuTimingWindow.FormatCloudDiagnostic()
                    : string.Empty;
            }

            if (GlTimingHudPublishGate.ShouldPublish(ref _lastGpuTimingHudPublishSeconds, renderTimeSeconds))
            {
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
            }

            if (_settings.LogGpuPassTimings &&
                renderTimeSeconds - _lastGpuTimingDiagnosticSeconds >= 2.0)
            {
                _lastGpuTimingDiagnosticSeconds = renderTimeSeconds;
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
            if (text is null)
            {
                _latestGpuTimingHudText = null;
                _lastGpuTimingHudPublishSeconds = double.NegativeInfinity;
                return;
            }

            if (string.Equals(_latestGpuTimingHudText, text, StringComparison.Ordinal))
            {
                return;
            }

            _latestGpuTimingHudText = text;
        }
    }

    private void SetLatestCpuTimingHudText(string? text)
    {
        lock (_sync)
        {
            if (text is null)
            {
                _latestCpuTimingHudText = null;
                _lastCpuTimingHudPublishSeconds = double.NegativeInfinity;
                return;
            }

            if (string.Equals(_latestCpuTimingHudText, text, StringComparison.Ordinal))
            {
                return;
            }

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
        _latestGpuTimingSnapshot = null;
        _gpuTimingHudLinger.Reset();
    }

    private void AbandonGpuTimerProfiler()
    {
        _gpuTimerProfiler = null;
        _latestGpuTimingHudText = null;
        _latestGpuTimingSnapshot = null;
        _cpuTimerProfiler = null;
        _latestCpuTimingHudText = null;
        _lastGpuTimingHudPublishSeconds = double.NegativeInfinity;
        _lastCpuTimingHudPublishSeconds = double.NegativeInfinity;
        _gpuTimingHudLinger.Reset();
        _cpuTimingHudLinger.Reset();
    }
}
