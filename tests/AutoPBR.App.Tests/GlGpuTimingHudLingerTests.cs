using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlGpuTimingHudLingerTests
{
    [Fact]
    public void ShouldShow_TrueWhileAtOrAboveThreshold()
    {
        var linger = new GlGpuTimingHudLinger();
        Assert.True(linger.ShouldShow(passId: 7, ms: GlGpuTimingHudLinger.MinDisplayMs, nowSeconds: 1.0));
        Assert.True(linger.ShouldShow(passId: 7, ms: 1.0, nowSeconds: 1.1));
    }

    [Fact]
    public void ShouldShow_FalseImmediatelyWhenNeverAboveThreshold()
    {
        var linger = new GlGpuTimingHudLinger();
        Assert.False(linger.ShouldShow(passId: 7, ms: 0.01, nowSeconds: 1.0));
    }

    [Fact]
    public void ShouldShow_HoldsVisibilityForHideDelayAfterDroppingBelowThreshold()
    {
        var linger = new GlGpuTimingHudLinger();
        Assert.True(linger.ShouldShow(passId: 7, ms: 0.2, nowSeconds: 5.0));

        Assert.True(
            linger.ShouldShow(passId: 7, ms: 0.0, nowSeconds: 5.0 + GlGpuTimingHudLinger.HideDelaySeconds - 0.01));
        Assert.False(
            linger.ShouldShow(passId: 7, ms: 0.0, nowSeconds: 5.0 + GlGpuTimingHudLinger.HideDelaySeconds));
    }

    [Fact]
    public void ShouldShow_RetriggerExtendsLingerWindow()
    {
        var linger = new GlGpuTimingHudLinger();
        Assert.True(linger.ShouldShow(passId: 4, ms: 0.2, nowSeconds: 1.0));
        Assert.True(linger.ShouldShow(passId: 4, ms: 0.0, nowSeconds: 2.0));
        Assert.True(linger.ShouldShow(passId: 4, ms: 0.2, nowSeconds: 2.4));
        Assert.True(
            linger.ShouldShow(passId: 4, ms: 0.0, nowSeconds: 2.4 + GlGpuTimingHudLinger.HideDelaySeconds - 0.01));
        Assert.False(
            linger.ShouldShow(passId: 4, ms: 0.0, nowSeconds: 2.4 + GlGpuTimingHudLinger.HideDelaySeconds));
    }

    [Fact]
    public void Reset_ClearsPendingLinger()
    {
        var linger = new GlGpuTimingHudLinger();
        Assert.True(linger.ShouldShow(passId: 3, ms: 0.5, nowSeconds: 1.0));
        linger.Reset();
        Assert.False(linger.ShouldShow(passId: 3, ms: 0.0, nowSeconds: 1.1));
    }
}
