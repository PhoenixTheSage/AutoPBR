using AutoPBR.App.Rendering.OpenGL;

namespace AutoPBR.App.Tests;

public sealed class GlTimingHudPublishGateTests
{
    [Fact]
    public void ShouldPublish_AllowsFirstSampleThenHoldsUntilInterval()
    {
        var last = double.NegativeInfinity;
        Assert.True(GlTimingHudPublishGate.ShouldPublish(ref last, 1.0));
        Assert.Equal(1.0, last);

        Assert.False(GlTimingHudPublishGate.ShouldPublish(ref last, 1.0 + GlTimingHudPublishGate.IntervalSeconds - 0.001));
        Assert.Equal(1.0, last);

        var afterInterval = 1.0 + GlTimingHudPublishGate.IntervalSeconds + 1e-9;
        Assert.True(GlTimingHudPublishGate.ShouldPublish(ref last, afterInterval));
        Assert.Equal(afterInterval, last);
    }
}
