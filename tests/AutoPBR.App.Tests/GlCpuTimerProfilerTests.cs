using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlCpuTimerProfilerTests
{
    [Fact]
    public void EndFrame_ReportsScopedWallClockMs()
    {
        var profiler = new GlCpuTimerProfiler();
        profiler.BeginFrame();
        Assert.True(profiler.TryBeginScope(GlGpuTimerScope.Setup));
        Thread.Sleep(2);
        profiler.EndScope(GlGpuTimerScope.Setup);
        Assert.True(profiler.TryBeginScope(GlGpuTimerScope.Scene));
        Thread.Sleep(1);
        profiler.EndScope(GlGpuTimerScope.Scene);
        profiler.EndFrame();

        Assert.True(profiler.TryTakeLatestSnapshot(out var snapshot));
        Assert.True(snapshot.SetupMs >= 1.0, $"SetupMs={snapshot.SetupMs}");
        Assert.True(snapshot.SceneMs >= 0.5, $"SceneMs={snapshot.SceneMs}");
        Assert.Equal(0.0, snapshot.ShadowMs);
        Assert.Contains("CPU ", snapshot.FormatHudLine("CPU"), StringComparison.Ordinal);
    }

    [Fact]
    public void DetailScopes_NestUnderOpenPassWithoutStoppingPassClock()
    {
        var profiler = new GlCpuTimerProfiler();
        profiler.BeginFrame();
        Assert.True(profiler.TryBeginScope(GlGpuTimerScope.Scene));
        Assert.True(profiler.TryBeginScope(GlGpuTimerScope.TerrainStream));
        Thread.Sleep(2);
        profiler.EndScope(GlGpuTimerScope.TerrainStream);
        Assert.True(profiler.TryBeginScope(GlGpuTimerScope.SubjectDraw));
        Thread.Sleep(2);
        profiler.EndScope(GlGpuTimerScope.SubjectDraw);
        Thread.Sleep(1);
        profiler.EndScope(GlGpuTimerScope.Scene);
        profiler.EndFrame();

        Assert.True(profiler.TryTakeLatestSnapshot(out var snapshot));
        Assert.True(snapshot.SceneMs >= 4.0, $"SceneMs={snapshot.SceneMs}");
        Assert.True(snapshot.TerrainStreamMs >= 1.0, $"TerrainStreamMs={snapshot.TerrainStreamMs}");
        Assert.True(snapshot.SubjectDrawMs >= 1.0, $"SubjectDrawMs={snapshot.SubjectDrawMs}");
        // Detail is nested wall time, not subtracted from Scene.
        Assert.True(snapshot.SceneMs >= snapshot.TerrainStreamMs + snapshot.SubjectDrawMs - 0.5);
        var hud = snapshot.FormatHudLine("CPU", expanded: true);
        Assert.Contains("  Terrain Stream", hud, StringComparison.Ordinal);
        Assert.Contains("  Subject", hud, StringComparison.Ordinal);
    }
}
