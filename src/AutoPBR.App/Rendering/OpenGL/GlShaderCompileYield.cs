namespace AutoPBR.App.Rendering.OpenGL;

/// <summary>
/// Optional yield between GL shader compiles during WGL GPU bootstrap so the driver and OS
/// can service other apps. Enabled only while the sidecar bootstrap loop is advancing.
/// </summary>
internal static class GlShaderCompileYield
{
    /// <summary>Milliseconds to sleep after each compile/link while enabled (0 = yield only).</summary>
    /// <remarks>Non-const so the Yield branch stays reachable for tuning without CS0162.</remarks>
    public static int SleepMilliseconds { get; set; } = 3;

    private static int _enabled;

    public static bool Enabled => Volatile.Read(ref _enabled) != 0;

    public static void SetEnabled(bool enabled) =>
        Volatile.Write(ref _enabled, enabled ? 1 : 0);

    public static void AfterCompile()
    {
        if (!Enabled)
        {
            return;
        }

        var sleepMs = SleepMilliseconds;
        if (sleepMs > 0)
        {
            Thread.Sleep(sleepMs);
        }
        else
        {
            Thread.Yield();
        }
    }
}
