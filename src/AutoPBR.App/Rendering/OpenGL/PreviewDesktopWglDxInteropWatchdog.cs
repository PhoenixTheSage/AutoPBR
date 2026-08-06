using System.Diagnostics;
using System.Text;

namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Cross-thread hang detector for D3D11/WGL async present. Logs from a background timer when
/// present/WGL heartbeats stall — the hung render thread cannot log itself.
/// </summary>
internal sealed class PreviewDesktopWglDxInteropWatchdog : IDisposable
{
    private readonly Action<string> _log;
    private readonly Func<string> _snapshot;
    private readonly Func<bool> _isWglWedged;
    private readonly Action? _onWglWedged;
    private readonly Action? _onPresentIdle;
    private readonly Timer _timer;
    private int _disposed;
    private int _stallLogged;

    public PreviewDesktopWglDxInteropWatchdog(
        Action<string> log,
        Func<string> snapshot,
        Func<bool> isWglWedged,
        Action? onWglWedged = null,
        Action? onPresentIdle = null,
        int periodMs = 500)
    {
        _log = log;
        _snapshot = snapshot;
        _isWglWedged = isWglWedged;
        _onWglWedged = onWglWedged;
        _onPresentIdle = onPresentIdle;
        _timer = new Timer(OnTick, null, periodMs, periodMs);
    }

    public long LastPresentHeartbeatTicks;
    public long LastWglHeartbeatTicks;
    public long AngleMutexWaitStartTicks;
    public int AngleMutexWaitFront = -1;

    public void NotePresentHeartbeat() =>
        Interlocked.Exchange(ref LastPresentHeartbeatTicks, Stopwatch.GetTimestamp());

    public void NoteWglHeartbeat() =>
        Interlocked.Exchange(ref LastWglHeartbeatTicks, Stopwatch.GetTimestamp());

    public void BeginAngleMutexWait(int front)
    {
        Volatile.Write(ref AngleMutexWaitFront, front);
        Interlocked.Exchange(ref AngleMutexWaitStartTicks, Stopwatch.GetTimestamp());
    }

    public void EndAngleMutexWait()
    {
        Interlocked.Exchange(ref AngleMutexWaitStartTicks, 0);
        Volatile.Write(ref AngleMutexWaitFront, -1);
    }

    private void OnTick(object? _)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var freq = Stopwatch.Frequency;
        var presentAgeMs = AgeMs(now, Volatile.Read(ref LastPresentHeartbeatTicks), freq);
        var wglAgeMs = AgeMs(now, Volatile.Read(ref LastWglHeartbeatTicks), freq);
        var mutexWaitStart = Volatile.Read(ref AngleMutexWaitStartTicks);
        var mutexWaitMs = mutexWaitStart == 0 ? 0 : AgeMs(now, mutexWaitStart, freq);

        var presentStalled = presentAgeMs > 2000;
        var mutexStalled = mutexWaitMs > 500;
        bool wglWedged;
        try
        {
            wglWedged = _isWglWedged();
        }
        catch
        {
            wglWedged = false;
        }

        // WGL heartbeat alone is not a hang if the owner is idle — present may simply have
        // stopped requesting frames (NeedsContinuousRendering false, compositor idle, etc.).
        var wglHeartbeatStaleWhileBusy = wglAgeMs > 3000 && wglWedged;

        if (!presentStalled && !mutexStalled && !wglHeartbeatStaleWhileBusy)
        {
            Interlocked.Exchange(ref _stallLogged, 0);
            return;
        }

        if (Interlocked.CompareExchange(ref _stallLogged, 1, 0) != 0)
        {
            return;
        }

        var sb = new StringBuilder(512);
        sb.Append("[3D preview] DX interop HANG watchdog: ");
        if (wglWedged)
        {
            sb.Append("WGL owner wedged; ");
        }

        if (mutexStalled)
        {
            sb.Append($"ANGLE keyed-mutex wait {mutexWaitMs:0}ms (front={Volatile.Read(ref AngleMutexWaitFront)}); ");
        }

        if (presentStalled)
        {
            sb.Append(wglWedged
                ? $"present heartbeat stale {presentAgeMs:0}ms; "
                : $"present loop idle {presentAgeMs:0}ms (WGL idle — not disabling interop); ");
        }

        if (wglHeartbeatStaleWhileBusy)
        {
            sb.Append($"WGL heartbeat stale {wglAgeMs:0}ms while busy; ");
        }

        try
        {
            sb.Append(_snapshot());
        }
        catch (Exception ex)
        {
            sb.Append($"snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }

        var message = sb.ToString();
        try
        {
            Services.LogService.Write(Services.AppLogCategory.PreviewGl, "[DX hang] " + message);
            Services.LogService.AppendEmergencyDiagnostic("D3D11/WGL interop hang", message);
            // Keep TEMP copy for quick debugger pickup when AppData is locked/unavailable.
            var path = Path.Combine(Path.GetTempPath(), "autopbr-dx-interop-hang.log");
            File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore file logging failures
        }

        try
        {
            _log(message);
        }
        catch
        {
            Debug.WriteLine(message);
        }

        try
        {
            if (wglWedged || wglHeartbeatStaleWhileBusy)
            {
                _onWglWedged?.Invoke();
            }
            else if (presentStalled)
            {
                _onPresentIdle?.Invoke();
            }
        }
        catch
        {
            // ignore recovery failures
        }
    }

    private static double AgeMs(long now, long then, long freq) =>
        then == 0 ? double.PositiveInfinity : (now - then) * 1000.0 / freq;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _timer.Dispose();
    }
}
